package com.mlcpu.loans.api.ports;

/** Invoices (the ERP posts them). */
public interface Billing {

    /** Creates the invoice of a loan line and returns its id. */
    String invoice(LoanInvoice invoice);
}
