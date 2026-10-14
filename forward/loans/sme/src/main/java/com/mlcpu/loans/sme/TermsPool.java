package com.mlcpu.loans.sme;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.node.ArrayNode;
import com.fasterxml.jackson.databind.node.ObjectNode;
import com.mlcpu.loans.api.Calculation;
import com.mlcpu.loans.api.CurrencyTerms;
import com.mlcpu.loans.api.Rejected;
import com.mlcpu.loans.api.ScheduleRejection;
import com.mlcpu.loans.api.ScheduleRequest;
import com.mlcpu.loans.terms.TermsReview;
import java.io.BufferedReader;
import java.io.IOException;
import java.math.BigDecimal;
import java.math.RoundingMode;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.LocalDate;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.EnumMap;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.TreeSet;
import java.util.function.UnaryOperator;
import java.util.stream.Stream;

/**
 * The terms pool (slice S4, ForwardEngineeringFineract.md sections 6 D-6, 7.2 to 7.4): terms the service rejects before any
 * calculation. Sources: the schedule pool's rejections by the terms review (the faithful job's, C); the corpus loans the legacy
 * envelope rejected that the service's limits still reject (the reference must name the field the legacy code stands for);
 * SME probes just outside each limit and pairs of violations for the order (decision D-6: the expected values are the rules').
 * One mutant per rule (TermsMutants); tags by killed mutants; selection and split as SlicePools.
 *
 * <p>Usage: TermsPool &lt;pool/schedule dir&gt; &lt;envelope.jsonl&gt; &lt;scenario root&gt; &lt;scenarios.json&gt; &lt;report.md&gt; [per group, 6]
 */
public final class TermsPool {

    private static final ObjectMapper JSON = new ObjectMapper();
    private static final String SLICE = "terms";
    private static final Set<String> TERMS_REASONS = Set.of("ROUNDING_MULTIPLE_NOT_OFFERED", "DOWN_PAYMENT_PERCENT_MISSING", "ROUNDING_MODE_NOT_OFFERED",
            "OUT_OF_RANGE");

    record Candidate(String key, ScheduleRequest request, Rejected expected, String origin, String source, Set<String> tags) {
    }

