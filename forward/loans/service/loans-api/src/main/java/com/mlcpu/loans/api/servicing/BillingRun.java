package com.mlcpu.loans.api.servicing;

import java.time.LocalDate;
import java.util.List;

/** What a billing run billed, by loan id and then installment number; empty when nothing was due. */
public record BillingRun(LocalDate asOf, List<InstallmentBilled> billed) {

    public BillingRun {
        billed = List.copyOf(billed);
    }
}
