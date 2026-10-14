package com.mlcpu.loans.api;

import java.math.BigDecimal;
import java.time.LocalDate;

/** The disbursement: the amount lent, and the balance it leaves. */
public record DisbursementLine(LocalDate from, LocalDate due, BigDecimal amount, BigDecimal balanceAfter) implements ScheduleLine {
}
