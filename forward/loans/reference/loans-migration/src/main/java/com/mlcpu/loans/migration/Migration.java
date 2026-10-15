package com.mlcpu.loans.migration;

import com.mlcpu.loans.api.DisbursementLine;
import com.mlcpu.loans.api.DownPaymentLine;
import com.mlcpu.loans.api.InstallmentLine;
import com.mlcpu.loans.api.Rejected;
import com.mlcpu.loans.api.Schedule;
import com.mlcpu.loans.api.ScheduleLine;
import com.mlcpu.loans.api.ScheduleOutcome;
import com.mlcpu.loans.api.ScheduleRejection;
import com.mlcpu.loans.api.ScheduleService;
import com.mlcpu.loans.api.Scheduled;
import com.mlcpu.loans.api.Totals;
import com.mlcpu.loans.api.legacy.LegacyScheduleHeader;
import com.mlcpu.loans.api.legacy.LegacyScheduleLine;
import com.mlcpu.loans.api.migration.LegacyLoan;
import com.mlcpu.loans.api.migration.LegacyMigration;
import com.mlcpu.loans.api.migration.LegacyTerms;
import com.mlcpu.loans.api.migration.LegacyTermsOutcome;
import com.mlcpu.loans.api.migration.Reconciliation;
import com.mlcpu.loans.api.migration.ReconciliationOutcome;
import com.mlcpu.loans.api.migration.Unrepresentable;
import java.math.BigDecimal;
import java.time.LocalDate;
import java.util.ArrayList;
import java.util.List;
import java.util.Objects;

/** The legacy loan book's migration, engine side: LEG-02 (terms), LEG-03 (a scheduled legacy loan), LEG-04 (a legacy reject). */
public final class Migration implements LegacyMigration {

    private final ScheduleService quotes;
    private final TermsMapping mapping = new TermsMapping();

    public Migration(ScheduleService quotes) {
        this.quotes = quotes;
    }

    @Override
    public LegacyTermsOutcome terms(LegacyLoan loan) {
        return mapping.terms(loan);
    }

    @Override
    public Reconciliation reconcile(LegacyLoan loan) {
        String id = loan.request().loanId();
        String code = loan.reject() == null ? null : loan.reject().code();
        LegacyTermsOutcome t = terms(loan);
        if (t instanceof Unrepresentable u) {
            return new Reconciliation(id, ReconciliationOutcome.EXPLAINED, "D-5", u.field(), null, null, code, null);
        }
        ScheduleOutcome o = quotes.schedule(((LegacyTerms) t).terms());
        ScheduleRejection rejection = o instanceof Rejected r ? r.reason() : null;
        if (code != null) {
            return legacyReject(id, code, rejection);
        }
        if (loan.lines().stream().noneMatch(l -> "D".equals(l.recType()))) {
            // D-3: the legacy wrote an all-zero schedule without a disbursement line; the service rejects the request
            return rejection == ScheduleRejection.DISBURSEMENT_OUTSIDE_TERM
                    ? new Reconciliation(id, ReconciliationOutcome.EXPLAINED, "D-3", "outcome", null, null, null, rejection)
                    : diff(id, "outcome", "scheduled without a disbursement line", rejection == null ? "scheduled" : rejection.name(), rejection);
        }
        if (rejection != null) {
            return diff(id, "outcome", "scheduled", rejection.name(), rejection);
        }
        List<String[]> differences = compare(loan.header(), loan.lines(), ((Scheduled) o).schedule());
        if (differences.isEmpty()) {
            return new Reconciliation(id, ReconciliationOutcome.MATCH, null, null, null, null, null, null);
        }
        String[] first = differences.get(0);
        if (differences.stream().allMatch(d -> runningBalance(d[0], loan.lines())) && !continuous(loan.lines())) {
            return new Reconciliation(id, ReconciliationOutcome.EXPLAINED, "D-2", first[0], null, null, null, null);
        }
        return diff(id, first[0], first[1], first[2], null);
    }

    private static Reconciliation legacyReject(String id, String code, ScheduleRejection rejection) {
        if (rejection != null && (code.equals(rejection.legacyCode()) || code.startsWith("RJENV") && rejection == ScheduleRejection.OUT_OF_RANGE)) {
            return new Reconciliation(id, ReconciliationOutcome.LEGACY_REJECT, null, null, null, null, code, rejection);
        }
        if (rejection == null && code.startsWith("RJENV")) {
            return new Reconciliation(id, ReconciliationOutcome.EXPLAINED, "D-6", "outcome", null, null, code, null);
        }
        return new Reconciliation(id, ReconciliationOutcome.DIFF, null, "outcome", code, rejection == null ? "scheduled" : rejection.name(), code,
                rejection);
    }

    private static Reconciliation diff(String id, String field, String legacy, String service, ScheduleRejection rejection) {
        return new Reconciliation(id, ReconciliationOutcome.DIFF, null, field, legacy, service, null, rejection);
    }

