package com.mlcpu.capabilities.transactionreporting;

import com.mlcpu.carddemo.platform.records.Fields;
import com.mlcpu.rt.StepEquivalence;
import java.io.ByteArrayOutputStream;
import java.math.BigDecimal;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.*;
import org.junit.jupiter.api.Test;

/**
 * Worked examples for CBTRN03C, traced from the faithful program: an illustrative input (whole card groups taken from the
 * middle of the sorted CardDemo transaction file; not one of the acceptance scenarios) run three ways: complete, with one
 * transaction outside the reporting period, and with an unknown category. Every statement about paging and totals is
 * checked against the output before it is written to -Dmlcpu.examples.out.
 */
class CBTRN03CWorkedExamples extends TransactionReportEquivalenceTest {
    static final String REPT = "data/" + Q + "TRANREPT", SYSOUT = "sysout/STEP10R.SYSOUT.txt";

    @Test
    void generate() throws Exception {
        Map<String, byte[]> base = sorted();
        byte[] all = base.get(TRAN), dates = base.get(DATES), xref = base.get(XREF), types = base.get(TYPE), cats = base.get(CATG);
        int start = 100 * 350;
        while (start + 350 <= all.length && text(all, start + 262, 16).equals(text(all, start - 350 + 262, 16))) start += 350;
        byte[] tran = null;
        int groups = 0, count = 0;
        Map<String, byte[]> in = null;
        StepEquivalence.Outcome run = null;
        for (int want = 3; want <= 12; want++) {
            var picked = new ByteArrayOutputStream();
            groups = 0;
            count = 0;
            String card = null;
            for (int i = start; i + 350 <= all.length; i += 350) {
                String c = text(all, i + 262, 16);
                if (!c.equals(card)) {
                    if (groups == want) break;
                    groups++;
                    card = c;
                }
                picked.writeBytes(Arrays.copyOfRange(all, i, i + 350));
                count++;
            }
            tran = picked.toByteArray();
            in = new TreeMap<>(base);
            in.put(TRAN, tran);
            run = StepEquivalence.run(step(), in, "legacy");
            List<String> l = lines(run.files().get(REPT));
            boolean midBreak = false;
            for (int i = 0; i + 2 < l.size(); i++) midBreak |= l.get(i).startsWith("Page Total") && l.get(i + 2).startsWith("DALYREPT");
            if (midBreak) break;
        }
        String from = text(dates, 0, 10), to = text(dates, 11, 10);
        byte storageDefault = com.mlcpu.rt.CobConfig.DEFAULT_BYTE;

        var md = new StringBuilder();
        md.append("""
                # Worked examples: CBTRN03C (traced from the faithful program)

                Runs of the faithful translation of CBTRN03C on an illustrative input (not one of the acceptance scenarios): %d
                transactions of %d cards, whole card groups taken from the middle of the CardDemo transaction file (already sorted
                by card number, as the TRANREPT job's SORT step leaves it). Lookup files are the CardDemo ones. DATEPARM is
                %s: reporting period %s to %s. The storage default byte (ctx.storageDefault()) is x'%02x'.

                Raw bytes are shown between « and » (ISO-8859-1). Report lines are 133 bytes; each is shown whole, trailing spaces
                included.

                """.formatted(count, groups, raw(dates, 0, 21), from, to, storageDefault & 0xff));

        md.append("## Run 1: every transaction inside the period\n\n").append("Return code: ").append(run.returnCode()).append("\n\n");
        md.append("Transactions, in input order, with the lookups the program makes (cross-reference only when the card number changes):\n\n");
        Map<String, String> acctOf = new HashMap<>();
        for (int i = 0; i + 50 <= xref.length; i += 50) acctOf.put(text(xref, i, 16), text(xref, i + 25, 11));
        Map<String, String> typeDesc = new HashMap<>(), catDesc = new HashMap<>();
        for (int i = 0; i + 60 <= types.length; i += 60) typeDesc.put(text(types, i, 2), text(types, i + 2, 50));
        for (int i = 0; i + 60 <= cats.length; i += 60) catDesc.put(text(cats, i, 6), text(cats, i + 6, 50));
        List<BigDecimal> amounts = new ArrayList<>();
        String prev = null;
        for (int i = 0; i + 350 <= tran.length; i += 350) {
            String c = text(tran, i + 262, 16), t = text(tran, i + 16, 2), k = text(tran, i + 16, 6);
            BigDecimal amt = Fields.zoned(tran, i + 132, 11, 2);
            amounts.add(amt);
            md.append("- ").append(i / 350 + 1).append(". TRAN-ID ").append(raw(tran, i, 16)).append(", TRAN-CARD-NUM ").append(raw(tran, i + 262, 16))
              .append(c.equals(prev) ? "" : " (new card: XREF-ACCT-ID " + acctOf.get(c) + ")").append(", TRAN-TYPE-CD ").append(raw(tran, i + 16, 2))
              .append(" -> TRAN-TYPE-DESC «").append(typeDesc.get(t).trim()).append("», TRAN-CAT-CD ").append(raw(tran, i + 18, 4)).append(" -> TRAN-CAT-TYPE-DESC «")
              .append(catDesc.get(k).trim()).append("», TRAN-SOURCE ").append(raw(tran, i + 22, 10)).append(", TRAN-AMT ").append(raw(tran, i + 132, 11)).append(" = ")
              .append(amt.toPlainString()).append(", TRAN-PROC-TS ").append(raw(tran, i + 304, 26)).append("\n");
            prev = c;
        }
        md.append("\n");
        List<String> lines = lines(run.files().get(REPT));
        md.append(annotate(lines, amounts));
        md.append("SYSOUT of run 1 (each transaction is displayed as its 350-byte record as it is read; the two lines before the end come from the end-of-file branch):\n\n```\n")
          .append(clean(run.files().get(SYSOUT))).append("```\n\n");

        byte[] late = tran.clone();
        int lateAt = 4;
        Fields.putText(late, lateAt * 350 + 304, 26, "2099-01-01-00.00.00.000000");
        Map<String, byte[]> in2 = new TreeMap<>(in);
        in2.put(TRAN, late);
        var run2 = StepEquivalence.run(step(), in2, "legacy");
        List<String> lines2 = lines(run2.files().get(REPT));
        if (lines2.stream().anyMatch(l -> l.startsWith("Page Total") || l.startsWith("Grand Total") || l.startsWith("Account Total")))
            throw new IllegalStateException("run 2 wrote totals");
        md.append("## Run 2: transaction ").append(lateAt + 1).append(" processed outside the period (quirk Q-01)\n\n")
          .append("Same input, except TRAN-PROC-TS of transaction ").append(lateAt + 1).append(" is «2099-01-01-00.00.00.000000», after the period end ").append(to)
          .append(". The program's NEXT SENTENCE leaves the read loop at that record: it is not displayed, and the report ends after the ").append(lateAt)
          .append(" earlier detail lines with no page total, account total or grand total. Return code: ").append(run2.returnCode()).append(".\n\n");
        md.append("TRANREPT of run 2 (").append(lines2.size()).append(" lines):\n\n```\n");
        for (String l : lines2) md.append(l).append("\n");
        md.append("```\n\nSYSOUT of run 2:\n\n```\n").append(clean(run2.files().get(SYSOUT))).append("```\n\n");

        byte[] badCat = tran.clone();
        int badAt = 2;
        Fields.putUnsigned(badCat, badAt * 350 + 18, 4, 9999);
        Map<String, byte[]> in3 = new TreeMap<>(in);
        in3.put(TRAN, badCat);
        var run3 = StepEquivalence.run(step(), in3, "legacy");
        md.append("## Run 3: transaction ").append(badAt + 1).append(" has a category that is not in TRANCATG\n\n")
          .append("Same input, except TRAN-CAT-CD of transaction ").append(badAt + 1).append(" is «9999». The lookup fails (INVALID KEY) and the program abends. Return code: ")
          .append(run3.returnCode()).append(" (").append(run3.detail()).append("). ");
        int keptLines = lines(run3.files().get(REPT)).size();
        md.append(keptLines == 0
                ? "TRANREPT is empty (0 bytes), although the WRITE statements for the headings and the first " + badAt + " detail lines had already run: the abend ends the program before its CLOSE statements, so report records still in the output buffer are never written. Do not close or flush output files on the abend path."
                : "TRANREPT holds " + keptLines + " lines.");
        md.append(" SYSOUT of run 3:\n\n```\n").append(clean(run3.files().get(SYSOUT))).append("```\n\n");

        byte saved = com.mlcpu.rt.CobConfig.DEFAULT_BYTE;
        com.mlcpu.rt.CobConfig.DEFAULT_BYTE = ' ';
        try {
            var spaced = StepEquivalence.run(step(), in, "legacy");
            boolean same = true;
            for (var e : run.files().entrySet()) same &= Arrays.equals(e.getValue(), spaced.files().get(e.getKey()));
            md.append("## Storage default\n\nRun 1 with storage default x'20' (a space) produces ").append(same ? "exactly the same bytes" : "DIFFERENT bytes")
              .append(": every report line is built from items with VALUE clauses or moved values.\n");
            if (!same) throw new IllegalStateException("space run differs");
        } finally {
            com.mlcpu.rt.CobConfig.DEFAULT_BYTE = saved;
        }
        Files.writeString(Path.of(System.getProperty("mlcpu.examples.out", "build/examples.md")), md.toString());
    }

