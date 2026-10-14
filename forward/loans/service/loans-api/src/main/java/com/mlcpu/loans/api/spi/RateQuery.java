package com.mlcpu.loans.api.spi;

import com.mlcpu.loans.api.Frequency;
import com.mlcpu.loans.api.MonthDays;
import com.mlcpu.loans.api.YearDays;
import java.math.BigDecimal;
import java.time.LocalDate;

/**
 * A stretch of a repayment period, from {@code from} to {@code to}, and the terms its rate factors depend on. The schedule
 * starts at {@code scheduleStart}; installments fall every {@code every} units of the frequency.
 */
public record RateQuery(LocalDate scheduleStart, Period period, LocalDate from, LocalDate to, BigDecimal annualRatePercent,
        Frequency frequency, int every, MonthDays monthDays, YearDays yearDays) {
}
