package com.mlcpu.loans.api.migration;

/**
 * A legacy request the API cannot represent (D-5): the first legacy field, by its COBOL name, whose value has no API
 * equivalent, and that value as the record holds it (trailing spaces removed).
 */
public record Unrepresentable(String loanId, String field, String value) implements LegacyTermsOutcome {
}
