package com.mlcpu.loans.api.spi;

import com.mlcpu.loans.api.ScheduleRejection;

/** The terms have no rate factor for the stretch, for the given reason. */
public record RateRejected(ScheduleRejection reason) implements RateOutcome {
}
