package com.mlcpu.loans.api;

/** A schedule, or why there is none. */
public sealed interface ScheduleOutcome permits Scheduled, Rejected {
}
