package com.mlcpu.cas.api.ports;

import java.util.List;

/** Every transaction the service has posted or generated. */
public interface Ledger {
    boolean contains(String transactionId);

    void append(LedgerEntry entry);

    /** Every entry, in the order appended. */
    List<LedgerEntry> all();
}
