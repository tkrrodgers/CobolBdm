package com.mlcpu.carddemo.platform.numeric;

import java.math.BigDecimal;
import java.math.RoundingMode;

/**
 * Decimal storage rules of the legacy data: a stored amount keeps a fixed number of integer and fraction digits.
 * Extra fraction digits are cut (not rounded) and extra integer digits are lost from the left, as the COBOL receivers do
 * when no ROUNDED or ON SIZE ERROR is coded. Unsigned fields keep the magnitude only.
 */
public final class Decimals {
    private Decimals() {}

    public static BigDecimal fit(BigDecimal value, int integerDigits, int scale, boolean signed) {
        BigDecimal v = value.setScale(scale, RoundingMode.DOWN);
        BigDecimal limit = BigDecimal.TEN.pow(integerDigits);
        BigDecimal magnitude = v.abs().remainder(limit);
        if (!signed || v.signum() >= 0) return magnitude.setScale(scale, RoundingMode.UNNECESSARY);
        return magnitude.negate().setScale(scale, RoundingMode.UNNECESSARY);
    }

    /** A signed amount with 9 integer and 2 fraction digits: S9(9)V99. */
    public static BigDecimal amount9(BigDecimal value) {
        return fit(value, 9, 2, true);
    }

    /** A signed balance with 10 integer and 2 fraction digits: S9(10)V99. */
    public static BigDecimal balance10(BigDecimal value) {
        return fit(value, 10, 2, true);
    }
}
