package com.mlcpu.cas.api.customers;

import com.mlcpu.cas.api.IdempotencyKey;
import java.util.List;

/** Apply a batch of customer master changes, in the given order. */
public record ApplyCustomerChanges(IdempotencyKey key, List<CustomerChange> changes) {
    public ApplyCustomerChanges {
        changes = List.copyOf(changes);
    }
}
