package com.mlcpu.loans.api.servicing;

import com.mlcpu.loans.api.ScheduleRejection;

/**
 * Terms the service does not book (nothing is stored). {@code scheduleReason} and {@code field} are the schedule's rejection
 * when {@code reason} is SCHEDULE_REJECTED, else null.
 */
public record BookingRejected(String externalId, BookingRejection reason, ScheduleRejection scheduleReason, String field) implements BookingOutcome {
}
