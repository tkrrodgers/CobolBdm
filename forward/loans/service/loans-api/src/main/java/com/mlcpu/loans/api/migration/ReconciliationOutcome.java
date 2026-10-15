package com.mlcpu.loans.api.migration;

/** How the service's result for a legacy loan compares with the legacy one (LEG-03, LEG-04). */
public enum ReconciliationOutcome {
    /** The legacy scheduled the loan and the service's schedule equals it, field by field. */
    MATCH,
    /** The two differ, and a decision of the forward design (FE section 6) explains the difference. */
    EXPLAINED,
    /** The two differ and no decision explains it: a defect to fix before migrating. */
    DIFF,
    /** The legacy rejected the request and the service rejects it for the same reason. */
    LEGACY_REJECT
}
