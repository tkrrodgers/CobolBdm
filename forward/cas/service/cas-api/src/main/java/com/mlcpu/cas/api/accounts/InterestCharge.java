package com.mlcpu.cas.api.accounts;

import java.math.BigDecimal;

/** One interest transaction: a category balance, the annual rate applied (percent) and the monthly interest. */
public record InterestCharge(long accountId, String typeCode, int categoryCode, BigDecimal categoryBalance, BigDecimal annualRatePercent,
                             BigDecimal interest, String transactionId) {}
