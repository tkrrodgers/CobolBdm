package com.mlcpu.loans.api.ports;

import java.math.BigDecimal;
import java.time.LocalDate;

/** A transaction on a loan's account; it names the payment or the invoice behind it (the other is null). */
public record AccountTransaction(String accountId, AccountTransactionType type, BigDecimal amount, LocalDate date, String paymentId,
        String invoiceId) {
}
