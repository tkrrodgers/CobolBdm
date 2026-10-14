package com.mlcpu.loans.api.servicing;

/** Disburse a booked loan, paid out with the given payment method. */
public record DisburseLoan(String loanId, String paymentMethodId) {
}
