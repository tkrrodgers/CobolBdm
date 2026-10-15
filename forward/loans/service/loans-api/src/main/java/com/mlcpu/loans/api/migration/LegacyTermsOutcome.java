package com.mlcpu.loans.api.migration;

/** A legacy request as the service's terms (LEG-02): the terms, or why the API cannot represent the request (D-5). */
public sealed interface LegacyTermsOutcome permits LegacyTerms, Unrepresentable {
}
