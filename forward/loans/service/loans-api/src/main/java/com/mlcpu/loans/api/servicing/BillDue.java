package com.mlcpu.loans.api.servicing;

import java.time.LocalDate;

/** Bill what falls due on or before the date (the daily job). */
public record BillDue(LocalDate asOf) {
}