    /** Every difference, in the digest's order: field name, legacy value, service value. */
    private static List<String[]> compare(LegacyScheduleHeader h, List<LegacyScheduleLine> lines, Schedule s) {
        List<String[]> d = new ArrayList<>();
        same(d, "currencyCode", h.currencyCode(), s.currencyCode());
        same(d, "decimalPlaces", String.valueOf(h.decimalPlaces()), String.valueOf(s.decimalPlaces()));
        same(d, "termDays", String.valueOf(h.termDays()), String.valueOf(s.termDays()));
        Totals t = s.totals();
        amount(d, "totals.disbursed", h.totDisbursed(), t.disbursed());
        amount(d, "totals.principal", h.totPrincipal(), t.principal());
        amount(d, "totals.interest", h.totInterest(), t.interest());
        amount(d, "totals.fee", h.totFee(), t.fee());
        amount(d, "totals.penalty", h.totPenalty(), t.penalty());
        amount(d, "totals.repayment", h.totRepayment(), t.repayment());
        amount(d, "totals.outstanding", h.totOutstanding(), t.outstanding());
        if (lines.size() != s.lines().size()) {
            d.add(new String[] {"lines", String.valueOf(lines.size()), String.valueOf(s.lines().size())});
            return d;
        }
        for (int i = 0; i < lines.size(); i++) {
            LegacyScheduleLine l = lines.get(i);
            ScheduleLine x = s.lines().get(i);
            String at = "line " + (i + 1) + " ";
            String kind = x instanceof DisbursementLine ? "D" : x instanceof DownPaymentLine ? "P" : "R";
            if (!kind.equals(l.recType())) {
                d.add(new String[] {at + "kind", l.recType(), kind});
                continue;
            }
            if (x instanceof DisbursementLine dl) {
                date(d, at + "from", l.fromDate(), dl.from());
                date(d, at + "due", l.dueDate(), dl.due());
                amount(d, at + "amount", l.principal(), dl.amount());
                amount(d, at + "balanceAfter", l.outstBalance(), dl.balanceAfter());
            } else if (x instanceof DownPaymentLine p) {
                same(d, at + "number", String.valueOf(l.periodNumber()), String.valueOf(p.number()));
                date(d, at + "from", l.fromDate(), p.from());
                date(d, at + "due", l.dueDate(), p.due());
                amount(d, at + "principal", l.principal(), p.principal());
                amount(d, at + "totalDue", l.totalDue(), p.totalDue());
                amount(d, at + "balanceAfter", l.outstBalance(), p.balanceAfter());
                amount(d, at + "remainingDue", l.totOutstanding(), p.remainingDue());
            } else {
                InstallmentLine r = (InstallmentLine) x;
                same(d, at + "number", String.valueOf(l.periodNumber()), String.valueOf(r.number()));
                date(d, at + "from", l.fromDate(), r.from());
                date(d, at + "due", l.dueDate(), r.due());
                amount(d, at + "principal", l.principal(), r.principal());
                amount(d, at + "interest", l.interest(), r.interest());
                amount(d, at + "fee", l.fee(), r.fee());
                amount(d, at + "penalty", l.penalty(), r.penalty());
                amount(d, at + "totalDue", l.totalDue(), r.totalDue());
                amount(d, at + "balanceAfter", l.outstBalance(), r.balanceAfter());
                amount(d, at + "remainingDue", l.totOutstanding(), r.remainingDue());
            }
        }
        return d;
    }

    /** Whether a field is the balance after a down payment or an installment line ("line 4 balanceAfter" of a P or R line). */
    private static boolean runningBalance(String field, List<LegacyScheduleLine> lines) {
        String[] parts = field.split(" ");
        if (parts.length != 3 || !"line".equals(parts[0]) || !"balanceAfter".equals(parts[2])) {
            return false;
        }
        return !"D".equals(lines.get(Integer.parseInt(parts[1]) - 1).recType());
    }

    /** The legacy's balances run on: each down payment or installment line's balance is the previous line's less its principal. */
    private static boolean continuous(List<LegacyScheduleLine> lines) {
        for (int i = 1; i < lines.size(); i++) {
            LegacyScheduleLine l = lines.get(i);
            if (!"D".equals(l.recType()) && lines.get(i - 1).outstBalance().subtract(l.principal()).compareTo(l.outstBalance()) != 0) {
                return false;
            }
        }
        return true;
    }

    private static void same(List<String[]> d, String field, String legacy, String service) {
        if (!Objects.equals(legacy, service)) {
            d.add(new String[] {field, legacy, service});
        }
    }

    private static void date(List<String[]> d, String field, LocalDate legacy, LocalDate service) {
        same(d, field, String.valueOf(legacy), String.valueOf(service));
    }

    private static void amount(List<String[]> d, String field, BigDecimal legacy, BigDecimal service) {
        if (legacy.compareTo(service) != 0) {
            d.add(new String[] {field, legacy.toPlainString(), service.toPlainString()});
        }
    }
}
