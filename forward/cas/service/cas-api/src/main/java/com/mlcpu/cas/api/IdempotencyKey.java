package com.mlcpu.cas.api;

/** Identifies one command. A command whose key was already completed is not executed again (SPEC.md IDEM-01). */
public record IdempotencyKey(String value) {
    public IdempotencyKey {
        if (value == null || value.isBlank()) throw new IllegalArgumentException("an idempotency key must not be blank");
    }
}
