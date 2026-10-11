package com.mlcpu.cas.customers;

import java.math.BigDecimal;
import java.math.RoundingMode;

/** A legacy unsigned numeric field: the sign is dropped, extra decimals are cut, and integer digits above its size are lost. */
record UnsignedField(int integerDigits, int scale) {
    static final UnsignedField BALANCE = new UnsignedField(7, 2);
    static final UnsignedField ORDERS = new UnsignedField(5, 0);

    BigDecimal store(BigDecimal value) {
        return value.abs().setScale(scale, RoundingMode.DOWN).remainder(BigDecimal.TEN.pow(integerDigits));
    }
}