    public static void main(String[] args) throws IOException {
        Path schedulePool = Path.of(args[0]);
        Path envelope = Path.of(args[1]);
        Path scenarioRoot = Path.of(args[2]);
        Path manifest = Path.of(args[3]);
        Path report = Path.of(args[4]);
        int perGroup = args.length > 5 ? Integer.parseInt(args[5]) : 6;
        TermsReview reference = new TermsReview();
        List<String> problems = new ArrayList<>();
        Map<String, Candidate> all = new LinkedHashMap<>();
        Map<TermsMutants.Mutant, Integer> kills = new EnumMap<>(TermsMutants.Mutant.class);
        Map<String, Integer> sources = new LinkedHashMap<>();
        int acceptedUnderD6 = 0;
        ScheduleRequest base = null;

        // (a) the schedule pool: rejections by the terms review (C)
        List<Path> files;
        try (Stream<Path> s = Files.list(schedulePool)) {
            files = s.filter(p -> p.toString().endsWith(".jsonl")).sorted().toList();
        }
        for (Path f : files) {
            try (BufferedReader r = Files.newBufferedReader(f, StandardCharsets.UTF_8)) {
                String line;
                while ((line = r.readLine()) != null) {
                    JsonNode sc = JSON.readTree(line);
                    JsonNode step = sc.get("steps").get(0);
                    JsonNode e = step.get("expected");
                    ScheduleRequest q = PoolRunner.request(step.get("input"));
                    if (base == null && "Scheduled".equals(e.path("@type").asText()) && q.installments() >= 3 && q.fixedLength() == null
                            && !q.downPaymentEnabled()) {
                        base = q;
                    }
                    if (!"Rejected".equals(e.path("@type").asText()) || !TERMS_REASONS.contains(e.path("reason").asText())) continue;
                    Rejected expected = new Rejected(q.loanId(), ScheduleRejection.valueOf(e.get("reason").asText()),
                            e.get("field").isNull() ? null : e.get("field").asText());
                    add(all, q, expected, sc.get("origin").asText(), "schedule pool (C)", reference, problems, kills, sources);
                }
            }
        }
        // (b) the legacy envelope's rejections the service's limits still reject
        try (BufferedReader r = Files.newBufferedReader(envelope, StandardCharsets.UTF_8)) {
            String line;
            while ((line = r.readLine()) != null) {
                JsonNode c = JSON.readTree(line);
                ScheduleRequest q = PoolRunner.request(c.get("input"));
                Rejected actual = reference.review(q);
                if (actual == null) {
                    acceptedUnderD6++;
                    continue;
                }
                boolean fieldOk = false;
                for (JsonNode fld : c.get("fields")) fieldOk |= fld.asText().equals(actual.field());
                if (actual.reason() != ScheduleRejection.OUT_OF_RANGE || !fieldOk) {
                    problems.add(c.get("origin").asText() + ": the reference gives " + actual + ", the legacy code " + c.get("legacyCode").asText()
                            + " stands for " + c.get("fields"));
                    continue;
                }
                add(all, q, actual, c.get("origin").asText(), "legacy envelope (C), service limit D-6", reference, problems, kills, sources);
            }
        }
        // (c) SME probes (decision D-6)
        if (base == null) throw new IllegalStateException("no scheduled loan to base the probes on");
        for (var p : probes(base).entrySet()) {
            ScheduleRequest q = p.getValue().apply(base);
            Rejected expected = reference.review(q);
            if (expected == null) {
                problems.add("probe " + p.getKey() + ": the reference does not reject it");
                continue;
            }
            add(all, q, expected, "SME probe (decision D-6): " + p.getKey(), "SME probe", reference, problems, kills, sources);
        }
        for (TermsMutants.Mutant m : TermsMutants.Mutant.values()) {
            if (kills.getOrDefault(m, 0) == 0) problems.add("rule " + m.rule() + ": no candidate kills its mutant (" + m.change + ")");
        }

        List<Candidate> list = new ArrayList<>(all.values());
        SlicePools.Chosen<Candidate> chosen = SlicePools.choose(list, Candidate::tags,
                c -> c.expected().reason() + "|" + c.expected().field(), c -> c.source().equals("SME probe"), perGroup,
                Comparator.comparing((Candidate c) -> c.expected().reason().name()).thenComparing(c -> String.valueOf(c.expected().field()))
                        .thenComparing(Candidate::key), "trm", c -> c.key().length(), "TRM-11");
        SlicePools.clear(scenarioRoot, SLICE);
        ArrayNode entries = JSON.createArrayNode();
        for (var e : chosen.byId().entrySet()) {
            String id = e.getKey();
            Candidate c = e.getValue();
            String visibility = chosen.publicIds().contains(id) ? "public" : "hidden";
            Files.writeString(scenarioRoot.resolve(visibility).resolve(SLICE).resolve(id + ".json"), scenario(id, c), StandardCharsets.UTF_8);
            SlicePools.entry(entries, id, SLICE, visibility, chosen.primary().get(id), c.tags(), c.origin());
        }
        SlicePools.writeManifest(manifest, SLICE, entries);
        writeReport(report, list, kills, chosen, sources, acceptedUnderD6, problems);
        System.out.printf("terms pool: %d distinct candidates %s, %d legacy envelope rejects the service accepts (D-6); %d chosen, %d public; %d problem(s)%n",
                list.size(), sources, acceptedUnderD6, chosen.byId().size(), chosen.publicIds().size(), problems.size());
        kills.forEach((m, n) -> System.out.printf("  %s killed by %d candidate(s)%n", m.rule(), n));
        problems.stream().limit(20).forEach(p -> System.out.println("  problem: " + p));
        if (!problems.isEmpty()) System.exit(1);
    }

    private static void add(Map<String, Candidate> all, ScheduleRequest q, Rejected expected, String origin, String source, TermsReview reference,
            List<String> problems, Map<TermsMutants.Mutant, Integer> kills, Map<String, Integer> sources) throws IOException {
        ObjectNode in = (ObjectNode) requestJson(q);
        in.remove("loanId");
        String key = JSON.writeValueAsString(in);
        if (all.containsKey(key)) return;
        Rejected actual = reference.review(q);
        if (!expected.equals(actual)) {
            problems.add("the reference differs on " + origin + ": expected " + expected + ", reference " + actual);
            return;
        }
        Set<String> tags = new TreeSet<>();
        for (TermsMutants.Mutant m : TermsMutants.Mutant.values()) {
            if (!expected.equals(TermsMutants.review(q, m))) {
                tags.add(m.rule());
                kills.merge(m, 1, Integer::sum);
            }
        }
        all.put(key, new Candidate(key, q, expected, origin, source, tags));
        sources.merge(source, 1, Integer::sum);
    }

