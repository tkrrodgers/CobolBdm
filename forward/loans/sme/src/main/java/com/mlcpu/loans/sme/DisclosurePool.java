package com.mlcpu.loans.sme;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.node.ArrayNode;
import com.fasterxml.jackson.databind.node.ObjectNode;
import com.mlcpu.loans.api.Calculation;
import com.mlcpu.loans.api.CurrencyTerms;
import com.mlcpu.loans.api.Disclosed;
import com.mlcpu.loans.api.Frequency;
import com.mlcpu.loans.api.InterestMethod;
import com.mlcpu.loans.api.MonthDays;
import com.mlcpu.loans.api.YearDays;
import java.time.LocalDate;
import com.mlcpu.loans.api.DisclosureOutcome;
import com.mlcpu.loans.api.ScheduleOutcome;
import com.mlcpu.loans.api.ScheduleRequest;
import com.mlcpu.loans.api.ScheduleService;
import com.mlcpu.loans.api.Scheduled;
import com.mlcpu.loans.application.LoansApplication;
import com.mlcpu.loans.disclosure.DisclosureMutant;
import com.mlcpu.loans.disclosure.DisclosureProbes;
import java.io.BufferedReader;
import java.io.IOException;
import java.math.BigDecimal;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.EnumMap;
import java.util.List;
import java.util.Map;
import java.util.Objects;
import java.util.Set;
import java.util.TreeMap;
import java.util.TreeSet;
import java.util.concurrent.ConcurrentHashMap;
import java.util.stream.Stream;

/**
 * The disclosure pool (slice S6, ForwardEngineeringFineract.md sections 7.4 and 7.5): the schedule document (RPT) and the cost
 * of credit (DISC). Document candidates come from the faithful job's printed schedules (tools/build_document_candidates.py,
 * each checked against the S5 pool); the reference must give each one. Disclosure candidates are the S5 pool's loans (at most
 * {@code maxInstallments} installments, {@code perFile} per file): there is no legacy oracle, so the expected value is the
 * reference's, checked by FE 7.5's closed forms here and by an independent implementation (tools/check_disclosure.py) on every
 * candidate. One mutant per rule (generated, tools/make_disclosure_mutants.py) must be killed; tags by killed mutants; selection
 * per tag set, call, outcome and unit-period kind; split as SlicePools.
 *
 * <p>Usage: DisclosurePool &lt;document candidates&gt; &lt;pool/schedule&gt; &lt;scenario root&gt; &lt;scenarios.json&gt; &lt;report.md&gt;
 * &lt;disclose candidates out&gt; [per group 3] [per file 150] [max installments 60]
 */
public final class DisclosurePool {

    private static final ObjectMapper JSON = new ObjectMapper();
    private static final String SLICE = "disclosure";

    record Cand(String id, String call, JsonNode input, JsonNode expected, String origin, Set<String> tags, String outcome, String unit) {
    }

