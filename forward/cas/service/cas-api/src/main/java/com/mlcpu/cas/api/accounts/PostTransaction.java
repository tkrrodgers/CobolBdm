package com.mlcpu.cas.api.accounts;

import com.mlcpu.cas.api.IdempotencyKey;

/** Post one transaction in real time. */
public record PostTransaction(IdempotencyKey key, TransactionInput transaction) {}
