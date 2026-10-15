package com.mlcpu.loans.acceptance.memory;

import com.mlcpu.loans.api.ports.AccountTransaction;
import com.mlcpu.loans.api.ports.AccountTransactionType;
import com.mlcpu.loans.api.ports.Billing;
import com.mlcpu.loans.api.ports.Clock;
import com.mlcpu.loans.api.ports.DisbursementPayment;
import com.mlcpu.loans.api.ports.FinancialAccounts;
import com.mlcpu.loans.api.ports.Ids;
import com.mlcpu.loans.api.ports.Ledger;
import com.mlcpu.loans.api.ports.LineServicing;
import com.mlcpu.loans.api.ports.LoanAccount;
import com.mlcpu.loans.api.ports.LoanBook;
import com.mlcpu.loans.api.ports.LoanInvoice;
import com.mlcpu.loans.api.ports.LoanRecord;
import com.mlcpu.loans.api.ports.LoanStatus;
import com.mlcpu.loans.api.ports.LoansPorts;
import java.math.BigDecimal;
import java.time.LocalDate;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import java.util.TreeMap;

/**
 * In-memory ports for the servicing scenarios (one instance per scenario): a loan book, a ledger that knows one base currency and
 * the currencies with a rate to it, billing, financial accounts and a clock the scenario sets. New ids are sequential per kind
 * (PAY-0001, INV-0001, ACC-0001, TRN-0001); a loan's id is "LN-" + its external id. {@link #state()} is what a scenario's
 * "state" compares: the loans (without their schedule, and of their terms only the loan id they carry), payments, invoices,
 * accounts with their balance, and transactions.
 */
public final class InMemoryPorts {

    /** The scenario's starting point: the business date, the organization's base currency, the currencies with a rate to it. */
    public record Given(LocalDate today, String baseCurrency, List<String> ratesToBase) {
    }

    public record LoanState(String loanId, String externalId, String termsLoanId, String accountId, LoanStatus status, LocalDate bookedOn, int scheduleVersion,
            List<LineServicing> lines, String disbursementPaymentId, String disbursementTransactionId) {
    }

    public record Payment(String paymentId, DisbursementPayment payment) {
    }

    public record Invoice(String invoiceId, LoanInvoice invoice) {
    }

    public record Account(String accountId, LoanAccount account, BigDecimal balance) {
    }

    public record Transaction(String transactionId, AccountTransaction transaction, boolean cancelled) {
    }

    public record State(LocalDate today, List<LoanState> loans, List<Payment> payments, List<Invoice> invoices, List<Account> accounts,
            List<Transaction> transactions) {
    }

    private final Given given;
    private LocalDate today;
    private final Map<String, LoanRecord> loans = new TreeMap<>();
    private final List<Payment> payments = new ArrayList<>();
    private final List<Invoice> invoices = new ArrayList<>();
    private final Map<String, LoanAccount> accounts = new TreeMap<>();
    private final List<Transaction> transactions = new ArrayList<>();

    public InMemoryPorts(Given given) {
        this.given = given;
        this.today = given.today();
    }

    public void today(LocalDate date) {
        today = date;
    }

    public LoansPorts ports() {
        Ids ids = externalId -> "LN-" + externalId;
        LoanBook book = new LoanBook() {
            @Override
            public Optional<LoanRecord> find(String loanId) {
                return Optional.ofNullable(loans.get(loanId));
            }

            @Override
            public Optional<LoanRecord> findByInvoice(String invoiceId) {
                return loans.values().stream().filter(l -> l.lines().stream().anyMatch(x -> invoiceId.equals(x.invoiceId()))).findFirst();
            }

            @Override
            public List<LoanRecord> withStatus(LoanStatus status) {
                return loans.values().stream().filter(l -> l.status() == status).toList();
            }

            @Override
            public void save(LoanRecord loan) {
                loans.put(loan.loanId(), loan);
            }
        };
        Ledger ledger = new Ledger() {
            @Override
            public boolean postable(String currencyCode, String organizationPartyId) {
                return currencyCode.equals(given.baseCurrency()) || given.ratesToBase().contains(currencyCode);
            }

            @Override
            public String disbursementPayment(DisbursementPayment payment) {
                String id = String.format("PAY-%04d", payments.size() + 1);
                payments.add(new Payment(id, payment));
                return id;
            }
        };
        Billing billing = invoice -> {
            String id = String.format("INV-%04d", invoices.size() + 1);
            invoices.add(new Invoice(id, invoice));
            return id;
        };
        FinancialAccounts finAccounts = new FinancialAccounts() {
            @Override
            public String open(LoanAccount account) {
                String id = String.format("ACC-%04d", accounts.size() + 1);
                accounts.put(id, account);
                return id;
            }

            @Override
            public String post(AccountTransaction transaction) {
                if (!accounts.containsKey(transaction.accountId())) throw new IllegalArgumentException("no account " + transaction.accountId());
                String id = String.format("TRN-%04d", transactions.size() + 1);
                transactions.add(new Transaction(id, transaction, false));
                return id;
            }

            @Override
            public void cancel(String transactionId) {
                for (int i = 0; i < transactions.size(); i++) {
                    Transaction t = transactions.get(i);
                    if (t.transactionId().equals(transactionId)) {
                        transactions.set(i, new Transaction(t.transactionId(), t.transaction(), true));
                        return;
                    }
                }
                throw new IllegalArgumentException("no transaction " + transactionId);
            }
        };
        Clock clock = () -> today;
        return new LoansPorts(ids, book, ledger, billing, finAccounts, clock);
    }

    public State state() {
        List<LoanState> l = loans.values().stream().map(x -> new LoanState(x.loanId(), x.externalId(), x.terms().loanId(), x.accountId(), x.status(), x.bookedOn(),
                x.scheduleVersion(), x.lines(), x.disbursementPaymentId(), x.disbursementTransactionId())).toList();
        List<Account> a = new ArrayList<>();
        for (var e : accounts.entrySet()) {
            BigDecimal balance = null;
            for (Transaction t : transactions) {
                if (t.cancelled() || !t.transaction().accountId().equals(e.getKey())) continue;
                BigDecimal amount = t.transaction().type() == AccountTransactionType.WITHDRAWAL ? t.transaction().amount().negate() : t.transaction().amount();
                balance = balance == null ? amount : balance.add(amount);
            }
            a.add(new Account(e.getKey(), e.getValue(), balance == null ? BigDecimal.ZERO : balance));
        }
        return new State(today, l, List.copyOf(payments), List.copyOf(invoices), a, List.copyOf(transactions));
    }
}
