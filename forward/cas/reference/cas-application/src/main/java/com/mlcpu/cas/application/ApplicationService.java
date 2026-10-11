package com.mlcpu.cas.application;

import com.mlcpu.cas.accounts.InterestAccrual;
import com.mlcpu.cas.accounts.Posting;
import com.mlcpu.cas.api.CasPorts;
import com.mlcpu.cas.api.CustomerAccountService;
import com.mlcpu.cas.api.IdempotencyKey;
import com.mlcpu.cas.api.accounts.AccrueInterest;
import com.mlcpu.cas.api.accounts.BatchPostingResult;
import com.mlcpu.cas.api.accounts.InterestRunResult;
import com.mlcpu.cas.api.accounts.PostBatch;
import com.mlcpu.cas.api.accounts.PostTransaction;
import com.mlcpu.cas.api.accounts.PostingOutcome;
import com.mlcpu.cas.api.customers.ApplyCustomerChanges;
import com.mlcpu.cas.api.customers.CustomerChangeSummary;
import com.mlcpu.cas.api.reporting.TransactionReport;
import com.mlcpu.cas.api.reporting.TransactionReportQuery;
import com.mlcpu.cas.api.statements.CustomerStatement;
import com.mlcpu.cas.api.statements.StatementQuery;
import com.mlcpu.cas.customers.CustomerMaintenance;
import com.mlcpu.cas.customers.MonthlyAverage;
import com.mlcpu.cas.reporting.TransactionReporting;
import com.mlcpu.cas.statements.Statements;
import java.util.function.Supplier;

/** The use cases: each command runs once per idempotency key (IDEM-01, IDEM-02); queries go straight to their context. */
final class ApplicationService implements CustomerAccountService {
    private final CasPorts ports;
    private final Posting posting;
    private final InterestAccrual interest;
    private final CustomerMaintenance customers;
    private final TransactionReporting reporting;
    private final Statements statements;

    ApplicationService(CasPorts ports) {
        this.ports = ports;
        this.posting = new Posting(ports);
        this.interest = new InterestAccrual(ports);
        this.customers = new CustomerMaintenance(ports.customers());
        this.reporting = new TransactionReporting(ports);
        this.statements = new Statements(ports, MonthlyAverage::of);
    }

    @Override
    public PostingOutcome postTransaction(PostTransaction command) {
        return once(command.key(), PostingOutcome.class, () -> posting.post(command.transaction()));
    }

    @Override
    public BatchPostingResult postBatch(PostBatch command) {
        return once(command.key(), BatchPostingResult.class, () -> posting.postAll(command.transactions()));
    }

    @Override
    public InterestRunResult accrueInterest(AccrueInterest command) {
        return once(command.key(), InterestRunResult.class, () -> interest.accrue(command.cycle()));
    }

    @Override
    public CustomerChangeSummary applyCustomerChanges(ApplyCustomerChanges command) {
        return once(command.key(), CustomerChangeSummary.class, () -> customers.apply(command.changes()));
    }

    @Override
    public TransactionReport transactionReport(TransactionReportQuery query) {
        return reporting.report(query);
    }

    @Override
    public CustomerStatement customerStatement(StatementQuery query) {
        return statements.statement(query);
    }

    /** A completed key returns its recorded result; a new key runs and is recorded only when it completes. */
    private <T> T once(IdempotencyKey key, Class<T> type, Supplier<T> command) {
        var done = ports.journal().find(key);
        if (done.isPresent()) return type.cast(done.get());
        T result = command.get();
        ports.journal().record(key, result);
        return result;
    }
}
