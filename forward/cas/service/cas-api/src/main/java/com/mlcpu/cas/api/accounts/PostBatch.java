package com.mlcpu.cas.api.accounts;

import com.mlcpu.cas.api.IdempotencyKey;
import java.util.List;

/** Post a day's transactions, in the given order. */
public record PostBatch(IdempotencyKey key, List<TransactionInput> transactions) {
    public PostBatch {
        transactions = List.copyOf(transactions);
    }
}
