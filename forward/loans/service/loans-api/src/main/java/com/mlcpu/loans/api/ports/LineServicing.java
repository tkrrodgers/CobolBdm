package com.mlcpu.loans.api.ports;

import java.math.BigDecimal;
import java.time.LocalDate;

/**
 * The servicing state of a line to bill: its number (the schedule line's), what it bills, its invoice and billing date (null
 * until billed) and the deposit that collected its principal (null until collected).
 */
public record LineServicing(int number, LocalDate due, BigDecimal principal, BigDecimal interest, String invoiceId, LocalDate billedOn,
        String collectionTransactionId) {
}
