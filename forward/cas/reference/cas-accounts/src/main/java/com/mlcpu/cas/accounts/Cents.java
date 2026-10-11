package com.mlcpu.cas.accounts;

import java.math.BigDecimal;
import java.math.RoundingMode;

/** Amounts at the legacy scale of 2 decimals. */
final class Cents {
    private Cents() {}

    static final BigDecimal ZERO = BigDecimal.ZERO.setScale(2);

    /** Truncates toward zero to 2 decimals, as a COBOL receiver without ROUNDED does. */
    static BigDecimal truncate(BigDecimal value) {
        return value.setScale(2, RoundingMode.DOWN);
    }
}
