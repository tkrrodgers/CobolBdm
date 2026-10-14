package com.mlcpu.loans.api.servicing;

/** A booked loan: its id, its account and its schedule's version. */
public record Booked(String loanId, String accountId, int scheduleVersion) implements BookingOutcome {
}
