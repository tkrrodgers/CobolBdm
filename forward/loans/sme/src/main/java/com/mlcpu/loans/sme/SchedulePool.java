package com.mlcpu.loans.sme;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.node.ArrayNode;
import com.mlcpu.loans.api.Rejected;
import com.mlcpu.loans.api.ScheduleOutcome;
import com.mlcpu.loans.api.ScheduleRequest;
import com.mlcpu.loans.api.ScheduleService;
import com.mlcpu.loans.application.LoansApplication;
import com.mlcpu.loans.calendar.RepaymentCalendar;
import com.mlcpu.loans.rates.InterestFactors;
import com.mlcpu.loans.kernel.Money;
import com.mlcpu.loans.schedule.Amortization;
import com.mlcpu.loans.schedule.AmortizationMutants;
import com.mlcpu.loans.schedule.AmortizationMutants.Mutant;
import com.mlcpu.loans.terms.TermsReview;
import java.io.BufferedReader;
import java.math.MathContext;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.EnumMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.TreeSet;
import java.util.concurrent.ConcurrentHashMap;
import java.util.stream.Stream;

/**
 * The schedule pool (slice S5, ForwardEngineeringFineract.md sections 7.2 to 7.4) from the schedule pool of the whole corpus
 * (pool/schedule, the faithful job's outcomes = oracle A; oracle A' for decision D-2): loans with at most {@code maxInstallments}
 * installments (section 13: long loans go to the sweep), at most {@code perFile} per scenario file. The reference must give each
 * one's outcome; one mutant per AMZ and PLAN rule (AmortizationMutants, generated from the reference) must be killed; tags by
 * killed mutants; selection and split as SlicePools.
 *
 * <p>Usage: SchedulePool &lt;pool/schedule dir&gt; &lt;scenario root&gt; &lt;scenarios.json&gt; &lt;report.md&gt; [per group 3] [per file 150] [max installments 60]
 */
public final class SchedulePool {

    private static final ObjectMapper JSON = new ObjectMapper();
    private static final String SLICE = "schedule";

    record Candidate(String id, JsonNode input, JsonNode expected, String origin, ScheduleRequest request, Set<String> tags, String outcome) {
    }

