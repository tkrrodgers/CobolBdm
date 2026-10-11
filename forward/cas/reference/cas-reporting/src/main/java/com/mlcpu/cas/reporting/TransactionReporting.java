package com.mlcpu.cas.reporting;

import com.mlcpu.cas.api.CasPorts;
import com.mlcpu.cas.api.ReferenceDataMissing;
import com.mlcpu.cas.api.ports.CardRecord;
import com.mlcpu.cas.api.ports.LedgerEntry;
import com.mlcpu.cas.api.reporting.CardGroup;
import com.mlcpu.cas.api.reporting.ReportLine;
import com.mlcpu.cas.api.reporting.TransactionReport;
import com.mlcpu.cas.api.reporting.TransactionReportQuery;
import java.math.BigDecimal;
import java.time.LocalDate;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/** The transaction report (rules RPT). */
public final class TransactionReporting {
    private static final BigDecimal ZERO = BigDecimal.ZERO.setScale(2);

    private final CasPorts ports;

    public TransactionReporting(CasPorts ports) {
        this.ports = ports;
    }

    public TransactionReport report(TransactionReportQuery q) {
        Map<String, List<LedgerEntry>> byCard = new LinkedHashMap<>();
        ports.ledger().all().stream()
                .filter(e -> e.processedTimestamp() != null && within(e.processedTimestamp().toLocalDate(), q))
                .sorted(Comparator.comparing(LedgerEntry::cardNumber).thenComparing(LedgerEntry::transactionId))
                .forEach(e -> byCard.computeIfAbsent(e.cardNumber(), k -> new ArrayList<>()).add(e));
        List<CardGroup> groups = new ArrayList<>();
        BigDecimal grand = ZERO;
        for (Map.Entry<String, List<LedgerEntry>> g : byCard.entrySet()) {
            CardRecord card = ports.cards().findByCardNumber(g.getKey()).orElseThrow(() -> new ReferenceDataMissing("card", g.getKey()));
            List<ReportLine> lines = new ArrayList<>();
            BigDecimal total = ZERO;
            for (LedgerEntry e : g.getValue()) {
                String type = ports.transactionTypes().find(e.typeCode())
                        .orElseThrow(() -> new ReferenceDataMissing("transaction type", e.typeCode())).description();
                String category = ports.transactionCategories().find(e.typeCode(), e.categoryCode())
                        .orElseThrow(() -> new ReferenceDataMissing("transaction category", e.typeCode() + String.format("%04d", e.categoryCode())))
                        .description();
                lines.add(new ReportLine(e.transactionId(), card.accountId(), e.typeCode(), type, e.categoryCode(), category, e.source(), e.amount()));
                total = total.add(e.amount());
            }
            groups.add(new CardGroup(g.getKey(), card.accountId(), lines, total));
            grand = grand.add(total);
        }
        return new TransactionReport(q.startDate(), q.endDate(), groups, grand);
    }

    private static boolean within(LocalDate d, TransactionReportQuery q) {
        return !d.isBefore(q.startDate()) && !d.isAfter(q.endDate());
    }
}
