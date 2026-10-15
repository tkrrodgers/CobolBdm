package com.mlcpu.loans.sme;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.node.ArrayNode;
import com.fasterxml.jackson.databind.node.ObjectNode;
import com.mlcpu.loans.api.Calculation;
import com.mlcpu.loans.api.Frequency;
import com.mlcpu.loans.api.MonthDays;
import com.mlcpu.loans.api.YearDays;
import com.mlcpu.loans.api.spi.Factors;
import com.mlcpu.loans.api.spi.Period;
import com.mlcpu.loans.api.spi.RateFactors;
import com.mlcpu.loans.api.spi.RateOutcome;
import com.mlcpu.loans.api.spi.RateQuery;
import com.mlcpu.loans.api.spi.RateRejected;
import com.mlcpu.loans.rates.InterestFactors;
import com.mlcpu.loans.sme.RatesMutants.Mutant;
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
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.TreeSet;

/**
 * The rates pool (slice S3, ForwardEngineeringFineract.md sections 7.2 to 7.4) from the candidates decoded from FNEM0300's CALL
 * traces (tools/build_rates_candidates.py, tools/trace_rates.py). The traced factors (12 decimals in the model record) are given
 * with as many decimals as the calculation's precision, which must be exact; the reference must give every candidate's
 * outcome; each digest rule's mutant must be killed; a scenario's rule tags are the rules whose mutants it kills. Selection and
 * split as SlicePools.
 *
 * <p>Usage: RatesPool &lt;scenario root&gt; &lt;scenarios.json&gt; &lt;report.md&gt; &lt;per group&gt; &lt;candidates.jsonl&gt; ...
 */
public final class RatesPool {

    private static final ObjectMapper JSON = new ObjectMapper();
    private static final String SLICE = "rates";

    /** tags: the rules whose mutants it kills; selection: the mutants themselves (a rule's second mutant, RATE-05-B, apart). */
    record Candidate(String key, RateQuery query, Calculation calculation, JsonNode expected, String origin, boolean rejection, Set<String> tags,
            Set<String> selection) {
    }

