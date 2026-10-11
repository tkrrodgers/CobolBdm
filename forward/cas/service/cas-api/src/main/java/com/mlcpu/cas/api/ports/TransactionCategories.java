package com.mlcpu.cas.api.ports;

import java.util.Optional;

/** Transaction categories by type and category code. */
public interface TransactionCategories {
    Optional<TransactionCategoryRecord> find(String typeCode, int categoryCode);
}
