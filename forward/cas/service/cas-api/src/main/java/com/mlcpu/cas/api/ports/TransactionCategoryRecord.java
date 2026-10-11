package com.mlcpu.cas.api.ports;

/** A transaction category of a type, and its description. */
public record TransactionCategoryRecord(String typeCode, int categoryCode, String description) {}
