package com.mlcpu.loans.api.spi;

import com.mlcpu.loans.api.Calculation;

/** Interest rate factors and day counts (rules RATE). */
public interface RateFactors {
    RateOutcome factors(RateQuery query, Calculation calculation);
}
