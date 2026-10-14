package com.mlcpu.loans.api;

/** A schedule document, or why there is none. */
public sealed interface DocumentOutcome permits Documented, Rejected {
}
