package com.mlcpu.cas.api.accounts;

import java.math.BigDecimal;
import java.time.LocalDateTime;

/**
 * A card transaction as received (the daily transaction record). {@code amount} has 2 decimals; a negative amount is a debit
 * (a payment or a return), zero or positive a credit. {@code originTimestamp} is null when the source left it blank. Text
 * values have no trailing blanks.
 */
public record TransactionInput(String transactionId, String typeCode, int categoryCode, String source, String description,
                               BigDecimal amount, long merchantId, String merchantName, String merchantCity, String merchantZip,
                               String cardNumber, LocalDateTime originTimestamp) {}
