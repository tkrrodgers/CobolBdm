package com.mlcpu.cas.api.customers;

/** Totals of one change type ("ADD", "DELETE" or "UPDATE"): requested = processed + inError. */
public record ChangeTotals(String changeType, int requested, int processed, int inError) {}
