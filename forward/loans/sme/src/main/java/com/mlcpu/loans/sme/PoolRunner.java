package com.mlcpu.loans.sme;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.node.ArrayNode;
import com.fasterxml.jackson.databind.node.ObjectNode;
import com.mlcpu.loans.api.Calculation;
import com.mlcpu.loans.api.CurrencyTerms;
import com.mlcpu.loans.api.DisbursementLine;
import com.mlcpu.loans.api.DownPaymentLine;
import com.mlcpu.loans.api.Frequency;
import com.mlcpu.loans.api.InstallmentLine;
import com.mlcpu.loans.api.InterestMethod;
import com.mlcpu.loans.api.MonthDays;
import com.mlcpu.loans.api.Rejected;
import com.mlcpu.loans.api.Schedule;
import com.mlcpu.loans.api.ScheduleLine;
import com.mlcpu.loans.api.ScheduleOutcome;
import com.mlcpu.loans.api.ScheduleRequest;
import com.mlcpu.loans.api.ScheduleService;
import com.mlcpu.loans.api.Scheduled;
import com.mlcpu.loans.api.YearDays;
import com.mlcpu.loans.application.LoansApplication;
import java.io.BufferedReader;
import java.io.IOException;
import java.io.PrintStream;
import java.math.BigDecimal;
import java.math.RoundingMode;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.LocalDate;
import java.util.ArrayList;
import java.util.Iterator;
import java.util.List;
import java.util.Map;
import java.util.TreeMap;
import java.util.stream.Stream;

/**
 * Runs the schedule pool (S5, sme/pool/schedule/*.jsonl) through the reference service and counts the scenarios whose outcome
 * differs from the expected one. The pool's expected values already carry the decisions (D-2 from oracle A'), so every
 * difference is a failure. Usage: PoolRunner &lt;pool dir&gt; [max loans per file, 0 = all] [file name filter] [diff file].
 */
public final class PoolRunner {

    private static final ObjectMapper JSON = new ObjectMapper();

    public static void main(String[] args) throws IOException {
        Path dir = Path.of(args[0]);
        int max = args.length > 1 ? Integer.parseInt(args[1]) : 0;
        String filter = args.length > 2 ? args[2] : "";
        PrintStream diffs = args.length > 3 ? new PrintStream(Files.newOutputStream(Path.of(args[3])), true, StandardCharsets.UTF_8)
                : System.out;
        List<Path> files;
        try (Stream<Path> s = Files.list(dir)) {
            files = s.filter(p -> p.getFileName().toString().endsWith(".jsonl") && p.getFileName().toString().contains(filter)).sorted()
                    .toList();
        }
        ScheduleService service = LoansApplication.quotes();
        Map<String, Integer> kinds = new TreeMap<>();
        long total = 0;
        long failed = 0;
        long started = System.nanoTime();
        for (Path f : files) {
            int n = 0;
            int bad = 0;
            try (BufferedReader r = Files.newBufferedReader(f, StandardCharsets.UTF_8)) {
                String line;
                while ((line = r.readLine()) != null && (max == 0 || n < max)) {
                    JsonNode scenario = JSON.readTree(line);
                    JsonNode step = scenario.get("steps").get(0);
                    JsonNode expected = step.get("expected");
                    JsonNode actual;
                    try {
                        actual = toJson(service.schedule(request(step.get("input"))));
                    } catch (RuntimeException | StackOverflowError e) {
                        ObjectNode o = JSON.createObjectNode();
                        o.put("@type", "Exception");
                        o.put("exception", e.toString());
                        actual = o;
                    }
                    n++;
                    String diff = firstDifference("", expected, actual);
                    if (diff != null) {
                        bad++;
                        String kind = kind(expected, actual, diff);
                        kinds.merge(kind, 1, Integer::sum);
                        diffs.println(scenario.get("id").asText() + "  " + kind + "  " + diff);
                    }
                }
            }
            total += n;
            failed += bad;
            System.out.printf("%-28s %7d loans %7d differ%n", f.getFileName().toString().replace(".jsonl", ""), n, bad);
            System.out.flush();
        }
        System.out.printf("TOTAL %d loans, %d differ, %.1f s%n", total, failed, (System.nanoTime() - started) / 1e9);
        kinds.forEach((k, v) -> System.out.printf("  %7d  %s%n", v, k));
    }

    private static String kind(JsonNode expected, JsonNode actual, String diff) {
        String e = expected.path("@type").asText();
        String a = actual.path("@type").asText();
        if (!e.equals(a)) {
            return e + "->" + a + ("Rejected".equals(e) ? " " + expected.path("reason").asText() : "")
                    + ("Rejected".equals(a) ? " " + actual.path("reason").asText() : "")
                    + ("Exception".equals(a) ? " " + actual.path("exception").asText().replaceAll(":.*", "") : "");
        }
        if ("Rejected".equals(e)) {
            return "reason " + expected.path("reason").asText() + "->" + actual.path("reason").asText();
        }
        String path = diff.substring(0, diff.indexOf(' '));
        return "field " + path.replaceAll("\\[\\d+\\]", "[]");
    }

