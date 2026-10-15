package com.mlcpu.loans.api.migration;

/**
 * The legacy loan book's migration, engine side (FE 9.2): each legacy loan as the service's terms, and its reconciliation
 * with what the legacy job wrote. The schedule comes from the service's own {@code ScheduleService.schedule}. Rules LEG.
 */
public interface LegacyMigration {
    /** The legacy request as the service's terms (LEG-02), or why the API cannot represent it (D-5). */
    LegacyTermsOutcome terms(LegacyLoan loan);

    /** The service recomputes the loan and compares its result with the legacy one (LEG-03, LEG-04). */
    Reconciliation reconcile(LegacyLoan loan);
}