    /** Requests just outside each limit, and pairs of violations for the order of the rules (each from the base request). */
    static Map<String, UnaryOperator<ScheduleRequest>> probes(ScheduleRequest b) {
        Map<String, UnaryOperator<ScheduleRequest>> p = new LinkedHashMap<>();
        p.put("precision 0", q -> with(q, w -> w.calculation = new Calculation(0, RoundingMode.HALF_EVEN)));
        p.put("precision 35", q -> with(q, w -> w.calculation = new Calculation(35, RoundingMode.HALF_EVEN)));
        p.put("rounding mode UNNECESSARY", q -> with(q, w -> w.calculation = new Calculation(12, RoundingMode.UNNECESSARY)));
        p.put("0 installments", q -> with(q, w -> w.installments = 0));
        p.put("1201 installments", q -> with(q, w -> w.installments = 1201));
        p.put("principal 0", q -> with(q, w -> w.principal = new BigDecimal("0.00")));
        p.put("principal -100", q -> with(q, w -> w.principal = new BigDecimal("-100.00")));
        p.put("principal 10^15", q -> with(q, w -> w.principal = new BigDecimal("1000000000000000.00")));
        p.put("5 decimal places", q -> with(q, w -> w.currency = new CurrencyTerms("XAU", 5, null)));
        p.put("rate -0.5%", q -> with(q, w -> w.rate = new BigDecimal("-0.5")));
        p.put("rate 100.000000001%", q -> with(q, w -> w.rate = new BigDecimal("100.000000001")));
        p.put("rate with 10 decimals", q -> with(q, w -> w.rate = new BigDecimal("12.1234567891")));
        p.put("every 0", q -> with(q, w -> w.every = 0));
        p.put("every 10000", q -> with(q, w -> w.every = 10000));
        p.put("schedule start 1899-12-31", q -> with(q, w -> w.start = LocalDate.of(1899, 12, 31)));
        p.put("schedule start 2200-01-01", q -> with(q, w -> w.start = LocalDate.of(2200, 1, 1)));
        p.put("disbursement 1899-12-31", q -> with(q, w -> w.disbursed = LocalDate.of(1899, 12, 31)));
        p.put("disbursement 2200-01-01", q -> with(q, w -> w.disbursed = LocalDate.of(2200, 1, 1)));
        p.put("down payment -1%", q -> with(q, w -> { w.dpEnabled = true; w.dp = new BigDecimal("-1"); }));
        p.put("down payment 100.5%", q -> with(q, w -> { w.dpEnabled = true; w.dp = new BigDecimal("100.5"); }));
        p.put("fixed length at (n-1) x every", q -> with(q, w -> w.fixedLength = (w.installments - 1) * w.every));
        p.put("fixed length above (n+1) x every", q -> with(q, w -> w.fixedLength = (w.installments + 1) * w.every + 1));
        p.put("rounding multiple with 0 decimal places", q -> with(q, w -> w.currency = new CurrencyTerms("XOF", 0, 5)));
        p.put("down payment without a percentage", q -> with(q, w -> { w.dpEnabled = true; w.dp = null; }));
        p.put("precision 0 and 0 installments (precision first)", q -> with(q, w -> { w.calculation = new Calculation(0, RoundingMode.HALF_UP); w.installments = 0; }));
        p.put("0 installments and principal 0 (installments first)", q -> with(q, w -> { w.installments = 0; w.principal = new BigDecimal("0.00"); }));
        p.put("principal 0 and 5 decimal places (principal first)", q -> with(q, w -> { w.principal = new BigDecimal("0.00000"); w.currency = new CurrencyTerms("XAU", 5, null); }));
        p.put("5 decimal places and rate -1% (decimal places first)", q -> with(q, w -> { w.currency = new CurrencyTerms("XAU", 5, null); w.rate = new BigDecimal("-1"); }));
        p.put("rate 101% and every 0 (rate first)", q -> with(q, w -> { w.rate = new BigDecimal("101"); w.every = 0; }));
        p.put("every 0 and start 1899-12-31 (every first)", q -> with(q, w -> { w.every = 0; w.start = LocalDate.of(1899, 12, 31); }));
        p.put("start and disbursement 2200-01-01 (start first)", q -> with(q, w -> { w.start = LocalDate.of(2200, 1, 1); w.disbursed = LocalDate.of(2200, 1, 1); }));
        p.put("disbursement 1899-12-31 and down payment 101% (disbursement first)", q -> with(q, w -> { w.disbursed = LocalDate.of(1899, 12, 31); w.dpEnabled = true; w.dp = new BigDecimal("101"); }));
        p.put("down payment 101% and fixed length 0 (down payment first)", q -> with(q, w -> { w.dpEnabled = true; w.dp = new BigDecimal("101"); w.fixedLength = 0; }));
        p.put("fixed length 0 and rounding multiple (fixed length first)", q -> with(q, w -> { w.fixedLength = 0; w.currency = new CurrencyTerms("XOF", 0, 5); w.principal = new BigDecimal("1000"); }));
        p.put("rounding multiple and down payment without a percentage (multiple first)", q -> with(q, w -> { w.currency = new CurrencyTerms("XOF", 0, 5); w.principal = new BigDecimal("1000"); w.dpEnabled = true; w.dp = null; }));
        p.put("rounding mode UNNECESSARY and 0 installments (mode first)", q -> with(q, w -> { w.calculation = new Calculation(12, RoundingMode.UNNECESSARY); w.installments = 0; }));
        return p;
    }

