package com.mlcpu.loans.api.servicing;

/** What a paid (or unpaid) invoice did to its loan. */
public sealed interface CollectionOutcome permits PrincipalCollected, CollectionCancelled, CollectionUnchanged, ServicingRejected {
}
