package com.mlcpu.loans.api;

/** The currency of a loan: its code, the decimal places of its amounts and the multiple amounts are rounded to. */
public record CurrencyTerms(String code, int decimalPlaces, /** null: amounts are not rounded to a multiple. */ Integer roundingMultiple) {
}
