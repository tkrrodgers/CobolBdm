package com.mlcpu.cas.api.reporting;

import java.math.BigDecimal;
import java.util.List;

/** The lines of one card, in transaction id order, and their total. */
public record CardGroup(String cardNumber, long accountId, List<ReportLine> lines, BigDecimal accountTotal) {
    public CardGroup {
        lines = List.copyOf(lines);
    }
}
