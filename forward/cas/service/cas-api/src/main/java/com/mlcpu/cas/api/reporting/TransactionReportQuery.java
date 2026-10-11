package com.mlcpu.cas.api.reporting;

import java.time.LocalDate;

/** The report period; both dates are inclusive. */
public record TransactionReportQuery(LocalDate startDate, LocalDate endDate) {}
