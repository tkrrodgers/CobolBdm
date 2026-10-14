package com.mlcpu.loans.api.servicing;

/** A command the service cannot carry out; {@code reference} is the loan id or invoice id it names. Nothing is changed. */
public record ServicingRejected(String reference, ServicingRejection reason) implements DisbursementOutcome, CollectionOutcome {
}
