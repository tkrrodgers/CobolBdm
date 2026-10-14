package com.mlcpu.loans.api.servicing;

/** A booking, or why there is none. */
public sealed interface BookingOutcome permits Booked, BookingRejected {
}
