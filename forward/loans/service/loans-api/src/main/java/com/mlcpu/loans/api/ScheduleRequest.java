package com.mlcpu.loans.api;

import java.math.BigDecimal;
import java.time.LocalDate;

/** Proposed loan terms. The schedule starts at scheduleStart; every installment falls every {@code every} units of the frequency. */
public record ScheduleRequest(
        String loanId,
        LocalDate scheduleStart,
        CurrencyTerms currency,
        BigDecimal principal,
        LocalDate disbursementDate,
        int installments,
        int every,
        Frequency frequency,
        BigDecimal annualRatePercent,
        /** null: no down payment percentage given. */
        BigDecimal downPaymentPercent,
        boolean downPaymentEnabled,
        MonthDays monthDays,
        YearDays yearDays,
        /** null: no fixed length. The length of the loan term in units of the frequency. */
        Integer fixedLength,
        InterestMethod interestMethod,
        boolean fullTermTranche,
        Calculation calculation) {
}
