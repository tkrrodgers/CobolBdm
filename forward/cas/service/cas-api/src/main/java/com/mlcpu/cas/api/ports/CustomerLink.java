package com.mlcpu.cas.api.ports;

import java.util.List;

/** A SAM customer's CardDemo accounts. */
public record CustomerLink(String customerId, List<Long> accountIds) {
    public CustomerLink {
        accountIds = List.copyOf(accountIds);
    }
}
