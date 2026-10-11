package com.mlcpu.cas.api.ports;

import java.util.List;
import java.util.Optional;

/** Cards by number and by account. */
public interface Cards {
    Optional<CardRecord> findByCardNumber(String cardNumber);

    /** The account's cards in card number order (empty when it has none). */
    List<CardRecord> findByAccountId(long accountId);
}
