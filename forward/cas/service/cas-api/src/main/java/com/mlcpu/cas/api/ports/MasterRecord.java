package com.mlcpu.cas.api.ports;

/** A record of the customer master: a customer's profile (type "A") or one of its contacts (any other type). */
public sealed interface MasterRecord permits CustomerProfile, CustomerContact {
    /** 5 characters. */
    String customerId();

    /** 1 character. */
    String recordType();

    /** The 6-character key: customer id and record type. */
    default String key() {
        return customerId() + recordType();
    }
}
