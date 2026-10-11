package com.mlcpu.cas.api.reporting;

import java.math.BigDecimal;

/** One reported transaction with its account and the descriptions of its type and category. */
public record ReportLine(String transactionId, long accountId, String typeCode, String typeDescription, int categoryCode,
                         String categoryDescription, String source, BigDecimal amount) {}
