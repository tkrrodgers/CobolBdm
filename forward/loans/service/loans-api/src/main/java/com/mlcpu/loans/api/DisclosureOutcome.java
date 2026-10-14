package com.mlcpu.loans.api;

/** A disclosure, or why there is none. */
public sealed interface DisclosureOutcome permits Disclosed, Rejected {
}
