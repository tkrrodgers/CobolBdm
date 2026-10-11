package com.mlcpu.cas.api.ports;

import java.util.Optional;

/** Card accounts by id. */
public interface Accounts {
    Optional<AccountRecord> find(long accountId);

    /** Inserts the account or replaces the one with the same id. */
    void save(AccountRecord account);
}
