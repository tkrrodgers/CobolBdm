package com.mlcpu.loans.api.ports;

import com.mlcpu.loans.api.Schedule;
import com.mlcpu.loans.api.ScheduleRequest;
import java.time.LocalDate;
import java.util.List;

/**
 * A booked loan as stored: who it is for, its terms and schedule, its status, what its disbursement created, and the servicing
 * state of each line to bill (the down payment and the installments, in the schedule's order).
 */
public record LoanRecord(String loanId, String externalId, String ownerPartyId, String organizationPartyId, String loanProductId, String accountId,
        ScheduleRequest terms, LoanStatus status, LocalDate bookedOn, int scheduleVersion, Schedule schedule, List<LineServicing> lines,
        String disbursementPaymentId, String disbursementTransactionId) {

    public LoanRecord {
        lines = List.copyOf(lines);
    }
}
