package com.mlcpu.loans.api.servicing;

import com.mlcpu.loans.api.ScheduleRequest;

/**
 * Book a loan. {@code externalId} is the caller's id of the loan and the booking's idempotency key; the loan's id comes from it
 * (the {@code Ids} port). The terms' own {@code loanId} is not used.
 */
public record BookLoan(String externalId, String ownerPartyId, String organizationPartyId, String loanProductId, ScheduleRequest terms) {
}
