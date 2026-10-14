package com.mlcpu.loans.api.spi;

import java.time.LocalDate;

/** One repayment period: it runs from {@code from} to the installment's due date. */
public record Period(LocalDate from, LocalDate due) {
}