    /** The report with line kinds and the line counter; checks the paging rule and the end-of-file totals as it goes. */
    static String annotate(List<String> lines, List<BigDecimal> amounts) {
        var sb = new StringBuilder("TRANREPT of run 1 (").append(lines.size()).append(" lines). Columns: file line, kind, WS-LINE-COUNTER after the line, the 133-byte line:\n\n```\n");
        long counter = 0;
        int detail = 0, firstBreak = -1, skippedAt = -1;
        BigDecimal page = BigDecimal.ZERO, grand = BigDecimal.ZERO, firstBreakTotal = null, lastPageTotal = null, grandTotal = null;
        for (int i = 0; i < lines.size(); i++) {
            String l = lines.get(i);
            String kind = kind(l);
            if (kind.equals("detail")) {
                if (counter % 20 == 0) throw new IllegalStateException("detail at counter " + counter + " without a page break");
                page = page.add(amounts.get(detail++));
            }
            if (kind.equals("page total")) {
                BigDecimal shown = amount(l);
                boolean last = i + 2 < lines.size() && lines.get(i + 2).startsWith("Grand Total");
                if (last) {
                    BigDecimal lastAmount = amounts.get(amounts.size() - 1);
                    if (shown.compareTo(page.add(lastAmount)) != 0) throw new IllegalStateException("final page total " + shown + " vs " + page + " + " + lastAmount);
                    lastPageTotal = shown;
                } else {
                    if (counter % 20 != 0) throw new IllegalStateException("page break at counter " + counter);
                    if (shown.compareTo(page) != 0) throw new IllegalStateException("page total " + shown + " vs " + page);
                    if (firstBreak < 0) { firstBreak = i + 1; firstBreakTotal = shown; }
                }
                grand = grand.add(shown);
                page = BigDecimal.ZERO;
            }
            if (kind.equals("grand total")) {
                grandTotal = amount(l);
                if (grandTotal.compareTo(grand) != 0) throw new IllegalStateException("grand total " + grandTotal + " vs " + grand);
            } else counter++;
            if (counter % 20 == 0 && !kind.equals("grand total") && i + 1 < lines.size() && !kind(lines.get(i + 1)).equals("page total")
                    && !kind(lines.get(i + 1)).equals("detail") && skippedAt < 0) skippedAt = i + 1;
            sb.append(String.format("%3d %-13s %3s %s%n", i + 1, kind, kind.equals("grand total") ? "-" : String.valueOf(counter), l));
        }
        sb.append("```\n\n");
        if (firstBreak < 0 || lastPageTotal == null || grandTotal == null) throw new IllegalStateException("run 1 needs a page break and final totals");
        BigDecimal last = amounts.get(amounts.size() - 1);
        sb.append("How to read it (each point checked against this output):\n\n")
          .append("- The title, blank line, column headings and rule are written before the first detail line; every report line except the grand total adds 1 to WS-LINE-COUNTER.\n")
          .append("- The page check runs only before a detail line: when WS-LINE-COUNTER is then a multiple of 20, a page total and a rule are written, then the four heading lines again, ")
          .append("and the page total restarts at 0 (file line ").append(firstBreak).append(": page total ").append(firstBreakTotal.toPlainString())
          .append(" = the details since the previous page break).");
        if (skippedAt > 0)
            sb.append(" When the counter reaches a multiple of 20 on a line that is followed by an account total or rule instead of a detail, it has moved past the multiple ")
              .append("by the time the next detail is checked, so no page break happens there (file line ").append(skippedAt).append(").");
        sb.append("\n- When the card number changes (not before the first card), an account total and a rule are written before the next detail line, and the account total restarts at 0.\n")
          .append("- At end of input (quirk Q-02) the program displays «TRAN-AMT » and «WS-PAGE-TOTAL» with the last record's amount and the running page total, adds the last amount (")
          .append(last.toPlainString()).append(") to the page total a second time, writes the page total (").append(lastPageTotal.toPlainString())
          .append(" = the last page's details + ").append(last.toPlainString()).append(" again) and a rule, then the grand total (")
          .append(grandTotal.toPlainString()).append(" = the sum of every page total written). The last card's account total is never written.\n\n");
        return sb.toString();
    }