    /** The first path where the two differ, with both values; null when they are equal. */
    private static String firstDifference(String path, JsonNode e, JsonNode a) {
        if (e == null || a == null || e.getNodeType() != a.getNodeType()) {
            return path + " expected " + e + " actual " + a;
        }
        if (e.isObject()) {
            Iterator<String> names = e.fieldNames();
            while (names.hasNext()) {
                String name = names.next();
                String d = firstDifference(path + "." + name, e.get(name), a.get(name));
                if (d != null) {
                    return d;
                }
            }
            if (a.size() != e.size()) {
                return path + " fields expected " + e.size() + " actual " + a.size();
            }
            return null;
        }
        if (e.isArray()) {
            if (e.size() != a.size()) {
                return path + ".size expected " + e.size() + " actual " + a.size();
            }
            for (int i = 0; i < e.size(); i++) {
                String d = firstDifference(path + "[" + i + "]", e.get(i), a.get(i));
                if (d != null) {
                    return d;
                }
            }
            return null;
        }
        return e.equals(a) ? null : path + " expected " + e + " actual " + a;
    }

    public static ScheduleRequest request(JsonNode in) {
        JsonNode c = in.get("currency");
        JsonNode calc = in.get("calculation");
        return new ScheduleRequest(in.get("loanId").asText(), LocalDate.parse(in.get("scheduleStart").asText()),
                new CurrencyTerms(c.get("code").asText(), c.get("decimalPlaces").asInt(),
                        c.get("roundingMultiple").isNull() ? null : c.get("roundingMultiple").asInt()),
                new BigDecimal(in.get("principal").asText()), LocalDate.parse(in.get("disbursementDate").asText()), in.get("installments").asInt(),
                in.get("every").asInt(), Frequency.valueOf(in.get("frequency").asText()), new BigDecimal(in.get("annualRatePercent").asText()),
                in.get("downPaymentPercent").isNull() ? null : new BigDecimal(in.get("downPaymentPercent").asText()),
                in.get("downPaymentEnabled").asBoolean(), MonthDays.valueOf(in.get("monthDays").asText()),
                YearDays.valueOf(in.get("yearDays").asText()), in.get("fixedLength").isNull() ? null : in.get("fixedLength").asInt(),
                InterestMethod.valueOf(in.get("interestMethod").asText()), in.get("fullTermTranche").asBoolean(),
                new Calculation(calc.get("precision").asInt(), RoundingMode.valueOf(calc.get("roundingMode").asText())));
    }

    public static JsonNode toJson(ScheduleOutcome outcome) {
        ObjectNode o = JSON.createObjectNode();
        if (outcome instanceof Rejected r) {
            o.put("@type", "Rejected");
            o.put("loanId", r.loanId());
            o.put("reason", r.reason().name());
            o.putNull("field");
            return o;
        }
        Schedule s = ((Scheduled) outcome).schedule();
        int dp = s.decimalPlaces();
        o.put("@type", "Scheduled");
        ObjectNode sc = o.putObject("schedule");
        sc.put("loanId", s.loanId());
        sc.put("currencyCode", s.currencyCode());
        sc.put("decimalPlaces", dp);
        sc.put("termDays", s.termDays());
        ObjectNode t = sc.putObject("totals");
        t.put("disbursed", money(s.totals().disbursed(), dp));
        t.put("principal", money(s.totals().principal(), dp));
        t.put("interest", money(s.totals().interest(), dp));
        t.put("fee", money(s.totals().fee(), dp));
        t.put("penalty", money(s.totals().penalty(), dp));
        t.put("repayment", money(s.totals().repayment(), dp));
        t.put("outstanding", money(s.totals().outstanding(), dp));
        ArrayNode lines = sc.putArray("lines");
        for (ScheduleLine line : s.lines()) {
            ObjectNode l = lines.addObject();
            if (line instanceof DisbursementLine d) {
                l.put("@type", "DisbursementLine");
                l.put("from", d.from().toString());
                l.put("due", d.due().toString());
                l.put("amount", money(d.amount(), dp));
                l.put("balanceAfter", money(d.balanceAfter(), dp));
            } else if (line instanceof DownPaymentLine d) {
                l.put("@type", "DownPaymentLine");
                l.put("number", d.number());
                l.put("from", d.from().toString());
                l.put("due", d.due().toString());
                l.put("principal", money(d.principal(), dp));
                l.put("totalDue", money(d.totalDue(), dp));
                l.put("balanceAfter", money(d.balanceAfter(), dp));
                l.put("remainingDue", money(d.remainingDue(), dp));
            } else if (line instanceof InstallmentLine d) {
                l.put("@type", "InstallmentLine");
                l.put("number", d.number());
                l.put("from", d.from().toString());
                l.put("due", d.due().toString());
                l.put("principal", money(d.principal(), dp));
                l.put("interest", money(d.interest(), dp));
                l.put("fee", money(d.fee(), dp));
                l.put("penalty", money(d.penalty(), dp));
                l.put("totalDue", money(d.totalDue(), dp));
                l.put("balanceAfter", money(d.balanceAfter(), dp));
                l.put("remainingDue", money(d.remainingDue(), dp));
            }
        }
        return o;
    }

    private static String money(BigDecimal v, int dp) {
        if (v.scale() == dp) {
            return v.toPlainString();
        }
        try {
            return v.setScale(dp, RoundingMode.UNNECESSARY).toPlainString();
        } catch (ArithmeticException e) {
            return v.toPlainString() + "(scale " + v.scale() + ")";
        }
    }
}
