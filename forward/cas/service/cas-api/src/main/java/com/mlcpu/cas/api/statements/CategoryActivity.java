package com.mlcpu.cas.api.statements;

import java.math.BigDecimal;

/** The cycle's postings of one type and category: how many and their sum. */
public record CategoryActivity(String typeCode, int categoryCode, int postings, BigDecimal total) {}
