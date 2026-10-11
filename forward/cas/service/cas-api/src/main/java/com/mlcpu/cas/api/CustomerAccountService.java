package com.mlcpu.cas.api;

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

/**
 * The Customer Account Service's use cases (the inbound port). Implemented by the application module and obtained through
 * {@code com.mlcpu.cas.application.CasApplication.create(CasPorts)}. Commands carry an idempotency key: a replayed key changes
 * nothing and returns the first result (SPEC.md section 5).
 */
public interface CustomerAccountService {

    /** Validates and posts one card transaction in real time (rules POST). */
    PostingOutcome postTransaction(PostTransaction command);

    /** The nightly batch: each transaction in order, with the same rules as {@link #postTransaction} (rules POST). */
    BatchPostingResult postBatch(PostBatch command);

    /** Monthly interest for one cycle and the cycle close (rules INT); all-or-nothing on missing reference data. */
    InterestRunResult accrueInterest(AccrueInterest command);

    /** A batch of customer master changes in order (rules CUST, UPD). */
    CustomerChangeSummary applyCustomerChanges(ApplyCustomerChanges command);

    /** Ledger transactions processed in a date range, grouped by card, with totals (rules RPT). */
    TransactionReport transactionReport(TransactionReportQuery query);

    /** One SAM customer's statement for one cycle (rules STMT). */
    CustomerStatement customerStatement(StatementQuery query);
}
