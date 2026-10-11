package com.mlcpu.capabilities.transactionreporting;

import static com.mlcpu.forwardexport.Jw.obj;

import com.mlcpu.forwardexport.CardDemoJson;
import com.mlcpu.forwardexport.Jw;
import com.mlcpu.rt.StepEquivalence;
import java.io.IOException;
import java.math.BigDecimal;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.Set;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/** Forward-engineering export (forward/cas): every CBTRN03C equivalence scenario run by the faithful program, its report parsed. */
class ForwardExportTest {
    static final String AT = "2022-07-06T08:00:00.000000";

    @BeforeAll
    static void oracle() {
        StepEquivalence.pinOracleEnvironment("2022-07-05T10:11:12-07:00");
    }

    @Test
    void export() throws IOException {
        String out = Jw.outDir() + "/reporting";
        Set<String> skip = Set.of("no date parameters", "totals overflow their pictures", "amounts stored unsigned (F-sign)", "date parameter file missing (abend)");
        Map<String, byte[]> base = TransactionReportEquivalenceTest.sorted();
        Map<String, Long> accountOf = CardDemoJson.accountOfCard(base.get(TransactionReportEquivalenceTest.XREF));
        int n = 0;
        for (var args : TransactionReportEquivalenceTest.scenarios().toList()) {
            String name = (String) args.get()[0];
            if (skip.contains(name)) continue;
            @SuppressWarnings("unchecked") Map<String, byte[]> in = (Map<String, byte[]>) args.get()[1];
            n++;
            String dates = new String(in.get(TransactionReportEquivalenceTest.DATES), StandardCharsets.ISO_8859_1);
            Map<String, Object> initial = obj("cards", CardDemoJson.cards(in.get(TransactionReportEquivalenceTest.XREF)),
                    "transactionTypes", CardDemoJson.transactionTypes(in.get(TransactionReportEquivalenceTest.TYPE)),
                    "transactionCategories", CardDemoJson.transactionCategories(in.get(TransactionReportEquivalenceTest.CATG)),
                    "ledger", CardDemoJson.ledger(in.get(TransactionReportEquivalenceTest.TRAN), accountOf));
            StepEquivalence.Outcome o = StepEquivalence.run(TransactionReportEquivalenceTest.step(), in, "legacy");
            byte[] report = o.files().entrySet().stream().filter(e -> e.getKey().contains("TRANREPT")).map(Map.Entry::getValue).findFirst().orElse(null);
            Jw.save(out, String.format("report-%02d.json", n), obj("name", name, "context", "reporting", "program", "CBTRN03C", "at", AT,
                    "initial", initial, "command", "transactionReport",
                    "input", obj("startDate", dates.substring(0, 10), "endDate", dates.substring(11, 21)),
                    "legacy", obj("returnCode", o.returnCode(), "detail", o.detail(), "report", parse(report), "sysout", CardDemoJson.sysout(o))));
        }
    }

    /** The report's detail lines, account totals and grand total. */
    static Map<String, Object> parse(byte[] report) {
        List<Object> lines = new ArrayList<>(), accountTotals = new ArrayList<>(), pageTotals = new ArrayList<>();
        Object grand = null;
        if (report != null) {
            String text = new String(report, StandardCharsets.ISO_8859_1);
            for (int i = 0; i + 1 <= text.length(); i += 133) {
                String l = text.substring(i, Math.min(text.length(), i + 133));
                if (l.matches("^\\d{16} \\d{11} .*")) {
                    lines.add(obj("transactionId", l.substring(0, 16), "accountId", Long.parseLong(l.substring(17, 28)), "typeCode", l.substring(29, 31),
                            "typeDescription", l.substring(32, 47).stripTrailing(), "categoryCode", Integer.parseInt(l.substring(48, 52)),
                            "categoryDescription", l.substring(53, 82).stripTrailing(), "source", l.substring(83, 93).stripTrailing(),
                            "amount", amount(l.substring(93))));
                } else if (l.startsWith("Account Total")) accountTotals.add(amount(l.substring(97)));
                else if (l.startsWith("Page Total")) pageTotals.add(amount(l.substring(97)));
                else if (l.startsWith("Grand Total")) grand = amount(l.substring(97));
            }
        }
        return obj("lines", lines, "accountTotals", accountTotals, "pageTotals", pageTotals, "grandTotal", grand);
    }

    static BigDecimal amount(String s) {
        String t = s.replace(",", "").replace(" ", "").replace("+", "").trim();
        boolean negative = t.startsWith("-") || t.endsWith("-");
        t = t.replace("-", "");
        if (t.isEmpty()) return null;
        BigDecimal v = new BigDecimal(t);
        return negative ? v.negate() : v;
    }
}
