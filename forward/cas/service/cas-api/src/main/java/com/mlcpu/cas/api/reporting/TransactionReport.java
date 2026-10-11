package com.mlcpu.cas.api.reporting;

import java.math.BigDecimal;
import java.time.LocalDate;
import java.util.List;

/** The transaction report: card groups in card number order and the grand total. */
public record TransactionReport(LocalDate startDate, LocalDate endDate, List<CardGroup> cards, BigDecimal grandTotal) {
    public TransactionReport {
        cards = List.copyOf(cards);
    }
}
