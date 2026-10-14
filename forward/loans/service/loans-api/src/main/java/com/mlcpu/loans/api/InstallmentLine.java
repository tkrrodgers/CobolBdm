package com.mlcpu.loans.api;

import java.math.BigDecimal;
import java.time.LocalDate;

/** An installment: the principal and interest due for one period, the balance after it, and the total of payments still due after it (remainingDue). */
public record InstallmentLine(int number, LocalDate from, LocalDate due, BigDecimal principal, BigDecimal interest, BigDecimal fee,
        BigDecimal penalty, BigDecimal totalDue, BigDecimal balanceAfter, BigDecimal remainingDue) implements ScheduleLine {
}