    public static void main(String[] args) throws IOException {
        Path documentCandidates = Path.of(args[0]);
        Path pool = Path.of(args[1]);
        Path scenarioRoot = Path.of(args[2]);
        Path manifest = Path.of(args[3]);
        Path report = Path.of(args[4]);
        Path discloseOut = Path.of(args[5]);
        int perGroup = args.length > 6 ? Integer.parseInt(args[6]) : 3;
        int perFile = args.length > 7 ? Integer.parseInt(args[7]) : 150;
        int maxInstallments = args.length > 8 ? Integer.parseInt(args[8]) : 60;
        ScheduleService quotes = LoansApplication.quotes();
        List<String> problems = Collections.synchronizedList(new ArrayList<>());
        Map<DisclosureMutant, Integer> kills = new ConcurrentHashMap<>();
        Map<String, Integer> checks = new ConcurrentHashMap<>();

        // the document: the printed schedules
        List<JsonNode> docs = new ArrayList<>();
        try (BufferedReader r = Files.newBufferedReader(documentCandidates, StandardCharsets.UTF_8)) {
            String line;
            while ((line = r.readLine()) != null) docs.add(JSON.readTree(line));
        }
        List<Cand> all = new ArrayList<>(docs.parallelStream().map(sc -> {
            JsonNode step = sc.get("steps").get(0);
            ScheduleRequest q = PoolRunner.request(step.get("input"));
            JsonNode expected = step.get("expected");
            if (!Canon.of(quotes.document(q)).equals(expected)) {
                problems.add("the reference's document differs on " + sc.get("id").asText());
                return null;
            }
            ScheduleOutcome s = quotes.schedule(q);
            Set<String> tags = tags(s, expected, true, kills);
            String unit = s instanceof Scheduled ok ? DisclosureProbes.unitKind(ok.schedule()) : "-";
            return new Cand(sc.get("id").asText(), "document", step.get("input"), expected, sc.get("origin").asText(), tags,
                    outcome(expected), unit);
        }).filter(Objects::nonNull).toList());

        // the disclosure: the S5 pool's loans
        List<JsonNode> loans = new ArrayList<>();
        List<Path> files;
        try (Stream<Path> s = Files.list(pool)) {
            files = s.filter(p -> p.toString().endsWith(".jsonl")).sorted().toList();
        }
        for (Path f : files) {
            int n = 0;
            try (BufferedReader r = Files.newBufferedReader(f, StandardCharsets.UTF_8)) {
                String line;
                while ((line = r.readLine()) != null && n < perFile) {
                    JsonNode sc = JSON.readTree(line);
                    if (sc.get("steps").get(0).get("input").get("installments").asInt() > maxInstallments) continue;
                    loans.add(sc);
                    n++;
                }
            }
        }
        List<Cand> disclosed = loans.parallelStream().map(sc -> {
            JsonNode step = sc.get("steps").get(0);
            ScheduleRequest q = PoolRunner.request(step.get("input"));
            ScheduleOutcome s = quotes.schedule(q);
            if (!PoolRunner.toJson(s).equals(step.get("expected"))) {
                problems.add("the reference's schedule differs on " + sc.get("id").asText());
                return null;
            }
            DisclosureOutcome d = quotes.disclose(q);
            JsonNode expected = Canon.of(d);
            if (s instanceof Scheduled ok && d instanceof Disclosed dd) {
                BigDecimal closed = DisclosureProbes.closedForm(ok.schedule(), dd.disclosure().amountFinanced(), dd.disclosure().financeCharge());
                if (closed != null) {
                    checks.merge(dd.disclosure().financeCharge().signum() == 0 ? "zero finance charge" : "one payment within a year", 1, Integer::sum);
                    if (!closed.equals(dd.disclosure().annualPercentageRate())) {
                        problems.add("closed form " + closed + " but the reference's APR " + dd.disclosure().annualPercentageRate() + " on " + sc.get("id").asText());
                    }
                }
            }
            Set<String> tags = tags(s, expected, false, kills);
            String unit = s instanceof Scheduled ok ? DisclosureProbes.unitKind(ok.schedule()) : "-";
            String origin = sc.get("origin").asText().replaceFirst(":.*", "") + ": the schedule as the S5 pool's; the disclosure is the reference's (no legacy "
                    + "oracle), checked independently";
            return new Cand(sc.get("id").asText(), "disclose", step.get("input"), expected, origin, tags, outcome(expected), unit);
        }).filter(Objects::nonNull).toList();
        all.addAll(disclosed);
        // SME probes at DISC-08/09's boundaries (the unit-period's thresholds, ties, the average): expected values the reference's,
        // checked like the others (closed forms here, tools/check_disclosure.py on the candidates file); kept whatever the groups
        Set<String> probeIds = new TreeSet<>();
        List<Cand> probeCands = new ArrayList<>();
        int pn = 0;
        for (var probe : probes()) {
            ScheduleRequest q = probe.getValue();
            ScheduleOutcome sch = quotes.schedule(q);
            if (!(sch instanceof Scheduled)) {
                problems.add("probe " + probe.getKey() + ": not scheduled (" + sch + ")");
                continue;
            }
            JsonNode expected = Canon.of(quotes.disclose(q));
            Set<String> tags = tags(sch, expected, false, kills);
            String id = String.format("probe-%02d", ++pn);
            ObjectNode in = JSON.valueToTree(Canon.of(q));
            Cand c = new Cand(id, "disclose", in, expected, "SME probe (DISC-08, DISC-09): " + probe.getKey()
                    + "; the disclosure is the reference's, checked independently", tags, outcome(expected), DisclosureProbes.unitKind(((Scheduled) sch).schedule()));
            probeCands.add(c);
            probeIds.add(id);
        }
        all.addAll(probeCands);
        for (DisclosureMutant m : DisclosureMutant.values()) {
            if (kills.getOrDefault(m, 0) == 0) problems.add("rule " + m.rule() + ": no candidate kills its mutant (" + m.change + ")");
        }

        // every disclose candidate, for the independent check
        Files.createDirectories(discloseOut.toAbsolutePath().getParent());
        StringBuilder out = new StringBuilder();
        for (Cand c : disclosed) out.append(JSON.writeValueAsString(scenarioNode(c.id(), c))).append('\n');
        for (Cand c : probeCands) {
            ObjectNode node = scenarioNode(c.id(), c);
            // a probe is no pool loan: its schedule (the reference's) goes with it for the independent check
            node.set("schedule", PoolRunner.toJson(quotes.schedule(PoolRunner.request(c.input()))));
            out.append(JSON.writeValueAsString(node)).append('\n');
        }
        Files.writeString(discloseOut, out.toString(), StandardCharsets.UTF_8);

        SlicePools.Chosen<Cand> chosen = SlicePools.choose(all, Cand::tags, c -> c.call() + "|" + c.outcome() + "|" + c.unit(), c -> probeIds.contains(c.id()), perGroup,
                Comparator.comparing(Cand::call).thenComparing(Cand::outcome).thenComparing(Cand::id), "dsc", c -> c.expected().toString().length(), "RPT-01");
        SlicePools.clear(scenarioRoot, SLICE);
        ArrayNode entries = JSON.createArrayNode();
        for (var e : chosen.byId().entrySet()) {
            String visibility = chosen.publicIds().contains(e.getKey()) ? "public" : "hidden";
            Files.writeString(scenarioRoot.resolve(visibility).resolve(SLICE).resolve(e.getKey() + ".json"), scenario(e.getKey(), e.getValue()),
                    StandardCharsets.UTF_8);
            SlicePools.entry(entries, e.getKey(), SLICE, visibility, chosen.primary().get(e.getKey()), e.getValue().tags(), e.getValue().origin());
        }
        SlicePools.writeManifest(manifest, SLICE, entries);
        Map<DisclosureMutant, Integer> ordered = new EnumMap<>(DisclosureMutant.class);
        ordered.putAll(kills);
        writeReport(report, docs.size(), loans.size(), all, ordered, chosen, checks, problems, maxInstallments, perFile);
        System.out.printf("disclosure pool: %d document and %d disclose candidates, %d chosen, %d public; %d problem(s)%n", docs.size(), disclosed.size(),
                chosen.byId().size(), chosen.publicIds().size(), problems.size());
        ordered.forEach((m, n) -> System.out.printf("  %s killed by %d candidate(s)%n", m.rule(), n));
        checks.forEach((k, v) -> System.out.printf("  closed form, %s: %d%n", k, v));
        problems.stream().limit(20).forEach(p -> System.out.println("  problem: " + p));
        if (!problems.isEmpty()) System.exit(1);
    }