    public static void main(String[] args) throws IOException {
        Path scenarioRoot = Path.of(args[0]);
        Path manifest = Path.of(args[1]);
        Path report = Path.of(args[2]);
        int perGroup = Integer.parseInt(args[3]);
        RateFactors reference = new InterestFactors();
        List<String> problems = new ArrayList<>();
        examples(reference, problems);
        List<Candidate> all = new ArrayList<>();
        Set<String> seen = new HashSet<>();
        Map<Mutant, Integer> kills = new EnumMap<>(Mutant.class);
        int read = 0;
        for (int f = 4; f < args.length; f++) {
            try (BufferedReader r = Files.newBufferedReader(Path.of(args[f]), StandardCharsets.UTF_8)) {
                String line;
                while ((line = r.readLine()) != null) {
                    read++;
                    JsonNode c = JSON.readTree(line);
                    RateQuery q = query(c.get("query"));
                    JsonNode cn = c.get("calculation");
                    Calculation calc = new Calculation(cn.get("precision").asInt(), RoundingMode.valueOf(cn.get("roundingMode").asText()));
                    boolean rejection = c.path("rejection").asBoolean(false);
                    String key = JSON.writeValueAsString(queryJson(q)) + JSON.writeValueAsString(cn) + rejection;
                    if (!seen.add(key)) continue;
                    JsonNode expected = scaled(c.get("expected"), calc, c.get("origin").asText(), problems);
                    if (expected == null) continue;
                    JsonNode actual = outcomeJson(reference.factors(q, calc));
                    if (!actual.equals(expected)) {
                        problems.add("the reference differs on " + c.get("origin").asText() + ": expected " + expected + ", reference " + actual);
                        continue;
                    }
                    Set<String> tags = new TreeSet<>();
                    Set<String> selection = new TreeSet<>();
                    for (Mutant m : Mutant.values()) {
                        if (!outcomeJson(RatesMutants.factors(q, calc, m)).equals(expected)) {
                            tags.add(m.rule());
                            selection.add(m.label());
                            kills.merge(m, 1, Integer::sum);
                        }
                    }
                    all.add(new Candidate(key, q, calc, expected, c.get("origin").asText(), rejection, tags, selection));
                }
            }
        }
        for (Mutant m : Mutant.values()) {
            if (kills.getOrDefault(m, 0) == 0) problems.add("rule " + m.rule() + ": no candidate kills its mutant (" + m.change + ")");
        }

        SlicePools.Chosen<Candidate> chosen = SlicePools.choose(all, Candidate::selection,
                c -> c.query().monthDays() + "|" + c.query().yearDays() + "|" + c.query().frequency(), Candidate::rejection, perGroup,
                Comparator.comparing((Candidate c) -> c.query().monthDays()).thenComparing(c -> c.query().frequency()).thenComparing(Candidate::key),
                "rate", c -> c.key().length(), "RATE-01");
        SlicePools.clear(scenarioRoot, SLICE);
        ArrayNode entries = JSON.createArrayNode();
        for (var e : chosen.byId().entrySet()) {
            String id = e.getKey();
            Candidate c = e.getValue();
            String visibility = chosen.publicIds().contains(id) ? "public" : "hidden";
            Files.writeString(scenarioRoot.resolve(visibility).resolve(SLICE).resolve(id + ".json"), scenario(id, c), StandardCharsets.UTF_8);
            SlicePools.entry(entries, id, SLICE, visibility, RatesMutants.rule(chosen.primary().get(id)), c.tags(), c.origin());
        }
        SlicePools.writeManifest(manifest, SLICE, entries);
        for (Mutant m : Mutant.values()) {
            if (kills.getOrDefault(m, 0) > 0 && chosen.byId().values().stream().noneMatch(c -> c.selection().contains(m.label()))) {
                problems.add("mutant " + m.label() + ": no chosen scenario kills it (" + m.change + ")");
            }
        }
        writeReport(report, read, all, kills, chosen, problems);
        System.out.printf("rates pool: %d candidate lines, %d distinct (reference agrees on all: %s), %d chosen, %d public; %d problem(s)%n", read,
                all.size(), problems.stream().noneMatch(p -> p.startsWith("the reference")), chosen.byId().size(), chosen.publicIds().size(), problems.size());
        kills.forEach((m, n) -> System.out.printf("  %s killed by %d candidate(s)%n", m.label(), n));
        problems.stream().limit(20).forEach(p -> System.out.println("  problem: " + p));
        if (!problems.isEmpty()) System.exit(1);
    }

