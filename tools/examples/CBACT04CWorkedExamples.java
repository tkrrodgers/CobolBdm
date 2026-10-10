package com.mlcpu.capabilities.interestcalculation;

import com.mlcpu.carddemo.platform.records.Fields;
import com.mlcpu.rt.StepEquivalence;
import java.io.ByteArrayOutputStream;
import java.math.BigDecimal;
import java.math.MathContext;
import java.math.RoundingMode;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.*;
import org.junit.jupiter.api.Test;

/**
 * Worked examples for CBACT04C, traced from the faithful program: a small illustrative input (three accounts, five
 * category balances, a hand-made disclosure-group file; not one of the acceptance scenarios), run through the faithful
 * translation, with every input, computation and output byte written to -Dmlcpu.examples.out. Claims are checked
 * against the output before they are written.
 */
class CBACT04CWorkedExamples extends InterestCalculationEquivalenceTest {
    static final String EX_PARM = "2023063000";

    record Bal(int account, String type, int cat, String balance, String note) {}

    record Rate(String group, String type, int cat, String rate) {}

    @Test
    void generate() throws Exception {
        Map<String, byte[]> month = month();
        byte[] tcatAll = month.get(TCAT), acct = month.get(ACCT), xref = month.get(XREF), disc = month.get(DISC);
        List<String> ids = new ArrayList<>();
        for (int i = 0; i + 50 <= tcatAll.length && ids.size() < 3; i += 50) {
            String a = text(tcatAll, i, 11);
            if (!ids.contains(a)) ids.add(a);
        }
        String[] groups = {"GROUPA", "GROUPB", "GROUPC"};
        Bal[] bals = {
                new Bal(0, "01", 1, "1000.00", "group rate found"),
                new Bal(0, "01", 2, "250.55", "no rate for the account's group: the DEFAULT group's rate is used"),
                new Bal(0, "02", 1, "-300.01", "negative balance"),
                new Bal(1, "01", 1, "500.00", "rate 0.00: no interest, no transaction"),
                new Bal(2, "01", 1, "999.99", "last category balance of the file")};
        Rate[] rates = {
                new Rate("DEFAULT", "01", 2, "24.99"),
                new Rate("GROUPA", "01", 1, "18.00"),
                new Rate("GROUPA", "02", 1, "15.00"),
                new Rate("GROUPB", "01", 1, "0.00"),
                new Rate("GROUPC", "01", 1, "12.34")};

        byte[] template = Arrays.copyOfRange(tcatAll, 0, 50);
        var tcatOut = new ByteArrayOutputStream();
        for (Bal b : bals) {
            byte[] r = template.clone();
            Fields.putText(r, 0, 11, ids.get(b.account()));
            Fields.putText(r, 11, 2, b.type());
            Fields.putUnsigned(r, 13, 4, b.cat());
            Fields.putZoned(r, 17, 11, 2, true, new BigDecimal(b.balance()));
            tcatOut.writeBytes(r);
        }
        byte[] tcat = tcatOut.toByteArray();
        byte[] discTemplate = Arrays.copyOfRange(disc, 0, 50);
        var discOut = new ByteArrayOutputStream();
        for (Rate x : rates) {
            byte[] r = discTemplate.clone();
            Fields.putText(r, 0, 10, x.group());
            Fields.putText(r, 10, 2, x.type());
            Fields.putUnsigned(r, 12, 4, x.cat());
            Fields.putZoned(r, 16, 6, 2, true, new BigDecimal(x.rate()));
            discOut.writeBytes(r);
        }
        byte[] discIn = discOut.toByteArray();
        byte[] acctIn = acct.clone();
        Map<String, Integer> acctAt = new HashMap<>();
        for (int i = 0; i + 300 <= acctIn.length; i += 300) acctAt.put(text(acctIn, i, 11), i);
        for (int k = 0; k < 3; k++) Fields.putText(acctIn, acctAt.get(ids.get(k)) + 112, 10, groups[k]);

        Map<String, byte[]> in = new TreeMap<>(month);
        in.put(TCAT, tcat);
        in.put(DISC, discIn);
        in.put(ACCT, acctIn);
        var run = StepEquivalence.run(step(EX_PARM), in, "legacy");
        byte[] acctOut = run.files().get("data/" + ACCT), sys = run.files().getOrDefault("data/" + Q + "SYSTRAN", new byte[0]);
        String sysout = new String(run.files().get("sysout/STEP15.SYSOUT.txt"), StandardCharsets.ISO_8859_1);
        byte storageDefault = com.mlcpu.rt.CobConfig.DEFAULT_BYTE;

        var md = new StringBuilder();
        md.append("""
                # Worked examples: CBACT04C (traced from the faithful program)

                One run of the faithful translation of CBACT04C on a small input built for illustration (not one of the acceptance
                scenarios): PARM «%s»; a category-balance file (TCATBALF) of five records for three accounts, in key order; a
                disclosure-group file (DISCGRP) of five rate records; the three accounts' ACCT-GROUP-ID set to GROUPA, GROUPB and
                GROUPC. Cross-reference and account files are the CardDemo ones. The batch clock is 2022-07-05T10:11:12-07:00; the
                storage default byte (ctx.storageDefault()) is x'%02x'.

                Raw bytes are shown between « and » (ISO-8859-1; \\x00 is byte 0). Signed numeric fields are zoned decimal with the
                sign overpunched in the last byte: positive { A B C D E F G H I = 0..9, negative } J K L M N O P Q R = 0..9.

                """.formatted(EX_PARM, storageDefault & 0xff));
        md.append("Return code: ").append(run.returnCode()).append("\n\n");
        md.append("## Input: disclosure-group rates (DIS-GROUP-RECORD, key DIS-ACCT-GROUP-ID + DIS-TRAN-TYPE-CD + DIS-TRAN-CAT-CD)\n\n");
        for (int i = 0; i + 50 <= discIn.length; i += 50)
            md.append("- ").append(raw(discIn, i, 16)).append(" DIS-INT-RATE ").append(raw(discIn, i + 16, 6)).append(" = ")
              .append(Fields.zoned(discIn, i + 16, 6, 2).toPlainString()).append("\n");
        md.append("\n## Input: category balances (TRAN-CAT-BAL-RECORD), read in this order\n\n");
        for (int i = 0; i < bals.length; i++)
            md.append("- record ").append(i + 1).append(": ").append(raw(tcat, i * 50, 50)).append(" -> account ").append(ids.get(bals[i].account()))
              .append(", type ").append(bals[i].type()).append(", category ").append(String.format("%04d", bals[i].cat())).append(", TRAN-CAT-BAL ")
              .append(Fields.zoned(tcat, i * 50 + 17, 11, 2).toPlainString()).append(" (").append(bals[i].note()).append(")\n");

        md.append("\n## Interest per category balance\n\n");
        int tx = 0;
        BigDecimal[] totals = {BigDecimal.ZERO, BigDecimal.ZERO, BigDecimal.ZERO};
        Map<String, Integer> xrefFirst = new HashMap<>();
        for (int i = 0; i + 50 <= xref.length; i += 50) xrefFirst.putIfAbsent(text(xref, i + 25, 11), i);
        for (int i = 0; i < bals.length; i++) {
            Bal b = bals[i];
            String group = groups[b.account()];
            BigDecimal rate = rateFor(rates, group, b.type(), b.cat());
            boolean usedDefault = rateFor(rates, group, b.type(), b.cat(), false) == null;
            BigDecimal bal = new BigDecimal(b.balance());
            md.append("### Record ").append(i + 1).append(": account ").append(ids.get(b.account())).append(", type ").append(b.type()).append(", category ")
              .append(String.format("%04d", b.cat())).append("\n\n");
            md.append("Rate lookup: key «").append(String.format("%-10s%s%04d", group, b.type(), b.cat())).append("» ")
              .append(usedDefault ? "is not in DISCGRP (console lines «DISCLOSURE GROUP RECORD MISSING» and «TRY WITH DEFAULT GROUP CODE»); key «"
                      + String.format("%-10s%s%04d", "DEFAULT", b.type(), b.cat()) + "» is read instead" : "is found")
              .append(": DIS-INT-RATE ").append(rate.toPlainString()).append(".\n\n");
            if (rate.signum() == 0) {
                md.append("DIS-INT-RATE = 0: no interest is computed and no transaction is written.\n\n");
                continue;
            }
            BigDecimal exact = bal.multiply(rate).divide(new BigDecimal(1200), MathContext.DECIMAL128);
            byte[] t = Arrays.copyOfRange(sys, tx * 350, tx * 350 + 350);
            BigDecimal stored = Fields.zoned(t, 132, 11, 2);
            if (stored.compareTo(exact.setScale(2, RoundingMode.DOWN)) != 0) throw new IllegalStateException("record " + (i + 1) + ": stored " + stored + " exact " + exact);
            totals[b.account()] = totals[b.account()].add(stored);
            md.append("WS-MONTHLY-INT = (TRAN-CAT-BAL * DIS-INT-RATE) / 1200 = (").append(bal.toPlainString()).append(" * ").append(rate.toPlainString())
              .append(") / 1200 = ").append(exact.stripTrailingZeros().toPlainString()).append(" exactly; stored in PIC S9(09)V99 as ").append(stored.toPlainString())
              .append(exact.compareTo(stored) == 0 ? "" : " (digits beyond the second decimal are dropped, not rounded)").append(". WS-TOTAL-INT for the account is now ")
              .append(totals[b.account()].toPlainString()).append(".\n\n");
            md.append("Interest transaction written to SYSTRAN (TRAN-RECORD, 350 bytes, transaction ").append(tx + 1).append(" of the run):\n\n")
              .append("- TRAN-ID ").append(raw(t, 0, 16)).append(" (PARM date «").append(EX_PARM).append("» + WS-TRANID-SUFFIX ").append(String.format("%06d", tx + 1)).append(")\n")
              .append("- TRAN-TYPE-CD ").append(raw(t, 16, 2)).append(", TRAN-CAT-CD ").append(raw(t, 18, 4)).append(", TRAN-SOURCE ").append(raw(t, 22, 10)).append("\n")
              .append("- TRAN-DESC ").append(raw(t, 32, 100)).append("\n")
              .append("- TRAN-AMT ").append(raw(t, 132, 11)).append(" = ").append(stored.toPlainString()).append(", TRAN-MERCHANT-ID ").append(raw(t, 143, 9)).append("\n")
              .append("- TRAN-MERCHANT-NAME ").append(raw(t, 152, 50)).append(", TRAN-MERCHANT-CITY ").append(raw(t, 202, 50)).append(", TRAN-MERCHANT-ZIP ").append(raw(t, 252, 10)).append("\n")
              .append("- TRAN-CARD-NUM ").append(raw(t, 262, 16)).append(" (XREF-CARD-NUM of the account's first cross-reference: ")
              .append(raw(xref, xrefFirst.get(ids.get(b.account())), 16)).append(")\n")
              .append("- TRAN-ORIG-TS ").append(raw(t, 278, 26)).append(", TRAN-PROC-TS ").append(raw(t, 304, 26)).append(", FILLER@330 ").append(raw(t, 330, 20)).append("\n\n");
            if (!Arrays.equals(t, 262, 278, xref, xrefFirst.get(ids.get(b.account())), xrefFirst.get(ids.get(b.account())) + 16))
                throw new IllegalStateException("card number is not the first cross-reference");
            tx++;
        }
        if (tx * 350 != sys.length) throw new IllegalStateException("SYSTRAN has " + sys.length / 350 + " records, expected " + tx);

        md.append("## Accounts (ACCOUNT-RECORD)\n\n");
        for (int k = 0; k < 3; k++) {
            int b = acctAt.get(ids.get(k));
            byte[] before = Arrays.copyOfRange(acctIn, b, b + 300), after = Arrays.copyOfRange(acctOut, b, b + 300);
            md.append("### Account ").append(ids.get(k)).append(" (ACCT-GROUP-ID ").append(groups[k]).append(", interest total ").append(totals[k].toPlainString()).append(")\n\n");
            md.append("Before: ").append(accountFields(before)).append(".\n\n");
            if (Arrays.equals(before, after)) {
                md.append("After: unchanged (every byte). This is the last account in TCATBALF: the program updates an account only when the next record belongs ")
                  .append("to a different account, and the end-of-file branch that would update the last one never runs (quirk Q-01), although its interest ")
                  .append("transaction was written.\n\n");
            } else {
                BigDecimal expected = Fields.zoned(before, 12, 12, 2).add(totals[k]);
                if (Fields.zoned(after, 12, 12, 2).compareTo(expected) != 0) throw new IllegalStateException("account " + ids.get(k) + " balance");
                md.append("After (rewritten when the first record of the next account was read): ").append(accountFields(after)).append(". ACCT-CURR-BAL = ")
                  .append(Fields.zoned(before, 12, 12, 2).toPlainString()).append(" + ").append(totals[k].toPlainString()).append(" = ").append(expected.toPlainString())
                  .append("; ACCT-CURR-CYC-CREDIT and ACCT-CURR-CYC-DEBIT are set to 0. Bytes that differ from before: ").append(ranges(before, after))
                  .append("; every other byte (credit limits, FILLER) is as it was read.\n\n");
            }
        }
        md.append("## Complete SYSOUT of the run\n\n```\n").append(sysout.replace("\u0000", "\\x00")).append(sysout.endsWith("\n") ? "" : "\n").append("```\n\n");

        byte saved = com.mlcpu.rt.CobConfig.DEFAULT_BYTE;
        com.mlcpu.rt.CobConfig.DEFAULT_BYTE = ' ';
        try {
            var spaced = StepEquivalence.run(step(EX_PARM), in, "legacy");
            md.append("## Same input, storage default x'20' (a space)\n\nCompared with the run above, the only bytes that differ are bytes that hold the storage default (x'")
              .append(String.format("%02x", storageDefault & 0xff)).append("' above, a space here):\n\n");
            Map<String, Integer> lrecl = Map.of("data/" + Q + "SYSTRAN", 350, "data/" + ACCT, 300);
            for (var e : new TreeMap<>(spaced.files()).entrySet()) {
                byte[] x = run.files().get(e.getKey()), y = e.getValue();
                if (x == null || x.length != y.length) throw new IllegalStateException("space run differs in size: " + e.getKey());
                int len = lrecl.getOrDefault(e.getKey(), Math.max(1, x.length));
                Map<String, Integer> where = new TreeMap<>();
                for (int i = 0; i < x.length; i++) {
                    if (x[i] == y[i]) continue;
                    if (x[i] != storageDefault || y[i] != ' ') throw new IllegalStateException(e.getKey() + " byte " + i + " differs but is not the storage default");
                    int s = i;
                    while (i + 1 < x.length && x[i + 1] != y[i + 1] && (i + 1) / len == s / len) i++;
                    where.merge((s % len) + "-" + (i % len), 1, Integer::sum);
                }
                if (!where.isEmpty())
                    md.append("- ").append(e.getKey().replace("data/", "")).append(": ").append(String.join(", ", where.entrySet().stream()
                            .map(w -> "bytes " + w.getKey() + " of " + w.getValue() + " record(s)").toList())).append("\n");
            }
            md.append("\nNever hard-code x'00' or spaces: use ctx.storageDefault().\n");
        } finally {
            com.mlcpu.rt.CobConfig.DEFAULT_BYTE = saved;
        }
        Files.writeString(Path.of(System.getProperty("mlcpu.examples.out", "build/examples.md")), md.toString());
    }

