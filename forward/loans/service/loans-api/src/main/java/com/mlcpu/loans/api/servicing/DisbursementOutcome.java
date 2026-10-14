package com.mlcpu.loans.api.servicing;

/** A disbursement, or why there is none. */
public sealed interface DisbursementOutcome permits Disbursed, ServicingRejected {
}
