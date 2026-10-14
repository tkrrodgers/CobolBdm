package com.mlcpu.loans.api.servicing;

/** A disbursed loan: the payment, the account's withdrawal, and the down payment's invoice (null without a down payment). */
public record Disbursed(String loanId, String paymentId, String accountTransactionId, String downPaymentInvoiceId) implements DisbursementOutcome {
}