    public static void main(String[] args) throws IOException {
        Path pool = Path.of(args[0]);
        Path scenarioRoot = Path.of(args[1]);
        Path manifest = Path.of(args[2]);
        Path report = Path.of(args[3]);
        int perGroup = args.length > 4 ? Integer.parseInt(args[4]) : 3;
        int perFile = args.length > 5 ? Integer.parseInt(args[5]) : 150;
        int maxInstallments = args.length > 6 ? Integer.parseInt(args[6]) : 60;
        List<JsonNode> read = new ArrayList<>();
        List<Path> files;
        try (Stream<Path> s = Files.list(pool)) {
            files = s.filter(p -> p.toString().endsWith(".jsonl")).sorted().toList();
        }
        for (Path f : files) {
            int n = 0;
            try (BufferedReader r = Files.newBufferedReader(f, StandardCharsets.UTF_8)) {
                String line;
                while ((line = r.readLine()) != null) {
                    JsonNode sc = JSON.readTree(line);
                    if (rare(sc)) {
                        read.add(sc);
                        continue;
                    }
                    if (n >= perFile || sc.get("steps").get(0).get("input").get("installments").asInt() > maxInstallments) continue;
                    read.add(sc);
                    n++;
                }
            }
        }
        ScheduleService reference = LoansApplication.quotes();
        TermsReview terms = new TermsReview();
        List<String> exampleProblems = examples(reference);
        Map<Mutant, Integer> kills = new ConcurrentHashMap<>();
        java.util.concurrent.atomic.AtomicInteger trm14 = new java.util.concurrent.atomic.AtomicInteger();
        List<String> problems = java.util.Collections.synchronizedList(new ArrayList<>());
        Set<String> mustKeep = ConcurrentHashMap.newKeySet();
        java.util.function.Function<JsonNode, Candidate> candidate = sc -> {
            JsonNode step = sc.get("steps").get(0);
            ScheduleRequest q = PoolRunner.request(step.get("input"));
            JsonNode expected = step.get("expected");
            JsonNode actual = PoolRunner.toJson(reference.schedule(q));
            if (!actual.equals(expected)) {
                problems.add("the reference differs on " + sc.get("id").asText());
                return null;
            }
            Set<String> tags = new TreeSet<>();
            for (Mutant m : Mutant.values()) {
                JsonNode got;
                try {
                    got = PoolRunner.toJson(mutated(q, terms, m));
                } catch (RuntimeException | StackOverflowError e) {
                    got = JSON.createObjectNode().put("exception", e.toString());
                }
                if (!got.equals(expected)) {
                    tags.add(m.rule());
                    kills.merge(m, 1, Integer::sum);
                }
            }
            // TRM-14 (the down payment) shows only in a schedule: the terms slice's rule, tagged here
            if (terms.review(q) == null && q.downPaymentEnabled()
                    && !PoolRunner.toJson(new Amortization(new RepaymentCalendar(), new InterestFactors()).schedule(q, downPaymentShareUnrounded(q))).equals(expected)) {
                tags.add("TRM-14");
                trm14.incrementAndGet();
            }
            String outcome = "Scheduled".equals(expected.path("@type").asText()) ? "Scheduled" : "Rejected " + expected.path("reason").asText();
            return new Candidate(sc.get("id").asText(), step.get("input"), expected, sc.get("origin").asText(), q, tags, outcome);
        };
        List<Candidate> all = new ArrayList<>(read.parallelStream().map(candidate).filter(java.util.Objects::nonNull).toList());
        // A mutant fewer than 3 capped candidates kill: its killers are looked for in the rest of the pool (long loans included,
        // FE 13's sweep brought in where a rule shows only there) and kept whatever the selection's groups.
        List<Mutant> weak = java.util.Arrays.stream(Mutant.values()).filter(m -> kills.getOrDefault(m, 0) < 3).toList();
        if (!weak.isEmpty()) {
            Set<String> have = new java.util.HashSet<>();
            read.forEach(sc -> have.add(sc.get("id").asText()));
            Map<Mutant, List<JsonNode>> found = new ConcurrentHashMap<>();
            for (Path f : files) {
                if (weak.stream().allMatch(m -> found.getOrDefault(m, List.of()).size() + kills.getOrDefault(m, 0) >= 3)) break;
                List<JsonNode> rest = new ArrayList<>();
                try (BufferedReader r = Files.newBufferedReader(f, StandardCharsets.UTF_8)) {
                    String line;
                    while ((line = r.readLine()) != null) {
                        JsonNode sc = JSON.readTree(line);
                        if (!have.contains(sc.get("id").asText())) rest.add(sc);
                    }
                }
                rest.parallelStream().forEach(sc -> {
                    JsonNode step = sc.get("steps").get(0);
                    ScheduleRequest q = PoolRunner.request(step.get("input"));
                    for (Mutant m : weak) {
                        if (found.getOrDefault(m, List.of()).size() + kills.getOrDefault(m, 0) >= 3) continue;
                        JsonNode got;
                        try {
                            got = PoolRunner.toJson(mutated(q, terms, m));
                        } catch (RuntimeException | StackOverflowError e) {
                            got = JSON.createObjectNode().put("exception", e.toString());
                        }
                        if (!got.equals(step.get("expected"))) {
                            synchronized (found) {
                                List<JsonNode> l = found.computeIfAbsent(m, k -> new ArrayList<>());
                                if (l.size() + kills.getOrDefault(m, 0) < 3) l.add(sc);
                            }
                        }
                    }
                });
            }
            Map<String, JsonNode> extra = new java.util.LinkedHashMap<>();
            found.values().forEach(l -> l.forEach(sc -> extra.putIfAbsent(sc.get("id").asText(), sc)));
            for (JsonNode sc : extra.values()) {
                Candidate c = candidate.apply(sc);
                if (c != null) {
                    all.add(c);
                    mustKeep.add(c.id());
                }
            }
            System.out.printf("  weak mutants %s: %d more candidate(s) from beyond the caps%n", weak, extra.size());
        }
        for (Mutant m : Mutant.values()) {
            if (kills.getOrDefault(m, 0) == 0) problems.add("rule " + m.rule() + ": no candidate kills its mutant (" + m.change + ")");
        }
        problems.addAll(exampleProblems);
        if (trm14.get() == 0) problems.add("rule TRM-14: no candidate kills its mutant (the down payment's share not rounded to the precision)");
        System.out.printf("  TRM-14 killed by %d candidate(s)%n", trm14.get());

        SlicePools.Chosen<Candidate> chosen = SlicePools.choose(all, Candidate::tags, Candidate::outcome,
                c -> c.tags().contains("AMZ-03") || mustKeep.contains(c.id()), perGroup,
                Comparator.comparing(Candidate::outcome).thenComparing(Candidate::id), "amz", c -> c.expected().toString().length(), "AMZ-01");
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
        Map<Mutant, Integer> ordered = new EnumMap<>(Mutant.class);
        ordered.putAll(kills);
        writeReport(report, read.size(), all, ordered, chosen, problems, maxInstallments, perFile);
        System.out.printf("schedule pool: %d read (at most %d installments, %d per file), the reference agrees on %d; %d chosen, %d public; %d problem(s)%n",
                read.size(), maxInstallments, perFile, all.size(), chosen.byId().size(), chosen.publicIds().size(), problems.size());
        ordered.forEach((m, n) -> System.out.printf("  %s killed by %d candidate(s)%n", m.rule(), n));
        problems.stream().limit(20).forEach(p -> System.out.println("  problem: " + p));
        if (!problems.isEmpty()) System.exit(1);
    }

