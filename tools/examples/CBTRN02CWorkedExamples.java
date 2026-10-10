package com.mlcpu.capabilities.transactionposting;

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
 * Worked examples for CBTRN02C, traced from the faithful program. Builds a small illustrative input from the CardDemo day
 * (nine transactions, one per behaviour; not one of the acceptance scenarios), runs the faithful translation, and writes
 * inputs, intermediate checks and the exact output bytes to the file named by -Dmlcpu.examples.out. Every statement about
 * where a FILLER byte came from is checked against the output before it is written.
 * Run by tools/New-WorkedExamples.ps1 inside a copy of the exported project.
 */
class CBTRN02CWorkedExamples extends TransactionPostingEquivalenceTest {
    enum Role {
        CREATE_FIRST("first posting of the run; its category balance does not exist yet (record created)", true, false),
        EXISTING_1("purchase posted to an existing category balance", false, true),
        CREDIT_CREATE("credit (negative amount) posted; its category balance does not exist yet (record created)", true, false),
        REJECT_100("card number not in the cross-reference file (reject 100)", false, false),
        REJECT_101("account record missing (reject 101)", false, false),
        REJECT_102("over the credit limit (reject 102)", false, false),
        REJECT_103("account expired before the transaction date (reject 103)", false, false),
        EXISTING_2("purchase posted to another existing category balance", false, true),
        FSIGN_ZERO_CREATE("zero amount posted to an account whose positive amounts are stored with an unsigned zone (F-sign); its category balance does not exist yet (record created)", true, false);

        final String text;
        final boolean create, existing;

        Role(String text, boolean create, boolean existing) {
            this.text = text;
            this.create = create;
            this.existing = existing;
        }
    }

