package com.mlcpu.loans.api.ports;

/**
 * Everything the servicing use cases reach outside the service. The ERP's adapters implement them (OFBiz: entities, services and
 * the ledger); the acceptance tests use in-memory ones.
 */
public record LoansPorts(Ids ids, LoanBook book, Ledger ledger, Billing billing, FinancialAccounts accounts, Clock clock) {
}