    /** A mutable copy of a request's fields, for the probes. */
    static final class W {
        String loanId;
        LocalDate start;
        CurrencyTerms currency;
        BigDecimal principal;
        LocalDate disbursed;
        int installments;
        int every;
        com.mlcpu.loans.api.Frequency frequency;
        BigDecimal rate;
        BigDecimal dp;
        boolean dpEnabled;
        com.mlcpu.loans.api.MonthDays monthDays;
        com.mlcpu.loans.api.YearDays yearDays;
        Integer fixedLength;
        com.mlcpu.loans.api.InterestMethod method;
        boolean fullTerm;
        Calculation calculation;
    }

    static ScheduleRequest with(ScheduleRequest q, java.util.function.Consumer<W> change) {
        W w = new W();
        w.loanId = q.loanId();
        w.start = q.scheduleStart();
        w.currency = q.currency();
        w.principal = q.principal();
        w.disbursed = q.disbursementDate();
        w.installments = q.installments();
        w.every = q.every();
        w.frequency = q.frequency();
        w.rate = q.annualRatePercent();
        w.dp = q.downPaymentPercent();
        w.dpEnabled = q.downPaymentEnabled();
        w.monthDays = q.monthDays();
        w.yearDays = q.yearDays();
        w.fixedLength = q.fixedLength();
        w.method = q.interestMethod();
        w.fullTerm = q.fullTermTranche();
        w.calculation = q.calculation();
        change.accept(w);
        return new ScheduleRequest(w.loanId, w.start, w.currency, w.principal, w.disbursed, w.installments, w.every, w.frequency, w.rate, w.dp,
                w.dpEnabled, w.monthDays, w.yearDays, w.fixedLength, w.method, w.fullTerm, w.calculation);
    }

