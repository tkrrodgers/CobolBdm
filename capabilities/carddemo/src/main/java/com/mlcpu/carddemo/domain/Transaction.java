package com.mlcpu.carddemo.domain;

import java.math.BigDecimal;

/**
 * A card transaction (CVTRA05Y TRAN-RECORD; the daily input CVTRA06Y has the same shape). Timestamps are DB2 text
 * (YYYY-MM-DD-HH.MM.SS.ffffff); the first ten characters are the ISO date.
 */
public record Transaction(String id, String typeCode, int categoryCode, String source, String description, BigDecimal amount,
                          long merchantId, String merchantName, String merchantCity, String merchantZip, String cardNumber,
                          String originTimestamp, String processedTimestamp) {

    public String originDate() {
        return originTimestamp.length() >= 10 ? originTimestamp.substring(0, 10) : originTimestamp;
    }

    public String processedDate() {
        return processedTimestamp.length() >= 10 ? processedTimestamp.substring(0, 10) : processedTimestamp;
    }

    public Transaction processedAt(String timestamp) {
        return new Transaction(id, typeCode, categoryCode, source, description, amount, merchantId, merchantName, merchantCity,
                merchantZip, cardNumber, originTimestamp, timestamp);
    }
}
