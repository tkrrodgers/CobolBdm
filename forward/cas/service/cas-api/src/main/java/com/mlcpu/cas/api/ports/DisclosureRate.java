package com.mlcpu.cas.api.ports;

import java.math.BigDecimal;

/** The annual interest rate (percent, 2 decimals) a disclosure group applies to one transaction type and category. */
public record DisclosureRate(String groupId, String typeCode, int categoryCode, BigDecimal annualRatePercent) {
    /** The fallback group. */
    public static final String DEFAULT_GROUP = "DEFAULT";
}
