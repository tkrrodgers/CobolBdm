package com.mlcpu.loans.application;

import com.mlcpu.loans.api.DisbursementLine;
import com.mlcpu.loans.api.DownPaymentLine;
import com.mlcpu.loans.api.InstallmentLine;
import com.mlcpu.loans.api.Rejected;
import com.mlcpu.loans.api.Schedule;
import com.mlcpu.loans.api.ScheduleLine;
import com.mlcpu.loans.api.ScheduleOutcome;
import com.mlcpu.loans.api.ScheduleRequest;
import com.mlcpu.loans.api.ScheduleService;
import com.mlcpu.loans.api.Scheduled;
import com.mlcpu.loans.api.ports.AccountTransaction;
import com.mlcpu.loans.api.ports.AccountTransactionType;
import com.mlcpu.loans.api.ports.DisbursementPayment;
import com.mlcpu.loans.api.ports.LineServicing;
import com.mlcpu.loans.api.ports.LoanAccount;
import com.mlcpu.loans.api.ports.LoanInvoice;
import com.mlcpu.loans.api.ports.LoanRecord;
import com.mlcpu.loans.api.ports.LoanStatus;
import com.mlcpu.loans.api.ports.LoansPorts;
import com.mlcpu.loans.api.servicing.BillDue;
import com.mlcpu.loans.api.servicing.BillingRun;
import com.mlcpu.loans.api.servicing.BookLoan;
import com.mlcpu.loans.api.servicing.Booked;
import com.mlcpu.loans.api.servicing.BookingOutcome;
import com.mlcpu.loans.api.servicing.BookingRejected;
import com.mlcpu.loans.api.servicing.BookingRejection;
import com.mlcpu.loans.api.servicing.CollectionCancelled;
import com.mlcpu.loans.api.servicing.CollectionOutcome;
import com.mlcpu.loans.api.servicing.CollectionUnchanged;
import com.mlcpu.loans.api.servicing.DisburseLoan;
import com.mlcpu.loans.api.servicing.Disbursed;
import com.mlcpu.loans.api.servicing.DisbursementOutcome;
import com.mlcpu.loans.api.servicing.InstallmentBilled;
import com.mlcpu.loans.api.servicing.InvoicePaid;
import com.mlcpu.loans.api.servicing.LoanServicing;
import com.mlcpu.loans.api.servicing.PrincipalCollected;
import com.mlcpu.loans.api.servicing.ServicingRejected;
import com.mlcpu.loans.api.servicing.ServicingRejection;
import java.math.BigDecimal;
import java.time.LocalDate;
import java.util.ArrayList;
import java.util.List;
import java.util.Optional;

/** The servicing use cases (rules SRV and IDEM) over the ports: book, disburse, bill, collect. */
final class Servicing implements LoanServicing {

    /** The ledger's capacity (D-11): amounts below 10^12, at most 2 decimal places; rates with at most 6 decimal places (D-12). */
    private static final BigDecimal LEDGER_LIMIT = BigDecimal.TEN.pow(12);
    private static final int LEDGER_SCALE = 2;
    private static final int LEDGER_RATE_SCALE = 6;

    private final ScheduleService quotes;
    private final LoansPorts ports;

    Servicing(ScheduleService quotes, LoansPorts ports) {
        this.quotes = quotes;
        this.ports = ports;
    }

    // ---------------------------------------------------------------- booking (SRV-01 to SRV-04, IDEM-01)

    @Override
    public BookingOutcome book(BookLoan command) {
        String loanId = ports.ids().loanId(command.externalId());
        Optional<LoanRecord> existing = ports.book().find(loanId);
        if (existing.isPresent()) {
            LoanRecord l = existing.get();
            return new Booked(l.loanId(), l.accountId(), l.scheduleVersion());
        }
        ScheduleRequest terms = withLoanId(command.terms(), loanId);
        ScheduleOutcome outcome = quotes.schedule(terms);
        if (outcome instanceof Rejected r) {
            return new BookingRejected(command.externalId(), BookingRejection.SCHEDULE_REJECTED, r.reason(), r.field());
        }
        Schedule schedule = ((Scheduled) outcome).schedule();
        BookingRejection ledger = ledgerLimit(command, terms, schedule);
        if (ledger != null) {
            return new BookingRejected(command.externalId(), ledger, null, null);
        }
        String accountId = ports.accounts().open(new LoanAccount(loanId, command.ownerPartyId(), command.organizationPartyId(), schedule.currencyCode()));
        ports.book().save(new LoanRecord(loanId, command.externalId(), command.ownerPartyId(), command.organizationPartyId(), command.loanProductId(),
                accountId, terms, LoanStatus.BOOKED, ports.clock().today(), 1, schedule, linesToBill(schedule), null, null));
        return new Booked(loanId, accountId, 1);
    }