    /** The probes: a request each, 1,000.00 USD at 12% (ACTUAL/ACTUAL, precision 12 HALF_EVEN) on dates that hit one boundary. */
    static List<Map.Entry<String, ScheduleRequest>> probes() {
        List<Map.Entry<String, ScheduleRequest>> p = new ArrayList<>();
        p.add(Map.entry("every 7 days: one week", probe(3, 7, Frequency.DAYS, 0, null)));
        p.add(Map.entry("every 364 days: 52 weeks", probe(3, 364, Frequency.DAYS, 0, null)));
        p.add(Map.entry("every 365 days: 365 days, 1 a year", probe(3, 365, Frequency.DAYS, 0, null)));
        p.add(Map.entry("every 366 days: more than 365, a year", probe(3, 366, Frequency.DAYS, 0, null)));
        p.add(Map.entry("every 11 months: 11 months", probe(3, 11, Frequency.MONTHS, 0, null)));
        p.add(Map.entry("every 12 months: a year", probe(3, 12, Frequency.MONTHS, 0, null)));
        p.add(Map.entry("every 2 years: a year (at most one)", probe(3, 2, Frequency.YEARS, 0, null)));
        p.add(Map.entry("2 payments 14 and 15 days out: no common period, average 14.5 between 2 weeks and a semimonth (the shorter)",
                probe(2, 15, Frequency.DAYS, 1, null)));
        p.add(Map.entry("2 payments 25 and 30 days out: average 27.5, nearest 4 weeks", probe(2, 30, Frequency.DAYS, 5, null)));
        p.add(Map.entry("2 payments 10 and 20 days out: average 15, a semimonth", probe(2, 20, Frequency.DAYS, 10, null)));
        p.add(Map.entry("periods 5, 10, 10, 5 days: two common periods as often, the shorter (5 days)", probe(4, 10, Frequency.DAYS, 5, 35)));
        p.add(Map.entry("monthly from the 31st: months back from month-ends", probeFrom(LocalDate.of(2024, 1, 31), LocalDate.of(2024, 1, 31), 4, 1, Frequency.MONTHS)));
        p.add(Map.entry("monthly from February 29th", probeFrom(LocalDate.of(2024, 2, 29), LocalDate.of(2024, 2, 29), 4, 1, Frequency.MONTHS)));
        p.add(Map.entry("one payment 13 months out: a year, t = 1, f = 1/12", probeFrom(LocalDate.of(2024, 1, 15), LocalDate.of(2024, 1, 15), 1, 13, Frequency.MONTHS)));
        p.add(Map.entry("one payment 400 days out: a year, the rest in days", probeFrom(LocalDate.of(2024, 1, 15), LocalDate.of(2024, 1, 15), 1, 400, Frequency.DAYS)));
        p.add(Map.entry("every 12 months, disbursed 20 days late: a year (not 12 months), the first period's days ÷ 365", probe(3, 12, Frequency.MONTHS, 20, null)));
        p.add(Map.entry("every 366 days, disbursed 20 days late: a year, not 366 days", probe(3, 366, Frequency.DAYS, 20, null)));
        p.add(Map.entry("every 365 days, disbursed 20 days late: 365 days", probe(3, 365, Frequency.DAYS, 20, null)));
        p.add(Map.entry("every 11 months, disbursed 20 days late: 11 months, 30-day counting", probe(3, 11, Frequency.MONTHS, 20, null)));
        p.add(Map.entry("every 30 days from 2024-04-01: periods of 1 month and of 30 days, as often (the days)",
                probeFrom(LocalDate.of(2024, 4, 1), LocalDate.of(2024, 4, 1), 4, 30, Frequency.DAYS)));
        p.add(Map.entry("every 9 days from the start, 4 payments, the last 4 days short: 9 days twice or more",
                probe(4, 9, Frequency.DAYS, 0, 32)));
        p.add(Map.entry("periods 3, 7, 7, 3 days: 3 days and 1 week as often, the shorter (3 days)", probe(4, 7, Frequency.DAYS, 4, 24)));
        return p;
    }

