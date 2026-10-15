package com.mlcpu.loans.api.migration;

import com.mlcpu.loans.api.ScheduleRejection;

/**
 * The reconciliation of one legacy loan (LEG-03, LEG-04).
 * @param loanId the legacy loan id (RQ-LOAN-ID)
 * @param outcome the outcome
 * @param decision EXPLAINED: the decision that explains the difference ("D-2", "D-3", "D-5", "D-6"); else null
 * @param field DIFF and EXPLAINED: the first field that differs, as the digest names it; else null
 * @param legacyValue DIFF: the legacy's value of that field; else null
 * @param serviceValue DIFF: the service's value of that field; else null
 * @param legacyCode the legacy's reject code when the legacy rejected the request; else null
 * @param serviceRejection the service's rejection when the service rejects the request; else null
 */
public record Reconciliation(String loanId, ReconciliationOutcome outcome, String decision, String field, String legacyValue,
        String serviceValue, String legacyCode, ScheduleRejection serviceRejection) {
}
