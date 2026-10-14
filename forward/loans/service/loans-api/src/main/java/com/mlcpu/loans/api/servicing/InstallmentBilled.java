package com.mlcpu.loans.api.servicing;

import java.math.BigDecimal;

/** One installment billed: its invoice, with the principal and interest it bills. */
public record InstallmentBilled(String loanId, int number, String invoiceId, BigDecimal principal, BigDecimal interest) {
}
