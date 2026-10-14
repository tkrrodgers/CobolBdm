package com.mlcpu.loans.api;

import java.time.LocalDate;

/** One line of a schedule. */
public sealed interface ScheduleLine permits DisbursementLine, DownPaymentLine, InstallmentLine {
    LocalDate from();

    LocalDate due();
}
