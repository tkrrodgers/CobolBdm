package com.mlcpu.cas.api.accounts;

import java.math.BigDecimal;

/** One account's cycle close: the interest added to its balance and the balance after it. */
public record AccountInterest(long accountId, BigDecimal totalInterest, BigDecimal newBalance) {}
