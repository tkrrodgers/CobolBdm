package com.mlcpu.cas.customers;

import com.mlcpu.cas.api.ports.CustomerProfile;
import java.math.BigDecimal;
import java.math.RoundingMode;

/** AVG-01: the whole parts of the 12 monthly amounts, summed, divided by 12 and truncated (scale 0). */
public final class MonthlyAverage {
    private MonthlyAverage() {}

    public static BigDecimal of(CustomerProfile profile) {
        BigDecimal sum = BigDecimal.ZERO;
        for (BigDecimal month : profile.monthlyAmounts()) sum = sum.add(month.setScale(0, RoundingMode.DOWN));
        return sum.divide(BigDecimal.valueOf(12), 0, RoundingMode.DOWN);
    }
}