    @Test
    void generate() throws Exception {
        Map<String, byte[]> day = day();
        byte[] daly = day.get(DALY), acct = day.get(ACCT), xref = day.get(XREF), tcat = day.get(TCAT).clone();
        for (int i = 0; i + 50 <= tcat.length; i += 50) Arrays.fill(tcat, i + 28, i + 50, (byte) ('a' + (i / 50) % 26));
        Map<String, String> cardAcct = new HashMap<>();
        for (int i = 0; i + 50 <= xref.length; i += 50) cardAcct.put(text(xref, i, 16), text(xref, i + 25, 11));
        Map<String, Integer> acctAt = new HashMap<>();
        for (int i = 0; i + 300 <= acct.length; i += 300) acctAt.put(text(acct, i, 11), i);
        Map<String, byte[]> tcatIn = byKey(tcat, 50, 17);

        Role[] roles = Role.values();
        byte[][] picked = new byte[roles.length][];
        String[] accts = new String[roles.length];
        Set<String> used = new HashSet<>();
        Set<Byte> fillers = new HashSet<>();
        for (Role want : new Role[] {Role.EXISTING_1, Role.EXISTING_2})
            for (int i = 0; i + 350 <= daly.length && picked[want.ordinal()] == null; i += 350) {
                String a = cardAcct.get(text(daly, i + 262, 16));
                if (a == null || !acctAt.containsKey(a) || used.contains(a)) continue;
                byte[] t = tcatIn.get(a + text(daly, i + 16, 2) + text(daly, i + 18, 4));
                if (t == null || !fillers.add(t[28])) continue;
                picked[want.ordinal()] = Arrays.copyOfRange(daly, i, i + 350);
                accts[want.ordinal()] = a;
                used.add(a);
            }
        for (Role r : roles)
            for (int i = 0; i + 350 <= daly.length && picked[r.ordinal()] == null; i += 350) {
                String a = cardAcct.get(text(daly, i + 262, 16));
                if (a == null || !acctAt.containsKey(a) || used.contains(a)) continue;
                picked[r.ordinal()] = Arrays.copyOfRange(daly, i, i + 350);
                accts[r.ordinal()] = a;
                used.add(a);
            }

        byte[] acctIn = acct.clone();
        for (String a : accts) {
            int b = acctAt.get(a);
            Fields.putZoned(acctIn, b + 24, 12, 2, true, new BigDecimal("50000.00"));
            Fields.putText(acctIn, b + 58, 10, "2099-12-31");
        }
        for (Role r : roles) {
            byte[] d = picked[r.ordinal()];
            BigDecimal amount = Fields.zoned(d, 132, 11, 2).abs();
            Fields.putZoned(d, 132, 11, 2, true, r == Role.CREDIT_CREATE ? amount.negate() : r == Role.FSIGN_ZERO_CREATE ? BigDecimal.ZERO : amount);
            if (r.create)
                for (int cat = 90; ; cat++) {
                    if (!tcatIn.containsKey(accts[r.ordinal()] + text(d, 16, 2) + String.format("%04d", cat))) {
                        Fields.putUnsigned(d, 18, 4, cat);
                        break;
                    }
                }
        }
        Fields.putText(picked[Role.REJECT_100.ordinal()], 262, 16, "9999999999999999");
        Fields.putZoned(acctIn, acctAt.get(accts[Role.REJECT_102.ordinal()]) + 24, 12, 2, true, new BigDecimal("1.00"));
        Fields.putZoned(picked[Role.REJECT_102.ordinal()], 132, 11, 2, true, new BigDecimal("100.00"));
        Fields.putText(acctIn, acctAt.get(accts[Role.REJECT_103.ordinal()]) + 58, 10, "2000-01-01");
        int bz = acctAt.get(accts[Role.FSIGN_ZERO_CREATE.ordinal()]);
        unsignedZone(acctIn, bz + 12, 12, bz + 24, 12, bz + 36, 12, bz + 78, 12, bz + 90, 12);
        byte[] acctBefore = acctIn.clone();
        int b101 = acctAt.get(accts[Role.REJECT_101.ordinal()]);
        acctIn = concat(Arrays.copyOfRange(acctIn, 0, b101), Arrays.copyOfRange(acctIn, b101 + 300, acctIn.length));

        Map<String, byte[]> in = new TreeMap<>(day);
        in.put(DALY, concat(picked));
        in.put(ACCT, acctIn);
        in.put(TCAT, tcat);
        var run = StepEquivalence.run(step(), in, "legacy");
        byte[] acctOut = run.files().get("data/" + ACCT), tcatOut = run.files().get("data/" + TCAT), tranOut = run.files().get("data/" + TRAN),
                rejOut = run.files().getOrDefault("data/" + Q + "DALYREJS", new byte[0]);
        String sysout = new String(run.files().get("sysout/STEP15.SYSOUT.txt"), StandardCharsets.ISO_8859_1);
        byte storageDefault = com.mlcpu.rt.CobConfig.DEFAULT_BYTE;

        var md = new StringBuilder();
        md.append("""
                # Worked examples: CBTRN02C (traced from the faithful program)

                One run of the faithful translation of CBTRN02C on a small input built for illustration: nine daily transactions
                from the CardDemo day, each edited to show one behaviour, processed in this order. This input is not one of the
                acceptance scenarios. Before the run: the nine accounts got credit limit 50000.00 and expiration 2099-12-31 unless an
                example says otherwise; every category-balance record (TRAN-CAT-BAL-RECORD) got its own FILLER letter (record 0
                'a' x 22, record 1 'b' x 22, ...) so you can see which record's bytes end up where; the transaction file starts
                empty; the batch clock is 2022-07-05T10:11:12-07:00; the storage default byte (ctx.storageDefault()) is x'%02x'.

                Raw bytes are shown between « and » (ISO-8859-1; \\x00 is byte 0). Signed numeric fields are zoned decimal with the
                sign overpunched in the last byte: positive { A B C D E F G H I = 0..9, negative } J K L M N O P Q R = 0..9; a positive
                value may also arrive with a plain digit (unsigned zone, "F-sign").

                """.formatted(storageDefault & 0xff));
        md.append("Return code: ").append(run.returnCode()).append("\n\n");

        byte[] area = new byte[50];
        Arrays.fill(area, storageDefault);
        String areaFrom = "the storage default: no category-balance record had been read or written yet in this run";
        for (Role r : roles) {
            int k = r.ordinal();
            byte[] d = picked[k];
            String id = text(d, 0, 16), card = text(d, 262, 16), acctId = cardAcct.get(card);
            BigDecimal amount = Fields.zoned(d, 132, 11, 2);
            md.append("## Example ").append(k + 1).append(": ").append(r.text).append("\n\n");
            md.append("Daily transaction (DALYTRAN-RECORD): DALYTRAN-ID ").append(raw(d, 0, 16)).append(", DALYTRAN-TYPE-CD ").append(raw(d, 16, 2))
              .append(", DALYTRAN-CAT-CD ").append(raw(d, 18, 4)).append(", DALYTRAN-AMT ").append(raw(d, 132, 11)).append(" = ").append(amount.toPlainString())
              .append(", DALYTRAN-CARD-NUM ").append(raw(d, 262, 16)).append(", DALYTRAN-ORIG-TS ").append(raw(d, 278, 26))
              .append(", DALYTRAN-PROC-TS ").append(raw(d, 304, 26)).append(".\n\n");
            if (acctId == null) md.append("Cross-reference: card ").append(card).append(" is not in CARDXREF.\n\n");
            else md.append("Cross-reference: card ").append(card).append(" -> XREF-ACCT-ID ").append(acctId).append(".\n\n");
            byte[] before = acctId == null || r == Role.REJECT_101 ? null : Arrays.copyOfRange(acctBefore, acctAt.get(acctId), acctAt.get(acctId) + 300);
            if (r == Role.REJECT_101) md.append("Account ").append(acctId).append(" was removed from ACCTDATA for this example.\n\n");
            else if (before != null) {
                md.append("Account before: ").append(accountFields(before)).append(".\n\n");
                BigDecimal credit = Fields.zoned(before, 78, 12, 2), debit = Fields.zoned(before, 90, 12, 2), limit = Fields.zoned(before, 24, 12, 2);
                BigDecimal projected = credit.subtract(debit).add(amount);
                md.append("Checks: WS-TEMP-BAL = ACCT-CURR-CYC-CREDIT - ACCT-CURR-CYC-DEBIT + DALYTRAN-AMT = ").append(credit.toPlainString()).append(" - ")
                  .append(debit.toPlainString()).append(" + ").append(amount.toPlainString()).append(" = ").append(projected.toPlainString())
                  .append("; ACCT-CREDIT-LIMIT ").append(limit.toPlainString()).append(limit.compareTo(projected) >= 0 ? " >= " : " < ").append("WS-TEMP-BAL")
                  .append("; ACCT-EXPIRAION-DATE ").append(text(before, 58, 10)).append(text(before, 58, 10).compareTo(text(d, 278, 10)) >= 0 ? " >= " : " < ")
                  .append("DALYTRAN-ORIG-TS(1:10) ").append(text(d, 278, 10)).append(".\n\n");
            }
            byte[] rej = find(rejOut, 430, 0, 16, id);
            if (rej != null) {
                md.append("Outcome: REJECTED. Reject record (DALYREJS, 430 bytes): bytes 0-349 are ")
                  .append(Arrays.equals(rej, 0, 350, d, 0, 350) ? "the daily record exactly as read" : "NOT the daily record: " + raw(rej, 0, 350))
                  .append("; WS-VALIDATION-FAIL-REASON ").append(raw(rej, 350, 4)).append("; WS-VALIDATION-FAIL-REASON-DESC ").append(raw(rej, 354, 76)).append(".\n\n");
                if (before != null) {
                    byte[] after = find(acctOut, 300, 0, 11, acctId);
                    md.append("Account after: ").append(after != null && Arrays.equals(after, before) ? "unchanged (every byte)" : "CHANGED " + accountFields(after)).append(".\n\n");
                }
                md.append("Category balances: not read or written for a rejected transaction (the record area keeps ").append(raw(area, 28, 22)).append(" in FILLER).\n\n");
                continue;
            }
            md.append("Outcome: POSTED.\n\n");
            byte[] after = find(acctOut, 300, 0, 11, acctId);
            md.append("Account after: ").append(accountFields(after)).append(". Bytes that differ from before: ").append(ranges(before, after)).append(".\n\n");
            String key = acctId + text(d, 16, 2) + text(d, 18, 4);
            byte[] tb = tcatIn.get(key), ta = find(tcatOut, 50, 0, 17, key);
            if (ta == null) throw new IllegalStateException("no category balance " + key + " in the output");
            md.append("Category balance (TRAN-CAT-BAL-RECORD) key ").append(raw(ta, 0, 17)).append(": ");
            if (tb != null) {
                if (!r.existing) throw new IllegalStateException("example " + (k + 1) + " expected a new category balance");
                md.append("READ found «").append(text(tb, 0, 17)).append("» = ").append(raw(tb, 0, 50)).append(" (TRAN-CAT-BAL ").append(Fields.zoned(tb, 17, 11, 2).toPlainString())
                  .append("); rewritten as ").append(raw(ta, 0, 50)).append(" (TRAN-CAT-BAL ").append(Fields.zoned(ta, 17, 11, 2).toPlainString())
                  .append("). Bytes that differ: ").append(ranges(tb, ta)).append(". The record area TRAN-CAT-BAL-RECORD now holds this record (FILLER ").append(raw(ta, 28, 22)).append(").\n\n");
                areaFrom = "the record read and rewritten in example " + (k + 1) + " (key " + text(ta, 0, 17) + ")";
            } else {
                if (!r.create) throw new IllegalStateException("example " + (k + 1) + " expected an existing category balance");
                if (!Arrays.equals(ta, 28, 50, area, 28, 50))
                    throw new IllegalStateException("example " + (k + 1) + ": FILLER " + raw(ta, 28, 22) + " is not the record area's " + raw(area, 28, 22));
                md.append("READ did not find it (INVALID KEY; console line «TCATBAL record not found for key : ").append(text(ta, 0, 17))
                  .append(".. Creating.»); the INTO area TRAN-CAT-BAL-RECORD is not changed by the failed READ. The program then runs INITIALIZE TRAN-CAT-BAL-RECORD, ")
                  .append("moves the key fields and the amount, and writes ").append(raw(ta, 0, 50)).append(" (TRAN-CAT-BAL ").append(Fields.zoned(ta, 17, 11, 2).toPlainString())
                  .append("). FILLER@28 ").append(raw(ta, 28, 22)).append(" was not written by the program (INITIALIZE skips FILLER): it is what the record area still held, ")
                  .append(areaFrom).append(".\n\n");
                areaFrom = "the record created in example " + (k + 1) + " (key " + text(ta, 0, 17) + ")";
            }
            area = ta.clone();
            byte[] t = find(tranOut, 350, 0, 16, id);
            md.append("Posted transaction (TRAN-RECORD, 350 bytes): TRAN-ID ").append(raw(t, 0, 16)).append(", TRAN-AMT ").append(raw(t, 132, 11))
              .append(", TRAN-ORIG-TS ").append(raw(t, 278, 26)).append(", TRAN-PROC-TS ").append(raw(t, 304, 26)).append(", FILLER@330 ").append(raw(t, 330, 20))
              .append("; bytes 0-303 ").append(Arrays.equals(t, 0, 304, d, 0, 304) ? "equal the daily record" : "differ from the daily record: " + ranges(Arrays.copyOfRange(d, 0, 304), Arrays.copyOfRange(t, 0, 304))).append(".\n\n");
        }
        md.append("## Complete SYSOUT of the run\n\n```\n").append(sysout.replace("\u0000", "\\x00")).append(sysout.endsWith("\n") ? "" : "\n").append("```\n\n");

        byte saved = com.mlcpu.rt.CobConfig.DEFAULT_BYTE;
        com.mlcpu.rt.CobConfig.DEFAULT_BYTE = ' ';
        try {
            var spaced = StepEquivalence.run(step(), in, "legacy");
            byte[] d = picked[Role.CREATE_FIRST.ordinal()];
            String key = accts[Role.CREATE_FIRST.ordinal()] + text(d, 16, 2) + text(d, 18, 4);
            byte[] ta = find(spaced.files().get("data/" + TCAT), 50, 0, 17, key);
            byte[] t = find(spaced.files().get("data/" + TRAN), 350, 0, 16, text(d, 0, 16));
            md.append("## Same input, storage default x'20' (a space)\n\n")
              .append("When ctx.storageDefault() is a space, the record area starts as spaces: example 1 then creates ").append(raw(ta, 0, 50))
              .append(" (FILLER@28 ").append(raw(ta, 28, 22)).append("), and its posted transaction ends with FILLER@330 ").append(raw(t, 330, 20))
              .append(". Compared with the run above, the only bytes that differ are bytes that hold the storage default (x'")
              .append(String.format("%02x", storageDefault & 0xff)).append("' above, a space here):\n\n");
            var plain = run.files();
            Map<String, Integer> lrecl = Map.of("data/" + TCAT, 50, "data/" + TRAN, 350, "data/" + ACCT, 300, "data/" + Q + "DALYREJS", 430);
            for (var e : new TreeMap<>(spaced.files()).entrySet()) {
                byte[] x = plain.get(e.getKey()), y = e.getValue();
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

    static Map<String, byte[]> byKey(byte[] f, int lrecl, int key) {
        Map<String, byte[]> m = new TreeMap<>();
        for (int i = 0; i + lrecl <= f.length; i += lrecl) m.put(text(f, i, key), Arrays.copyOfRange(f, i, i + lrecl));
        return m;
    }

    static String accountFields(byte[] a) {
        if (a == null) return "(record not in the output ACCTDATA)";
        return "ACCT-CURR-BAL " + raw(a, 12, 12) + " = " + Fields.zoned(a, 12, 12, 2).toPlainString() + ", ACCT-CREDIT-LIMIT " + raw(a, 24, 12)
                + " = " + Fields.zoned(a, 24, 12, 2).toPlainString() + ", ACCT-CASH-CREDIT-LIMIT " + raw(a, 36, 12) + ", ACCT-EXPIRAION-DATE " + raw(a, 58, 10)
                + ", ACCT-CURR-CYC-CREDIT " + raw(a, 78, 12) + " = " + Fields.zoned(a, 78, 12, 2).toPlainString() + ", ACCT-CURR-CYC-DEBIT " + raw(a, 90, 12)
                + " = " + Fields.zoned(a, 90, 12, 2).toPlainString();
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

    static byte[] find(byte[] f, int lrecl, int off, int len, String key) {
        for (int i = 0; i + lrecl <= f.length; i += lrecl)
            if (text(f, i + off, len).equals(key)) return Arrays.copyOfRange(f, i, i + lrecl);
        return null;
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

    static byte[] concat(byte[]... parts) {
        var o = new ByteArrayOutputStream();
        for (byte[] p : parts) o.writeBytes(p);
        return o.toByteArray();
    }
}
