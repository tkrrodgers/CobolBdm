package com.mlcpu.loans.api.ports;

/** The general ledger and payments. */
public interface Ledger {

    /** Amounts in this currency can be posted for the organization: it is the organization's base currency or has a rate to it. */
    boolean postable(String currencyCode, String organizationPartyId);

    /** Creates the disbursement payment (the ERP posts it) and returns its id. */
    String disbursementPayment(DisbursementPayment payment);
}
