package com.mlcpu.loans.api.ports;

import java.math.BigDecimal;
import java.time.LocalDate;

/** The invoice of a loan line, from the organization to the borrower: its principal and its interest (an item each; none for 0). */
public record LoanInvoice(String loanId, String loanProductId, int lineNumber, String fromPartyId, String toPartyId, String currencyCode,
        LocalDate invoiceDate, BigDecimal principal, BigDecimal interest) {
}
