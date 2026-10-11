package com.mlcpu.cas.customers;

import java.math.BigDecimal;
import java.util.Optional;

/** UPD-01..03: an update amount as the legacy 36-character field holds it, with two implied decimals. */
final class UpdateAmount {
    private UpdateAmount() {}

    static Optional<BigDecimal> parse(String value) {
        char[] d = String.format("%-36s", value == null ? "" : value).substring(0, 36).toCharArray();
        if (!digit(d[7]) || !digit(d[8])) {
            d[7] = '0';
            d[8] = '0';
        }
        boolean negative = d[0] == '-';
        if (d[0] == '-' || d[0] == '+') d[0] = '0';
        for (int i = 0; i < 9; i++) if (!digit(d[i])) return Optional.empty();
        BigDecimal amount = new BigDecimal(new String(d, 0, 9)).movePointLeft(2);
        return Optional.of(negative ? amount.negate() : amount);
    }

    private static boolean digit(char c) {
        return c >= '0' && c <= '9';
    }
}