    static String kind(String l) {
        if (l.startsWith("DALYREPT")) return "title";
        if (l.isBlank()) return "blank";
        if (l.startsWith("Transaction ID")) return "headings";
        if (l.startsWith("-----")) return "rule";
        if (l.startsWith("Page Total")) return "page total";
        if (l.startsWith("Account Total")) return "account total";
        if (l.startsWith("Grand Total")) return "grand total";
        return "detail";
    }

    static BigDecimal amount(String line) {
        String s = line.substring(97).replace(" ", "").replace(",", "");
        if (s.isEmpty()) return BigDecimal.ZERO;
        boolean neg = s.contains("-");
        BigDecimal v = new BigDecimal(s.replace("+", "").replace("-", ""));
        return neg ? v.negate() : v;
    }

    static List<String> lines(byte[] f) {
        List<String> l = new ArrayList<>();
        if (f == null) return l;
        for (int i = 0; i + 133 <= f.length; i += 133) l.add(text(f, i, 133));
        return l;
    }

    static String clean(byte[] sysout) {
        String s = new String(sysout, StandardCharsets.ISO_8859_1).replace("\u0000", "\\x00");
        return s.endsWith("\n") ? s : s + "\n";
    }

    static String text(byte[] r, int off, int len) {
        return new String(r, off, len, StandardCharsets.ISO_8859_1);
    }

    static String raw(byte[] r, int off, int len) {
        var sb = new StringBuilder("«");
        for (int i = off; i < off + len; i++) {
            int c = r[i] & 0xff;
            sb.append(c >= 0x20 && c < 0x7f ? String.valueOf((char) c) : String.format("\\x%02x", c));
        }
        return sb.append('»').toString();
    }
}
