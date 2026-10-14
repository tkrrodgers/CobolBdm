package com.mlcpu.loans.api.servicing;

/** A line's collection cancelled (its invoice is no longer paid): the deposit is cancelled and the loan is active again. */
public record CollectionCancelled(String loanId, int number, String accountTransactionId) implements CollectionOutcome {
}