    private static ScheduleRequest probe(int n, int every, Frequency f, int lateDays, Integer fixedLength) {
        LocalDate start = LocalDate.of(2024, 1, 15);
        return request(start, start.plusDays(lateDays), n, every, f, fixedLength);
    }

    private static ScheduleRequest probeFrom(LocalDate start, LocalDate disbursed, int n, int every, Frequency f) {
        return request(start, disbursed, n, every, f, null);
    }

    private static ScheduleRequest request(LocalDate start, LocalDate disbursed, int n, int every, Frequency f, Integer fixedLength) {
        return new ScheduleRequest("PROBE", start, new CurrencyTerms("USD", 2, null), new BigDecimal("1000.0000"), disbursed, n, every, f,
                new BigDecimal("12.000000000"), null, false, MonthDays.ACTUAL, YearDays.ACTUAL, fixedLength, InterestMethod.DECLINING_BALANCE, false,
                new Calculation(12, java.math.RoundingMode.HALF_EVEN));
    }

    private static String outcome(JsonNode expected) {
        String t = expected.path("@type").asText();
        return t.equals("Rejected") ? "Rejected " + expected.path("reason").asText() : t;
    }

    /** The rules whose mutant this candidate kills (the document's or the disclosure's). */
    private static Set<String> tags(ScheduleOutcome s, JsonNode expected, boolean document, Map<DisclosureMutant, Integer> kills) {
        Set<String> tags = new TreeSet<>();
        for (DisclosureMutant m : DisclosureMutant.values()) {
            if (m.name().startsWith("RPT") != document) continue;
            JsonNode got;
            try {
                got = Canon.of(document ? DisclosureProbes.document(s, m) : DisclosureProbes.disclosure(s, m));
            } catch (RuntimeException e) {
                got = JSON.createObjectNode().put("exception", e.toString());
            }
            if (!got.equals(expected)) {
                tags.add(m.rule());
                kills.merge(m, 1, Integer::sum);
            }
        }
        return tags;
    }

