package com.mlcpu.loans.api;

/** Terms the service does not schedule (a typed rejection, never an exception). */
public record Rejected(String loanId, ScheduleRejection reason, /** OUT_OF_RANGE only: the field whose limit it breaks; else null. */ String field)
        implements ScheduleOutcome, DocumentOutcome, DisclosureOutcome {
}
