package com.mlcpu.cas.api.ports;

import java.math.BigDecimal;
import java.time.LocalDateTime;
import java.time.YearMonth;

/**
 * A posted or generated transaction. {@code processedTimestamp} is when the service posted it (null only for migrated entries
 * that never had one). {@code interestCycle} is the cycle an interest transaction was charged for, null for postings.
 */
public record LedgerEntry(String transactionId, long accountId, String cardNumber, String typeCode, int categoryCode, String source,
                          String description, BigDecimal amount, long merchantId, String merchantName, String merchantCity,
                          String merchantZip, LocalDateTime originTimestamp, LocalDateTime processedTimestamp, YearMonth interestCycle) {}
