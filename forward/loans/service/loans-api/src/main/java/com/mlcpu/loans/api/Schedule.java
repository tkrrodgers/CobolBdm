package com.mlcpu.loans.api;

import java.util.List;

/** A repayment schedule: its totals and its lines in order (the disbursement, a down payment, the installments). */
public record Schedule(String loanId, String currencyCode, int decimalPlaces, int termDays, Totals totals, List<ScheduleLine> lines) {
    public Schedule {
        lines = List.copyOf(lines);
    }
}
