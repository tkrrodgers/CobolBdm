package com.mlcpu.loans.api;

/** The loans quote: schedule, document and disclosure of proposed terms. Pure: no ports. */
public interface ScheduleService {
    /** The schedule for proposed terms (quote): nothing is stored. Rules TRM, CAL, RATE, AMZ, PLAN, MNY. */
    ScheduleOutcome schedule(ScheduleRequest request);

    /** The schedule as a document: its lines and totals as the printed schedule shows them. Rules RPT. */
    DocumentOutcome document(ScheduleRequest request);

    /** The disclosure of the schedule: amount financed, finance charge, total of payments, APR. Rules DISC. */
    DisclosureOutcome disclose(ScheduleRequest request);
}
