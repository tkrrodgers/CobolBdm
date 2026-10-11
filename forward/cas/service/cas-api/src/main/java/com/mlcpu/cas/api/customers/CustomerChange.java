package com.mlcpu.cas.api.customers;

/**
 * One customer master change request, as the change file carries it. Values are raw text without trailing blanks:
 * <ul>
 *   <li>{@code code}: "UPDATE", "ADD", "DELETE" or anything else; a code that starts with '*' marks a comment line;</li>
 *   <li>{@code customerKey}: 6 characters, the 5-character customer id and the record type ('A' = profile);</li>
 *   <li>{@code action}: for UPDATE, "REPLACE" or "ADD"; {@code fieldName}: "NAME", "BALANCE", "ORDERS" or anything else;</li>
 *   <li>{@code value}: the update data, at most 36 characters (digests/SAM2.md says how numbers are read).</li>
 * </ul>
 */
public record CustomerChange(String code, String customerKey, String action, String fieldName, String value) {}
