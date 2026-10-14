package com.mlcpu.loans.api;

import java.math.BigDecimal;
import java.util.List;

/** The printed schedule as data (rules RPT): its heading, one line per schedule line, its totals. Layout belongs to the screen. */
public record ScheduleDocument(String loanId, String currencyCode, int decimalPlaces, int termDays, int periods, List<DocumentLine> lines,
        BigDecimal totalPrincipal, BigDecimal totalInterest, BigDecimal totalRepayment, BigDecimal totalDisbursed) {
    public ScheduleDocument {
        lines = List.copyOf(lines);
    }
}
