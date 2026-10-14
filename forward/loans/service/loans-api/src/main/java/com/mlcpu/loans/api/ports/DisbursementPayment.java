package com.mlcpu.loans.api.ports;

import java.math.BigDecimal;
import java.time.LocalDate;

/** A payment of the principal from the organization to the borrower. */
public record DisbursementPayment(String loanId, String loanProductId, String fromPartyId, String toPartyId, String paymentMethodId,
        String currencyCode, BigDecimal amount, LocalDate date) {
}
