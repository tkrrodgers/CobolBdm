package com.mlcpu.loans.api;

import java.math.BigDecimal;

/** The totals of a schedule. */
public record Totals(BigDecimal disbursed, BigDecimal principal, BigDecimal interest, BigDecimal fee, BigDecimal penalty, BigDecimal repayment,
        BigDecimal outstanding) {
}
