package com.mlcpu.cas.api.ports;

import java.math.BigDecimal;

/** The running balance (2 decimals) of one account for one transaction type and category. */
public record CategoryBalanceRecord(long accountId, String typeCode, int categoryCode, BigDecimal balance) {}