    /** SRV-02: the ledger's limits, in this order. */
    private BookingRejection ledgerLimit(BookLoan command, ScheduleRequest terms, Schedule schedule) {
        if (schedule.decimalPlaces() > LEDGER_SCALE) {
            return BookingRejection.CURRENCY_SCALE_UNSUPPORTED_BY_LEDGER;
        }
        if (!ports.ledger().postable(schedule.currencyCode(), command.organizationPartyId())) {
            return BookingRejection.NO_EXCHANGE_RATE_TO_BASE_CURRENCY;
        }
        if (terms.annualRatePercent().stripTrailingZeros().scale() > LEDGER_RATE_SCALE) {
            return BookingRejection.RATE_PRECISION_UNSUPPORTED_BY_LEDGER;
        }
        for (BigDecimal amount : amounts(schedule)) {
            if (amount.abs().compareTo(LEDGER_LIMIT) >= 0) {
                return BookingRejection.AMOUNT_EXCEEDS_LEDGER_CAPACITY;
            }
        }
        return null;
    }

    private static List<BigDecimal> amounts(Schedule s) {
        List<BigDecimal> out = new ArrayList<>(List.of(s.totals().disbursed(), s.totals().principal(), s.totals().interest(), s.totals().repayment()));
        for (ScheduleLine l : s.lines()) {
            if (l instanceof DisbursementLine d) {
                out.add(d.amount());
                out.add(d.balanceAfter());
            } else if (l instanceof DownPaymentLine p) {
                out.add(p.totalDue());
                out.add(p.remainingDue());
            } else {
                InstallmentLine i = (InstallmentLine) l;
                out.add(i.totalDue());
                out.add(i.balanceAfter());
                out.add(i.remainingDue());
            }
        }
        return out;
    }

    /** SRV-03: the lines to bill, in the schedule's order: the down payment and every installment. */
    private static List<LineServicing> linesToBill(Schedule s) {
        List<LineServicing> lines = new ArrayList<>();
        for (ScheduleLine l : s.lines()) {
            if (l instanceof DownPaymentLine p) {
                lines.add(new LineServicing(p.number(), p.due(), p.principal(), BigDecimal.ZERO.setScale(s.decimalPlaces()), null, null, null));
            } else if (l instanceof InstallmentLine i) {
                lines.add(new LineServicing(i.number(), i.due(), i.principal(), i.interest(), null, null, null));
            }
        }
        return lines;
    }

    private static ScheduleRequest withLoanId(ScheduleRequest q, String loanId) {
        return new ScheduleRequest(loanId, q.scheduleStart(), q.currency(), q.principal(), q.disbursementDate(), q.installments(), q.every(), q.frequency(),
                q.annualRatePercent(), q.downPaymentPercent(), q.downPaymentEnabled(), q.monthDays(), q.yearDays(), q.fixedLength(), q.interestMethod(),
                q.fullTermTranche(), q.calculation());
    }

    // ---------------------------------------------------------------- disbursement (SRV-05, SRV-06, IDEM-02)

    @Override
    public DisbursementOutcome disburse(DisburseLoan command) {
        Optional<LoanRecord> found = ports.book().find(command.loanId());
        if (found.isEmpty()) {
            return new ServicingRejected(command.loanId(), ServicingRejection.UNKNOWN_LOAN);
        }
        LoanRecord l = found.get();
        if (l.status() != LoanStatus.BOOKED) {
            return new Disbursed(l.loanId(), l.disbursementPaymentId(), l.disbursementTransactionId(), downPaymentInvoice(l));
        }
        Schedule s = l.schedule();
        DisbursementLine d = (DisbursementLine) s.lines().stream().filter(x -> x instanceof DisbursementLine).findFirst().orElseThrow();
        String paymentId = ports.ledger().disbursementPayment(new DisbursementPayment(l.loanId(), l.loanProductId(), l.organizationPartyId(),
                l.ownerPartyId(), command.paymentMethodId(), s.currencyCode(), d.amount(), d.due()));
        String transactionId = ports.accounts().post(new AccountTransaction(l.accountId(), AccountTransactionType.WITHDRAWAL, d.amount(), d.due(), paymentId,
                null));
        List<LineServicing> lines = new ArrayList<>(l.lines());
        String downPaymentInvoice = null;
        for (ScheduleLine x : s.lines()) {
            if (x instanceof DownPaymentLine p && owed(lines.get(indexOf(lines, p.number()))).signum() != 0) {
                int k = indexOf(lines, p.number());
                downPaymentInvoice = bill(l, lines.get(k), d.due());
                lines.set(k, billed(lines.get(k), downPaymentInvoice, d.due()));
            }
        }
        ports.book().save(new LoanRecord(l.loanId(), l.externalId(), l.ownerPartyId(), l.organizationPartyId(), l.loanProductId(), l.accountId(), l.terms(),
                LoanStatus.ACTIVE, l.bookedOn(), l.scheduleVersion(), l.schedule(), lines, paymentId, transactionId));
        return new Disbursed(l.loanId(), paymentId, transactionId, downPaymentInvoice);
    }