    /** The worked examples of digests/rates.md, on the reference. */
    private static void examples(RateFactors reference, List<String> problems) {
        check(reference, problems, "RATE-04", "2023-12-01", "2023-12-01", "2024-01-31", "2023-12-01", "2024-01-31", "12", Frequency.MONTHS, 2,
                MonthDays.ACTUAL, YearDays.ACTUAL, 12, RoundingMode.HALF_EVEN, "0.020026948125", "0.020026948125");
        check(reference, problems, "RATE-05", "2023-01-01", "2023-01-01", "2023-02-01", "2023-01-01", "2023-02-01", "9.48222", Frequency.MONTHS, 1,
                MonthDays.ACTUAL, YearDays.DAYS_365, 6, RoundingMode.HALF_UP, "0.008053", "0.008053");
        // the stretch's days are rounded to the precision first: 1,096 days at precision 3 are 1,090 (exact days would give 0.361)
        check(reference, problems, "RATE-05 (days at the precision)", "1919-07-06", "1919-07-06", "1922-07-06", "1919-07-06", "1922-07-06",
                "11.98", Frequency.YEARS, 3, MonthDays.ACTUAL, YearDays.DAYS_360, 3, RoundingMode.FLOOR, "0.359", "0.359");
        check(reference, problems, "RATE-06", "2023-01-01", "2023-01-01", "2023-02-01", "2023-01-01", "2023-02-01", "7", Frequency.MONTHS, 1,
                MonthDays.THIRTY, YearDays.DAYS_360, 12, RoundingMode.HALF_EVEN, "0.005833333333", "0.005833333333");
        check(reference, problems, "RATE-08", "2023-01-29", "2023-02-28", "2023-03-31", "2023-02-28", "2023-03-31", "7", Frequency.MONTHS, 1,
                MonthDays.THIRTY, YearDays.DAYS_360, 12, RoundingMode.HALF_EVEN, "0.005833333333", "0.006397849462");
        check(reference, problems, "RATE-09", "2023-01-01", "2023-01-01", "2023-02-01", "2023-01-01", "2023-02-01", "7", Frequency.MONTHS, 1,
                MonthDays.THIRTY, YearDays.DAYS_360, 3, RoundingMode.HALF_UP, "0.006", "0.006");
        check(reference, problems, "RATE-10", "2023-01-01", "2023-01-01", "2024-01-01", "2023-01-01", "2023-06-01", "7", Frequency.YEARS, 1,
                MonthDays.THIRTY, YearDays.DAYS_360, 12, RoundingMode.HALF_EVEN, null, null);
    }

    private static void check(RateFactors reference, List<String> problems, String what, String start, String pf, String pd, String from, String to,
            String rate, Frequency f, int every, MonthDays md, YearDays yd, int precision, RoundingMode mode, String installment, String interest) {
        RateQuery q = new RateQuery(LocalDate.parse(start), new Period(LocalDate.parse(pf), LocalDate.parse(pd)), LocalDate.parse(from),
                LocalDate.parse(to), new BigDecimal(rate), f, every, md, yd);
        RateOutcome o = reference.factors(q, new Calculation(precision, mode));
        boolean ok = installment == null ? o instanceof RateRejected
                : o instanceof Factors x && x.installment().toPlainString().equals(installment) && x.interest().toPlainString().equals(interest);
        if (!ok) problems.add("digest example " + what + ": the reference gives " + o);
    }

    /** The traced factors with the calculation's precision as their decimal places (exact), or the rejection as it is. */
    private static JsonNode scaled(JsonNode expected, Calculation calc, String origin, List<String> problems) {
        if (!"Factors".equals(expected.path("@type").asText())) return expected;
        ObjectNode o = JSON.createObjectNode();
        o.put("@type", "Factors");
        for (String f : List.of("installment", "interest")) {
            try {
                o.put(f, new BigDecimal(expected.get(f).asText()).setScale(calc.precision(), RoundingMode.UNNECESSARY).toPlainString());
            } catch (ArithmeticException e) {
                problems.add(origin + ": the traced " + f + " factor " + expected.get(f).asText() + " has more than " + calc.precision() + " decimals");
                return null;
            }
        }
        return o;
    }

    static RateQuery query(JsonNode q) {
        return new RateQuery(LocalDate.parse(q.get("scheduleStart").asText()),
                new Period(LocalDate.parse(q.get("period").get("from").asText()), LocalDate.parse(q.get("period").get("due").asText())),
                LocalDate.parse(q.get("from").asText()), LocalDate.parse(q.get("to").asText()), new BigDecimal(q.get("annualRatePercent").asText()),
                Frequency.valueOf(q.get("frequency").asText()), q.get("every").asInt(), MonthDays.valueOf(q.get("monthDays").asText()),
                YearDays.valueOf(q.get("yearDays").asText()));
    }

