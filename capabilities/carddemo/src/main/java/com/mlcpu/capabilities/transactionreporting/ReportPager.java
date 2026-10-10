package com.mlcpu.capabilities.transactionreporting;

import com.mlcpu.carddemo.platform.numeric.Decimals;
import java.io.IOException;
import java.math.BigDecimal;

/**
 * Page and total bookkeeping of the report: 20 lines per page (headings and totals count as lines), a page total and
 * the column headings start each new page, account totals close each card's group, and page totals roll into the
 * grand total.
 */
public final class ReportPager {

    public interface Lines {
        void write(String line) throws IOException;
    }

    static final int PAGE_SIZE = 20;

    private final Lines out;
    private long lineCount;
    private boolean started;
    private BigDecimal pageTotal = BigDecimal.ZERO.setScale(2);
    private BigDecimal accountTotal = BigDecimal.ZERO.setScale(2);
    private BigDecimal grandTotal = BigDecimal.ZERO.setScale(2);
    private String startDate = "";
    private String endDate = "";

    public ReportPager(Lines out) {
        this.out = out;
    }

    public boolean started() {
        return started;
    }

    public BigDecimal pageTotal() {
        return pageTotal;
    }

    /** A detail line: the title and headings first, a page break every 20 lines, then the amount joins the totals. */
    public void detail(String startDate, String endDate, ReportLayout.DetailLine d) throws IOException {
        if (!started) {
            started = true;
            this.startDate = startDate;
            this.endDate = endDate;
            headings();
        }
        if (lineCount % PAGE_SIZE == 0) {
            pageTotals();
            headings();
        }
        accumulate(d.amount());
        emit(ReportLayout.detail(d));
    }

    public void accumulate(BigDecimal amount) {
        pageTotal = Decimals.amount9(pageTotal.add(amount));
        accountTotal = Decimals.amount9(accountTotal.add(amount));
    }

    public void accountTotals() throws IOException {
        emit(ReportLayout.accountTotal(accountTotal));
        accountTotal = BigDecimal.ZERO.setScale(2);
        emit(ReportLayout.rule());
    }

    public void pageTotals() throws IOException {
        emit(ReportLayout.pageTotal(pageTotal));
        grandTotal = Decimals.amount9(grandTotal.add(pageTotal));
        pageTotal = BigDecimal.ZERO.setScale(2);
        emit(ReportLayout.rule());
    }

    /** The grand total line does not count towards the page. */
    public void grandTotal() throws IOException {
        out.write(ReportLayout.grandTotal(grandTotal));
    }

    private void headings() throws IOException {
        emit(ReportLayout.title(startDate, endDate));
        emit(ReportLayout.blank());
        emit(ReportLayout.columnHeadings());
        emit(ReportLayout.rule());
    }

    private void emit(String line) throws IOException {
        out.write(line);
        lineCount++;
    }
}
