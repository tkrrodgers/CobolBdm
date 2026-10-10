package com.mlcpu.capabilities.interestcalculation;

import com.mlcpu.carddemo.platform.numeric.Decimals;
import java.math.BigDecimal;
import java.math.RoundingMode;

/** Monthly interest on a category balance: balance x annual rate (percent) / 1200, truncated to the cent (rule R-CBACT04C-1300). */
public final class InterestPolicy {
    private InterestPolicy() {}

    private static final BigDecimal MONTHS_X_PERCENT = BigDecimal.valueOf(1200);

    public static BigDecimal monthlyInterest(BigDecimal balance, BigDecimal annualRatePercent) {
        return Decimals.amount9(balance.multiply(annualRatePercent).divide(MONTHS_X_PERCENT, 2, RoundingMode.DOWN));
    }

    /** No interest is charged for a zero rate. */
    public static boolean charges(BigDecimal annualRatePercent) {
        return annualRatePercent.signum() != 0;
    }
}