    private static ObjectNode scenarioNode(String id, Cand c) {
        ObjectNode o = JSON.createObjectNode();
        o.put("id", id);
        o.put("slice", SLICE);
        o.put("origin", c.origin());
        ArrayNode rules = o.putArray("rules");
        c.tags().forEach(rules::add);
        ObjectNode step = o.putArray("steps").addObject();
        step.put("call", c.call());
        step.set("input", c.input());
        step.set("expected", c.expected());
        return o;
    }

    /** A scenario file: header fields one per line, the input on one line, a document with one line per document line. */
    private static String scenario(String id, Cand c) throws IOException {
        StringBuilder sb = new StringBuilder("{\n");
        sb.append("  \"id\": ").append(JSON.writeValueAsString(id)).append(",\n");
        sb.append("  \"slice\": \"").append(SLICE).append("\",\n");
        sb.append("  \"origin\": ").append(JSON.writeValueAsString(c.origin())).append(",\n");
        sb.append("  \"rules\": ").append(JSON.writeValueAsString(c.tags())).append(",\n");
        sb.append("  \"steps\": [ {\n    \"call\": \"").append(c.call()).append("\",\n    \"input\": ").append(JSON.writeValueAsString(c.input())).append(",\n");
        JsonNode e = c.expected();
        if (!"Documented".equals(e.path("@type").asText())) {
            sb.append("    \"expected\": ").append(JSON.writeValueAsString(e)).append("\n  } ]\n}\n");
            return sb.toString();
        }
        ObjectNode head = e.get("document").deepCopy();
        JsonNode lines = head.remove("lines");
        String h = JSON.writeValueAsString(head);
        sb.append("    \"expected\": {\"@type\":\"Documented\",\"document\":").append(h, 0, h.length() - 1).append(",\n      \"lines\":[");
        for (int i = 0; i < lines.size(); i++) sb.append(i == 0 ? "\n        " : ",\n        ").append(JSON.writeValueAsString(lines.get(i)));
        sb.append("\n      ]}}\n  } ]\n}\n");
        return sb.toString();
    }

    private static void writeReport(Path report, int documents, int loans, List<Cand> all, Map<DisclosureMutant, Integer> kills,
            SlicePools.Chosen<Cand> chosen, Map<String, Integer> checks, List<String> problems, int maxInstallments, int perFile) throws IOException {
        StringBuilder sb = new StringBuilder("# Disclosure pool (slice S6): verification and selection\n\n");
        sb.append("Built by `DisclosurePool`. Documents: the faithful job's printed schedules (`tools/build_document_candidates.py`, each equal to the ")
                .append("S5 pool's schedule by the RPT rules; D-2 balances from A'). Disclosures: the S5 pool's loans of at most ").append(maxInstallments)
                .append(" installments, at most ").append(perFile).append(" per file; the expected values are the reference's (no legacy oracle), checked by ")
                .append("FE 7.5's closed forms and by `tools/check_disclosure.py`.\n\n");
        sb.append("| | |\n| --- | --- |\n");
        sb.append("| Document candidates | ").append(documents).append(" |\n");
        sb.append("| Disclosure candidates | ").append(loans).append(" |\n");
        checks.forEach((k, v) -> sb.append("| Closed form: ").append(k).append(" | ").append(v).append(" equal |\n"));
        sb.append("| Chosen | ").append(chosen.byId().size()).append(": ").append(chosen.publicIds().size()).append(" public |\n\n");
        Map<String, Integer> units = new TreeMap<>();
        for (Cand c : all) if (c.call().equals("disclose")) units.merge(c.unit(), 1, Integer::sum);
        sb.append("Disclosure candidates by unit-period: ");
        units.forEach((k, v) -> sb.append(k).append(" ").append(v).append("; "));
        sb.append("\n\n## Rule mutants (generated by `tools/make_disclosure_mutants.py`)\n\n| Rule | Mutant | Candidates that kill it | Chosen scenarios tagged | of them public |\n| --- | --- | --- | --- | --- |\n");
        for (DisclosureMutant m : DisclosureMutant.values()) {
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
