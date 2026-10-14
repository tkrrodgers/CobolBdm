package com.mlcpu.loans.api;

/** Why terms are not scheduled. */
public enum ScheduleRejection {
    FLAT_INTEREST_NOT_OFFERED("RJTNNT01"),
    FULL_TERM_TRANCHE_NOT_OFFERED("RJTNNT02"),
    ROUNDING_MULTIPLE_NOT_OFFERED("RJTNNT03"),
    SINGLE_INSTALLMENT_AFTER_LATE_DISBURSEMENT("RJTNNT04"),
    DOWN_PAYMENT_COVERS_PRINCIPAL("RJTNNT05"),
    PERIOD_NOT_SUPPORTED_WITH_THIRTY_DAY_MONTHS("RJUNSP01"),
    DOWN_PAYMENT_PERCENT_MISSING("RJNULL01"),
    FIXED_LENGTH_WITHOUT_ADVANCING_FREQUENCY("RJNULL02"),
    DISBURSEMENT_OUTSIDE_TERM(null),
    ROUNDING_MODE_NOT_OFFERED(null),
    OUT_OF_RANGE(null);

    private final String legacyCode;

    ScheduleRejection(String legacyCode) {
        this.legacyCode = legacyCode;
    }

    /** The legacy reject code, kept for the migration's reconciliation; null for a rejection the legacy system did not have. */
    public String legacyCode() {
        return legacyCode;
    }
}
