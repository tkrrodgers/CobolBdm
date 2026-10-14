package com.mlcpu.loans.api.servicing;

/** The principal of a paid line deposited into the loan's account; {@code paidOff} when that was the last line to collect. */
public record PrincipalCollected(String loanId, int number, String accountTransactionId, boolean paidOff) implements CollectionOutcome {
}
