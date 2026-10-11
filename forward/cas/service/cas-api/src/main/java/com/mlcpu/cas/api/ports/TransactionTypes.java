package com.mlcpu.cas.api.ports;

import java.util.Optional;

/** Transaction types by code. */
public interface TransactionTypes {
    Optional<TransactionTypeRecord> find(String typeCode);
}
