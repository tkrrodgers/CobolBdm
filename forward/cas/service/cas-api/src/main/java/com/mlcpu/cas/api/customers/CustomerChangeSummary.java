package com.mlcpu.cas.api.customers;

import java.util.List;

/**
 * The batch's result: totals for ADD, DELETE and UPDATE (always these three, in this order), every error in batch order,
 * and the number of changes read (comment lines not counted).
 */
public record CustomerChangeSummary(List<ChangeTotals> totals, List<CustomerChangeError> errors, int changesRead) {
    public CustomerChangeSummary {
        totals = List.copyOf(totals);
        errors = List.copyOf(errors);
    }
}
