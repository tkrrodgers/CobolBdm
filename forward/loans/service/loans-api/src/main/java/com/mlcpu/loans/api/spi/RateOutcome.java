package com.mlcpu.loans.api.spi;

/** The rate factors of a stretch, or why the terms have none. */
public sealed interface RateOutcome permits Factors, RateRejected {
}
