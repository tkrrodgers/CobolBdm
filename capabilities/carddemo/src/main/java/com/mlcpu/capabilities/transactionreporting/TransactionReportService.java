package com.mlcpu.capabilities.transactionreporting;

import com.mlcpu.carddemo.domain.Reference.CardCrossReference;
import com.mlcpu.carddemo.domain.Reference.TransactionCategory;
import com.mlcpu.carddemo.domain.Reference.TransactionType;
import com.mlcpu.carddemo.domain.Repositories;
import com.mlcpu.carddemo.domain.Repositories.Received;
import com.mlcpu.carddemo.domain.Transaction;
import com.mlcpu.carddemo.platform.text.Formats;
import java.io.IOException;
import java.util.Optional;
import java.util.function.Consumer;

/**
 * Daily transaction detail report (CBTRN03C, design TransactionDetailReport). Transactions arrive sorted by card; each is
 * reported with its account, type and category descriptions, grouped by card with account totals, paged with page totals,
 * and closed by a grand total.
 * <p>Legacy behaviour preserved for parity (quirks, see design.json):
 * <ul>
 *   <li>Q-01: the first transaction processed outside the reporting period ends the report at once — no totals are written.</li>
 *   <li>Q-02: at end of input the last transaction's amount is added to the totals a second time before the final page
 *       and grand totals, and the last card's account total is never written.</li>
 * </ul>
 */
public final class TransactionReportService {

    public interface Transactions {
        Optional<Received<Transaction>> next() throws IOException;
    }

    public record Period(String startDate, String endDate) {
        boolean contains(String isoDate) {
            return isoDate.compareTo(startDate) >= 0 && isoDate.compareTo(endDate) <= 0;
        }
    }

    /** A lookup that must succeed; the step decides how a miss ends the run. */
    public static final class MissingReference extends IOException {
        public final String what;
        public final String key;

        public MissingReference(String what, String key) {
            super(what + " " + key);
            this.what = what;
            this.key = key;
        }
    }

    private final Transactions transactions;
    private final Repositories.CardCrossReferences crossReferences;
    private final Repositories.TransactionTypes types;
    private final Repositories.TransactionCategories categories;
    private final ReportPager pager;
    private final Consumer<String> console;
    private final String numericStyle;

    public TransactionReportService(Transactions transactions, Repositories.CardCrossReferences crossReferences,
                                    Repositories.TransactionTypes types, Repositories.TransactionCategories categories,
                                    ReportPager pager, Consumer<String> console, String numericStyle) {
        this.transactions = transactions;
        this.crossReferences = crossReferences;
        this.types = types;
        this.categories = categories;
        this.pager = pager;
        this.console = console;
        this.numericStyle = numericStyle;
    }

    public void report(Period period) throws IOException {
        console.accept("Reporting from " + period.startDate() + " to " + period.endDate());
        Transaction last = null;
        String currentCard = null;
        CardCrossReference card = null;
        while (true) {
            Optional<Received<Transaction>> next = transactions.next();
            if (next.isPresent()) last = next.get().value();
            if (last == null || !period.contains(last.processedDate())) return;
            if (next.isEmpty()) {
                console.accept("TRAN-AMT " + Formats.signedNumber(last.amount(), 9, 2, numericStyle));
                console.accept("WS-PAGE-TOTAL" + Formats.signedNumber(pager.pageTotal(), 9, 2, numericStyle));
                pager.accumulate(last.amount());
                pager.pageTotals();
                pager.grandTotal();
                return;
            }
            console.accept(next.get().original());
            Transaction t = last;
            if (!t.cardNumber().equals(currentCard)) {
                if (pager.started()) pager.accountTotals();
                currentCard = t.cardNumber();
                card = crossReferences.findByCardNumber(t.cardNumber()).orElseThrow(() -> new MissingReference("CARD", t.cardNumber()));
            }
            TransactionType type = types.find(t.typeCode()).orElseThrow(() -> new MissingReference("TYPE", t.typeCode()));
            TransactionCategory category = categories.find(t.typeCode(), t.categoryCode())
                    .orElseThrow(() -> new MissingReference("CATEGORY", String.format("%-2s%04d", t.typeCode(), t.categoryCode())));
            pager.detail(period.startDate(), period.endDate(), new ReportLayout.DetailLine(t.id(), card.accountId(), t.typeCode(),
                    type.description(), t.categoryCode(), category.description(), t.source(), t.amount()));
        }
    }
}
