package com.mlcpu.loans.api.spi;

import java.math.BigDecimal;

/**
 * The rate factors of a stretch: the share of the annual rate it earns for the installment ({@code installment}, the stretch
 * itself) and for its interest ({@code interest}, from the stretch's start to the period's due date).
 */
public record Factors(BigDecimal installment, BigDecimal interest) implements RateOutcome {
}
