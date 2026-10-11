package com.mlcpu.cas.api.ports;

import java.util.List;
import java.util.Optional;

/** Category balances by key (account id, type code, category code). */
public interface CategoryBalances {
    Optional<CategoryBalanceRecord> find(long accountId, String typeCode, int categoryCode);

    /** Every category balance in key order: account id, then type code, then category code. */
    List<CategoryBalanceRecord> all();

    /** Inserts the balance or replaces the one with the same key. */
    void save(CategoryBalanceRecord balance);
}