    static JsonNode requestJson(ScheduleRequest q) {
        ObjectNode o = JSON.createObjectNode();
        o.put("loanId", q.loanId());
        o.put("scheduleStart", q.scheduleStart().toString());
        ObjectNode c = o.putObject("currency");
        c.put("code", q.currency().code());
        c.put("decimalPlaces", q.currency().decimalPlaces());
        if (q.currency().roundingMultiple() == null) c.putNull("roundingMultiple");
        else c.put("roundingMultiple", q.currency().roundingMultiple());
        o.put("principal", q.principal().toPlainString());
        o.put("disbursementDate", q.disbursementDate().toString());
        o.put("installments", q.installments());
        o.put("every", q.every());
        o.put("frequency", q.frequency().name());
        o.put("annualRatePercent", q.annualRatePercent().toPlainString());
        if (q.downPaymentPercent() == null) o.putNull("downPaymentPercent");
        else o.put("downPaymentPercent", q.downPaymentPercent().toPlainString());
        o.put("downPaymentEnabled", q.downPaymentEnabled());
        o.put("monthDays", q.monthDays().name());
        o.put("yearDays", q.yearDays().name());
        if (q.fixedLength() == null) o.putNull("fixedLength");
        else o.put("fixedLength", q.fixedLength());
        o.put("interestMethod", q.interestMethod().name());
        o.put("fullTermTranche", q.fullTermTranche());
        ObjectNode calc = o.putObject("calculation");
        calc.put("precision", q.calculation().precision());
        calc.put("roundingMode", q.calculation().roundingMode().name());
        return o;
    }

    private static String scenario(String id, Candidate c) throws IOException {
        ObjectNode expected = JSON.createObjectNode();
        expected.put("@type", "Rejected");
        expected.put("loanId", c.request().loanId());
        expected.put("reason", c.expected().reason().name());
        if (c.expected().field() == null) expected.putNull("field");
        else expected.put("field", c.expected().field());
        return "{\n  \"id\": " + JSON.writeValueAsString(id) + ",\n  \"slice\": \"" + SLICE + "\",\n  \"origin\": " + JSON.writeValueAsString(c.origin())
                + ",\n  \"rules\": " + JSON.writeValueAsString(c.tags()) + ",\n  \"steps\": [ {\n    \"call\": \"schedule\",\n    \"input\": "
                + JSON.writeValueAsString(requestJson(c.request())) + ",\n    \"expected\": " + JSON.writeValueAsString(expected) + "\n  } ]\n}\n";
    }

    private static void writeReport(Path report, List<Candidate> all, Map<TermsMutants.Mutant, Integer> kills, SlicePools.Chosen<Candidate> chosen,
            Map<String, Integer> sources, int acceptedUnderD6, List<String> problems) throws IOException {
        StringBuilder sb = new StringBuilder("# Terms pool (slice S4): verification and selection\n\n");
        sb.append("Built by `TermsPool` from the schedule pool's terms rejections, the legacy envelope's rejections (`tools/build_terms_candidates.py`) and SME probes (decision D-6).\n\n");
        sb.append("| | |\n| --- | --- |\n");
        sources.forEach((k, v) -> sb.append("| Distinct candidates from ").append(k).append(" | ").append(v).append(" |\n"));
        sb.append("| Legacy envelope rejects the service accepts (D-6: wider limits; their schedules are privileged, slice S5) | ").append(acceptedUnderD6).append(" |\n");
        sb.append("| Reference (`TermsReview`) equal on | ").append(problems.stream().anyMatch(p -> p.startsWith("the reference")) ? "NOT ALL" : "all").append(" |\n");
        sb.append("| Chosen | ").append(chosen.byId().size()).append(": ").append(chosen.publicIds().size()).append(" public |\n\n");
        sb.append("## Rule mutants\n\n| Rule | Mutant | Candidates that kill it | Chosen scenarios tagged | of them public |\n| --- | --- | --- | --- | --- |\n");
        for (TermsMutants.Mutant m : TermsMutants.Mutant.values()) {
            String rule = m.rule();
            long pub = chosen.publicIds().stream().filter(id -> chosen.byId().get(id).tags().contains(rule)).count();
            sb.append("| ").append(rule).append(" | ").append(m.change).append(" | ").append(kills.getOrDefault(m, 0)).append(" | ")
                    .append(chosen.ruleCount().getOrDefault(rule, 0)).append(" | ").append(pub).append(" |\n");
        }
        sb.append("\nTRM-14 (the down payment's amount) is observable only in a schedule: the schedule slice's scenarios verify it.\n");
        if (!problems.isEmpty()) {
            sb.append("\n## Problems\n\n");
            problems.forEach(p -> sb.append("- ").append(p).append('\n'));
        }
        Files.createDirectories(report.getParent());
        Files.writeString(report, sb.toString(), StandardCharsets.UTF_8);
    }
}
