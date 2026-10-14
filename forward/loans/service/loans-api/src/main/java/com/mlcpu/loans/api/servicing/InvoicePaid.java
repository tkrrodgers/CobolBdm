package com.mlcpu.loans.api.servicing;

/** An invoice became paid ({@code paid} true) or stopped being paid (false: a payment applied to it was voided). */
public record InvoicePaid(String invoiceId, boolean paid) {
}