    /**
     * Loans every pool keeps whatever the caps: the only ones that show a rule (the 7 loans whose 30-day months with yearly
     * installments are rejected before their full-term tranche: AMZ-03, PLAN-05).
     */
    static boolean rare(JsonNode sc) {
        JsonNode step = sc.get("steps").get(0);
        return step.get("input").get("fullTermTranche").asBoolean()
               && "PERIOD_NOT_SUPPORTED_WITH_THIRTY_DAY_MONTHS".equals(step.get("expected").path("reason").asText());
    }

    /** The examples of digests/schedule.md (the worked example, PLAN-01's order), on the reference: the digest's words checked. */
    public static List<String> examples(ScheduleService reference) {
        List<String> problems = new ArrayList<>();
        // the worked example: 15,000.00 USD on 2023-01-28, 3 months, 9.4822%, 30/360, precision 3 CEILING
        ScheduleRequest worked = new ScheduleRequest("EXAMPLE1", java.time.LocalDate.of(2023, 1, 28), new com.mlcpu.loans.api.CurrencyTerms("USD", 2, null),
                new java.math.BigDecimal("15000.0000"), java.time.LocalDate.of(2023, 1, 28), 3, 1, com.mlcpu.loans.api.Frequency.MONTHS,
                new java.math.BigDecimal("9.482200000"), null, false, com.mlcpu.loans.api.MonthDays.THIRTY, com.mlcpu.loans.api.YearDays.DAYS_360, null,
                com.mlcpu.loans.api.InterestMethod.DECLINING_BALANCE, false, new com.mlcpu.loans.api.Calculation(3, java.math.RoundingMode.CEILING));
        String got = lines(reference.schedule(worked));
        String want = "D 15000.00 15000.00; I1 4959.80 121.00 5080.80 10040.20 10161.60; I2 5000.10 80.70 5080.80 5040.10 5080.80; "
                + "I3 5040.10 40.70 5080.80 0.00 0.00";
        if (!got.equals(want)) problems.add("digests/schedule.md, the worked example: the reference gives " + got);
        // PLAN-01's example: periods of 30 days from 2024-01-15, the disbursement on 2024-02-14 (in period 2)
        ScheduleRequest order = new ScheduleRequest("EXAMPLE2", java.time.LocalDate.of(2024, 1, 15), new com.mlcpu.loans.api.CurrencyTerms("USD", 2, null),
                new java.math.BigDecimal("1000.0000"), java.time.LocalDate.of(2024, 2, 14), 6, 30, com.mlcpu.loans.api.Frequency.DAYS,
                new java.math.BigDecimal("12.000000000"), null, false, com.mlcpu.loans.api.MonthDays.ACTUAL, com.mlcpu.loans.api.YearDays.ACTUAL, null,
                com.mlcpu.loans.api.InterestMethod.DECLINING_BALANCE, false, new com.mlcpu.loans.api.Calculation(12, java.math.RoundingMode.HALF_EVEN));
        String o = lines(reference.schedule(order));
        if (!o.startsWith("I1 0.00 0.00 0.00") || !o.contains("; D 1000.00 1000.00; I2 ")) problems.add("digests/schedule.md, PLAN-01's example: the reference gives " + o);
        return problems;
    }

    /** A schedule's lines in short: D amount balance; Pn principal ...; In principal interest totalDue balance remainingDue. */
    private static String lines(com.mlcpu.loans.api.ScheduleOutcome o) {
        if (!(o instanceof com.mlcpu.loans.api.Scheduled sc)) return String.valueOf(o);
        List<String> out = new ArrayList<>();
        for (var l : sc.schedule().lines()) {
            if (l instanceof com.mlcpu.loans.api.DisbursementLine d) out.add("D " + d.amount() + " " + d.balanceAfter());
            else if (l instanceof com.mlcpu.loans.api.DownPaymentLine p) out.add("P" + p.number() + " " + p.principal() + " " + p.balanceAfter());
            else if (l instanceof com.mlcpu.loans.api.InstallmentLine i)
                out.add("I" + i.number() + " " + i.principal() + " " + i.interest() + " " + i.totalDue() + " " + i.balanceAfter() + " " + i.remainingDue());
        }
        return String.join("; ", out);
    }