    /** The query in the API's canonical JSON (components in declaration order). */
    static ObjectNode queryJson(RateQuery q) {
        ObjectNode o = JSON.createObjectNode();
        o.put("scheduleStart", q.scheduleStart().toString());
        ObjectNode p = o.putObject("period");
        p.put("from", q.period().from().toString());
        p.put("due", q.period().due().toString());
        o.put("from", q.from().toString());
        o.put("to", q.to().toString());
        o.put("annualRatePercent", q.annualRatePercent().toPlainString());
        o.put("frequency", q.frequency().name());
        o.put("every", q.every());
        o.put("monthDays", q.monthDays().name());
        o.put("yearDays", q.yearDays().name());
        return o;
    }

    static JsonNode outcomeJson(RateOutcome outcome) {
        ObjectNode o = JSON.createObjectNode();
        if (outcome instanceof Factors f) {
            o.put("@type", "Factors");
            o.put("installment", f.installment().toPlainString());
            o.put("interest", f.interest().toPlainString());
        } else if (outcome instanceof RateRejected r) {
            o.put("@type", "RateRejected");
            o.put("reason", r.reason().name());
        }
        return o;
    }

    private static String scenario(String id, Candidate c) throws IOException {
        ObjectNode input = JSON.createObjectNode();
        input.set("query", queryJson(c.query()));
        ObjectNode calc = input.putObject("calculation");
        calc.put("precision", c.calculation().precision());
        calc.put("roundingMode", c.calculation().roundingMode().name());
        return "{\n  \"id\": " + JSON.writeValueAsString(id) + ",\n  \"slice\": \"" + SLICE + "\",\n  \"origin\": " + JSON.writeValueAsString(c.origin())
                + ",\n  \"rules\": " + JSON.writeValueAsString(c.tags()) + ",\n  \"steps\": [ {\n    \"call\": \"factors\",\n    \"input\": "
                + JSON.writeValueAsString(input) + ",\n    \"expected\": " + JSON.writeValueAsString(c.expected()) + "\n  } ]\n}\n";
    }

    private static void writeReport(Path report, int read, List<Candidate> all, Map<Mutant, Integer> kills, SlicePools.Chosen<Candidate> chosen,
            List<String> problems) throws IOException {
        StringBuilder sb = new StringBuilder("# Rates pool (slice S3): verification and selection\n\n");
        sb.append("Built by `RatesPool` from FNEM0300's CALL traces (`tools/build_rates_candidates.py`, `tools/trace_rates.py`).\n\n");
        sb.append("| | |\n| --- | --- |\n");
        sb.append("| Candidate lines read | ").append(read).append(" |\n");
        sb.append("| Distinct queries | ").append(all.size()).append(" (rejections: ").append(all.stream().filter(Candidate::rejection).count()).append(") |\n");
        sb.append("| Reference (`InterestFactors`) equal on | ").append(problems.stream().anyMatch(p -> p.startsWith("the reference")) ? "NOT ALL" : "all").append(" |\n");
        sb.append("| Chosen | ").append(chosen.byId().size()).append(": ").append(chosen.publicIds().size()).append(" public, ")
                .append(chosen.byId().size() - chosen.publicIds().size()).append(" hidden |\n\n");
        sb.append("## Rule mutants (FE 7.2 step 3)\n\n| Rule | Mutant | Candidates that kill it | Chosen scenarios that kill it | of them public |\n| --- | --- | --- | --- | --- |\n");
        for (Mutant m : Mutant.values()) {
            String label = m.label();
            long pub = chosen.publicIds().stream().filter(id -> chosen.byId().get(id).selection().contains(label)).count();
            sb.append("| ").append(label).append(" | ").append(m.change).append(" | ").append(kills.getOrDefault(m, 0)).append(" | ")
                    .append(chosen.ruleCount().getOrDefault(label, 0)).append(" | ").append(pub).append(" |\n");
        }
        if (!problems.isEmpty()) {
            sb.append("\n## Problems\n\n");
            problems.forEach(p -> sb.append("- ").append(p).append('\n'));
        }
        Files.createDirectories(report.getParent());
        Files.writeString(report, sb.toString(), StandardCharsets.UTF_8);
    }
}
