package com.mlcpu.capabilities.transactionreporting;

import com.mlcpu.carddemo.platform.text.Formats;
import java.math.BigDecimal;

/** The 133-column lines of the daily transaction report (copybook CVTRA07Y). */
public final class ReportLayout {
    private ReportLayout() {}

    public static final int WIDTH = 133;

    public record DetailLine(String transactionId, long accountId, String typeCode, String typeDescription, int categoryCode,
                             String categoryDescription, String source, BigDecimal amount) {}

    public static String title(String startDate, String endDate) {
        return line(Formats.column("DALYREPT", 38) + Formats.column("Daily Transaction Report", 41) + "Date Range: "
                + Formats.column(startDate, 10) + " to " + Formats.column(endDate, 10));
    }

    public static String blank() {
        return " ".repeat(WIDTH);
    }

    public static String columnHeadings() {
        return line(Formats.column("Transaction ID", 17) + Formats.column("Account ID", 12) + Formats.column("Transaction Type", 19)
                + Formats.column("Tran Category", 35) + Formats.column("Tran Source", 14) + " " + Formats.column("        Amount", 16));
    }

    public static String rule() {
        return "-".repeat(WIDTH);
    }

    public static String detail(DetailLine d) {
        return line(Formats.column(d.transactionId(), 16) + " " + String.format("%011d", d.accountId()) + " "
                + Formats.column(d.typeCode(), 2) + "-" + Formats.column(d.typeDescription(), 15) + " "
                + String.format("%04d", d.categoryCode()) + "-" + Formats.column(d.categoryDescription(), 29) + " "
                + Formats.column(d.source(), 10) + "    " + Formats.signedAmount(d.amount(), '-') + "  ");
    }

    public static String pageTotal(BigDecimal total) {
        return line(Formats.column("Page Total", 11) + ".".repeat(86) + Formats.signedAmount(total, '+'));
    }

    public static String accountTotal(BigDecimal total) {
        return line(Formats.column("Account Total", 13) + ".".repeat(84) + Formats.signedAmount(total, '+'));
    }

    public static String grandTotal(BigDecimal total) {
        return line(Formats.column("Grand Total", 11) + ".".repeat(86) + Formats.signedAmount(total, '+'));
    }

    private static String line(String s) {
        return Formats.column(s, WIDTH);
    }
}
