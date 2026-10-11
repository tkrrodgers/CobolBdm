package com.mlcpu.cas.api.ports;

import com.mlcpu.cas.api.IdempotencyKey;
import java.util.Optional;

/** The results of completed commands, by idempotency key (SPEC.md IDEM-01, IDEM-02). */
public interface CommandJournal {
    /** The result recorded for a completed command, if any. */
    Optional<Object> find(IdempotencyKey key);

    void record(IdempotencyKey key, Object result);
}
