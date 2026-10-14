package com.mlcpu.loans.api.ports;

/** A loan's account: whose it is, for which organization, in which currency. */
public record LoanAccount(String loanId, String ownerPartyId, String organizationPartyId, String currencyCode) {
}