    /** TRM-14's mutant: the down payment with the percentage's share not rounded to the calculation's precision. */
    static Money downPaymentShareUnrounded(ScheduleRequest q) {
        MathContext mc = new MathContext(q.calculation().precision(), q.calculation().roundingMode());
        Money principal = Money.of(q.principal(), q.currency().decimalPlaces(), mc);
        if (principal.amount().signum() <= 0) return principal.zero();
        return principal.of(principal.amount().multiply(q.downPaymentPercent().movePointLeft(2), mc));
    }

    /** The quote with the mutated schedule: the terms review, then the mutant amortization. */
    static ScheduleOutcome mutated(ScheduleRequest q, TermsReview terms, Mutant m) {
        Rejected r = terms.review(q);
        if (r != null) return r;
        return new AmortizationMutants(new RepaymentCalendar(), new InterestFactors(), m).schedule(q, terms.downPayment(q));
    }

    /** A scenario file: the header fields one per line, the input on one line, the expected outcome with one line per schedule line. */
    static String scenario(String id, Candidate c) throws IOException {
        StringBuilder sb = new StringBuilder("{\n");
        sb.append("  \"id\": ").append(JSON.writeValueAsString(id)).append(",\n");
        sb.append("  \"slice\": \"").append(SLICE).append("\",\n");
        sb.append("  \"origin\": ").append(JSON.writeValueAsString(c.origin())).append(",\n");
        sb.append("  \"rules\": ").append(JSON.writeValueAsString(c.tags())).append(",\n");
        sb.append("  \"steps\": [ {\n    \"call\": \"schedule\",\n    \"input\": ").append(JSON.writeValueAsString(c.input())).append(",\n");
        JsonNode e = c.expected();
        if (!"Scheduled".equals(e.path("@type").asText())) {
            sb.append("    \"expected\": ").append(JSON.writeValueAsString(e)).append("\n  } ]\n}\n");
            return sb.toString();
        }
        JsonNode s = e.get("schedule");
        sb.append("    \"expected\": {\"@type\":\"Scheduled\",\"schedule\":{\"loanId\":").append(JSON.writeValueAsString(s.get("loanId").asText()))
                .append(",\"currencyCode\":").append(JSON.writeValueAsString(s.get("currencyCode").asText()))
                .append(",\"decimalPlaces\":").append(s.get("decimalPlaces").asInt()).append(",\"termDays\":").append(s.get("termDays").asInt())
                .append(",\n      \"totals\":").append(JSON.writeValueAsString(s.get("totals"))).append(",\n      \"lines\":[");
        for (int i = 0; i < s.get("lines").size(); i++) {
            sb.append(i == 0 ? "\n        " : ",\n        ").append(JSON.writeValueAsString(s.get("lines").get(i)));
        }
        sb.append("\n      ]}}\n  } ]\n}\n");
        return sb.toString();
    }

    private static void writeReport(Path report, int read, List<Candidate> all, Map<Mutant, Integer> kills, SlicePools.Chosen<Candidate> chosen,
            List<String> problems, int maxInstallments, int perFile) throws IOException {
        StringBuilder sb = new StringBuilder("# Schedule pool (slice S5): verification and selection\n\n");
        sb.append("Built by `SchedulePool` from `pool/schedule` (the faithful job's outcomes = oracle A; oracle A' for D-2), loans of at most ")
                .append(maxInstallments).append(" installments, at most ").append(perFile).append(" per scenario file (FE 13: long loans go to the sweep).\n\n");
        sb.append("| | |\n| --- | --- |\n");
        sb.append("| Read | ").append(read).append(" |\n");
        sb.append("| Reference equal on | ").append(all.size()).append(" |\n");
        sb.append("| Chosen | ").append(chosen.byId().size()).append(": ").append(chosen.publicIds().size()).append(" public |\n\n");
        sb.append("## Rule mutants (generated by `tools/make_schedule_mutants.py`)\n\n| Rule | Mutant | Candidates that kill it | Chosen scenarios tagged | of them public |\n| --- | --- | --- | --- | --- |\n");
        for (Mutant m : Mutant.values()) {
            String rule = m.rule();
            long pub = chosen.publicIds().stream().filter(id -> chosen.byId().get(id).tags().contains(rule)).count();
            sb.append("| ").append(rule).append(" | ").append(m.change).append(" | ").append(kills.getOrDefault(m, 0)).append(" | ")
                    .append(chosen.ruleCount().getOrDefault(rule, 0)).append(" | ").append(pub).append(" |\n");
        }
        if (!problems.isEmpty()) {
            sb.append("\n## Problems\n\n");
            problems.forEach(p -> sb.append("- ").append(p).append('\n'));
        }
        Files.createDirectories(report.getParent());
        Files.writeString(report, sb.toString(), StandardCharsets.UTF_8);
    }
}
