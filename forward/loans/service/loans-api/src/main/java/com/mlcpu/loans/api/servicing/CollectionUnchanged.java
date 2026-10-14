package com.mlcpu.loans.api.servicing;

/** Nothing to do: the line is already collected (paid again) or not collected (unpaid again). */
public record CollectionUnchanged(String loanId, int number) implements CollectionOutcome {
}
