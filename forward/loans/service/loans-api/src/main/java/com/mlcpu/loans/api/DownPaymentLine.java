package com.mlcpu.loans.api;

import java.math.BigDecimal;
import java.time.LocalDate;

/** A down payment: principal paid at disbursement; remainingDue is the total of payments still due after it. */
public record DownPaymentLine(int number, LocalDate from, LocalDate due, BigDecimal principal, BigDecimal totalDue, BigDecimal balanceAfter,
        BigDecimal remainingDue) implements ScheduleLine {
}
