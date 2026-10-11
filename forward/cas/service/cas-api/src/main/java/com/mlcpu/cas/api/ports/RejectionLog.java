package com.mlcpu.cas.api.ports;

import java.util.List;

/** Every refused transaction. */
public interface RejectionLog {
    void append(RejectionRecord rejection);

    /** Every rejection, in the order appended. */
    List<RejectionRecord> all();
}
