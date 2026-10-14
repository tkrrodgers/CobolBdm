package com.mlcpu.loans.api;

import java.math.BigDecimal;

/** The cost of credit (rules DISC-01 to DISC-07): APR by the actuarial method, a percentage with two decimals. */
public record Disclosure(BigDecimal amountFinanced, BigDecimal financeCharge, BigDecimal totalOfPayments, BigDecimal annualPercentageRate) {
}
