package com.mlcpu.cas.api.ports;

import java.util.List;
import java.util.Optional;

/** The customer master by 6-character key. */
public interface CustomerMaster {
    Optional<MasterRecord> find(String key);

    /** Every record in key order. */
    List<MasterRecord> all();

    /** Inserts the record or replaces the one with the same key. */
    void save(MasterRecord record);

    void delete(String key);
}
