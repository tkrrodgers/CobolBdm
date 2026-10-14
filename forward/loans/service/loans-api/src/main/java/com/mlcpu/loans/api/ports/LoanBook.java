package com.mlcpu.loans.api.ports;

import java.util.List;
import java.util.Optional;

/** The booked loans. */
public interface LoanBook {

    Optional<LoanRecord> find(String loanId);

    /** The loan whose line was billed with this invoice. */
    Optional<LoanRecord> findByInvoice(String invoiceId);

    /** The loans with this status, by loan id. */
    List<LoanRecord> withStatus(LoanStatus status);

    /** Stores the loan (a new one, or the new state of one already stored). */
    void save(LoanRecord loan);
}
