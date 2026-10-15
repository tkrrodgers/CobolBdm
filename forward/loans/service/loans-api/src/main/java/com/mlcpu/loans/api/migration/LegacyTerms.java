package com.mlcpu.loans.api.migration;

import com.mlcpu.loans.api.ScheduleRequest;

/** The legacy request as the service's terms, with the run's calculation (LEG-02). */
public record LegacyTerms(ScheduleRequest terms) implements LegacyTermsOutcome {
}