    static BigDecimal rateFor(Rate[] rates, String group, String type, int cat) {
        BigDecimal r = rateFor(rates, group, type, cat, false);
        return r != null ? r : rateFor(rates, "DEFAULT", type, cat, false);
    }

    static BigDecimal rateFor(Rate[] rates, String group, String type, int cat, boolean unused) {
        for (Rate x : rates) if (x.group().equals(group) && x.type().equals(type) && x.cat() == cat) return new BigDecimal(x.rate());
        return null;
    }

    static String accountFields(byte[] a) {
        return "ACCT-CURR-BAL " + raw(a, 12, 12) + " = " + Fields.zoned(a, 12, 12, 2).toPlainString() + ", ACCT-CREDIT-LIMIT " + raw(a, 24, 12)
                + ", ACCT-CASH-CREDIT-LIMIT " + raw(a, 36, 12) + ", ACCT-CURR-CYC-CREDIT " + raw(a, 78, 12) + " = " + Fields.zoned(a, 78, 12, 2).toPlainString()
                + ", ACCT-CURR-CYC-DEBIT " + raw(a, 90, 12) + " = " + Fields.zoned(a, 90, 12, 2).toPlainString() + ", ACCT-GROUP-ID " + raw(a, 112, 10);
    }

    static String ranges(byte[] a, byte[] b) {
        var out = new ArrayList<String>();
        for (int i = 0; i < a.length; i++) {
            if (a[i] == b[i]) continue;
            int s = i;
            while (i + 1 < a.length && a[i + 1] != b[i + 1]) i++;
            out.add(s + "-" + i);
        }
        return out.isEmpty() ? "none" : String.join(", ", out);
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
