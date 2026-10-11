package com.mlcpu.cas.accounts;

import java.math.BigDecimal;
import java.math.RoundingMode;

/** INT-06, INT-07: monthly interest on a category balance at an annual percent rate, truncated to the cent. */
final class InterestPolicy {
    private InterestPolicy() {}

    private static final BigDecimal MONTHS_TIMES_PERCENT = BigDecimal.valueOf(1200);

    static boolean charges(BigDecimal annualRatePercent) {
        return annualRatePercent.signum() != 0;
    }

    static BigDecimal monthlyInterest(BigDecimal balance, BigDecimal annualRatePercent) {
        return balance.multiply(annualRatePercent).divide(MONTHS_TIMES_PERCENT, 2, RoundingMode.DOWN);
    }
}