    private static String downPaymentInvoice(LoanRecord l) {
        for (ScheduleLine x : l.schedule().lines()) {
            if (x instanceof DownPaymentLine p) {
                return l.lines().get(indexOf(l.lines(), p.number())).invoiceId();
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- billing (SRV-07, SRV-08, IDEM-03)

    @Override
    public BillingRun billDue(BillDue command) {
        List<InstallmentBilled> billed = new ArrayList<>();
        for (LoanRecord l : ports.book().withStatus(LoanStatus.ACTIVE)) {
            List<LineServicing> lines = new ArrayList<>(l.lines());
            boolean changed = false;
            for (int k = 0; k < lines.size(); k++) {
                LineServicing line = lines.get(k);
                if (line.invoiceId() != null || line.due().isAfter(command.asOf()) || owed(line).signum() == 0) {
                    continue;
                }
                String invoiceId = bill(l, line, line.due());
                lines.set(k, billed(line, invoiceId, command.asOf()));
                billed.add(new InstallmentBilled(l.loanId(), line.number(), invoiceId, line.principal(), line.interest()));
                changed = true;
            }
            if (changed) {
                ports.book().save(withLines(l, lines, l.status()));
            }
        }
        return new BillingRun(command.asOf(), billed);
    }

    private String bill(LoanRecord l, LineServicing line, LocalDate invoiceDate) {
        return ports.billing().invoice(new LoanInvoice(l.loanId(), l.loanProductId(), line.number(), l.organizationPartyId(), l.ownerPartyId(),
                l.schedule().currencyCode(), invoiceDate, line.principal(), line.interest()));
    }

    private static LineServicing billed(LineServicing line, String invoiceId, LocalDate on) {
        return new LineServicing(line.number(), line.due(), line.principal(), line.interest(), invoiceId, on, line.collectionTransactionId());
    }

    private static BigDecimal owed(LineServicing line) {
        return line.principal().add(line.interest());
    }

    // ---------------------------------------------------------------- collection (SRV-09 to SRV-11, IDEM-04)

    @Override
    public CollectionOutcome invoicePaid(InvoicePaid event) {
        Optional<LoanRecord> found = ports.book().findByInvoice(event.invoiceId());
        if (found.isEmpty()) {
            return new ServicingRejected(event.invoiceId(), ServicingRejection.UNKNOWN_INVOICE);
        }
        LoanRecord l = found.get();
        List<LineServicing> lines = new ArrayList<>(l.lines());
        int k = 0;
        while (!event.invoiceId().equals(lines.get(k).invoiceId())) {
            k++;
        }
        LineServicing line = lines.get(k);
        if (event.paid() == (line.collectionTransactionId() != null)) {
            return new CollectionUnchanged(l.loanId(), line.number());
        }
        if (!event.paid()) {
            ports.accounts().cancel(line.collectionTransactionId());
            lines.set(k, collected(line, null));
            ports.book().save(withLines(l, lines, LoanStatus.ACTIVE));
            return new CollectionCancelled(l.loanId(), line.number(), line.collectionTransactionId());
        }
        String transactionId = ports.accounts().post(new AccountTransaction(l.accountId(), AccountTransactionType.DEPOSIT, line.principal(),
                ports.clock().today(), null, event.invoiceId()));
        lines.set(k, collected(line, transactionId));
        boolean paidOff = lines.stream().allMatch(x -> x.collectionTransactionId() != null || owed(x).signum() == 0);
        ports.book().save(withLines(l, lines, paidOff ? LoanStatus.PAID_OFF : l.status()));
        return new PrincipalCollected(l.loanId(), line.number(), transactionId, paidOff);
    }

    private static LineServicing collected(LineServicing line, String transactionId) {
        return new LineServicing(line.number(), line.due(), line.principal(), line.interest(), line.invoiceId(), line.billedOn(), transactionId);
    }

    private static LoanRecord withLines(LoanRecord l, List<LineServicing> lines, LoanStatus status) {
        return new LoanRecord(l.loanId(), l.externalId(), l.ownerPartyId(), l.organizationPartyId(), l.loanProductId(), l.accountId(), l.terms(), status,
                l.bookedOn(), l.scheduleVersion(), l.schedule(), lines, l.disbursementPaymentId(), l.disbursementTransactionId());
    }

    private static int indexOf(List<LineServicing> lines, int number) {
        for (int k = 0; k < lines.size(); k++) {
            if (lines.get(k).number() == number) {
                return k;
            }
        }
        throw new IllegalStateException("no line " + number);
    }
}
