package com.mlcpu.loans.api.migration;

import com.mlcpu.loans.api.legacy.LegacyCurrency;
import com.mlcpu.loans.api.legacy.LegacyReject;
import com.mlcpu.loans.api.legacy.LegacyRequest;
import com.mlcpu.loans.api.legacy.LegacyRunControl;
import com.mlcpu.loans.api.legacy.LegacyScheduleHeader;
import com.mlcpu.loans.api.legacy.LegacyScheduleLine;
import java.util.List;

/**
 * A loan of the legacy loan book (FE 9.2): its request, the run's calculation, its currency, and what the legacy job wrote for
 * it: its schedule (the header and the lines in the order of their sequence numbers) or its reject.
 * @param request the loan request
 * @param runControl the run's calculation (one per run in the legacy, D-9)
 * @param currency the request's currency from the currency file; null when the file does not have it
 * @param header the schedule's header; null when the legacy rejected the request
 * @param lines the schedule's lines (disbursement D, down payment P, installments R) in sequence order; empty when rejected
 * @param reject the legacy reject; null when the legacy scheduled the request
 */
public record LegacyLoan(LegacyRequest request, LegacyRunControl runControl, LegacyCurrency currency, LegacyScheduleHeader header,
        List<LegacyScheduleLine> lines, LegacyReject reject) {
    public LegacyLoan {
        lines = List.copyOf(lines);
    }
}
