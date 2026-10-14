package com.mlcpu.loans.api.servicing;

/** Why a loan is not booked: its schedule is rejected, or the ledger cannot hold it (decisions D-11 and D-12). */
public enum BookingRejection {
    SCHEDULE_REJECTED,
    CURRENCY_SCALE_UNSUPPORTED_BY_LEDGER,
    NO_EXCHANGE_RATE_TO_BASE_CURRENCY,
    RATE_PRECISION_UNSUPPORTED_BY_LEDGER,
    AMOUNT_EXCEEDS_LEDGER_CAPACITY
}
