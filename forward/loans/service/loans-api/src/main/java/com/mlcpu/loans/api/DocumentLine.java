package com.mlcpu.loans.api;

import java.math.BigDecimal;
import java.time.LocalDate;

/** One line of the printed schedule. */
public record DocumentLine(LineType type, int number, LocalDate from, LocalDate due, BigDecimal principal, BigDecimal interest,
        BigDecimal totalDue, BigDecimal outstanding) {
    public enum LineType { DISBURSEMENT, DOWN_PAYMENT, INSTALLMENT }
}
