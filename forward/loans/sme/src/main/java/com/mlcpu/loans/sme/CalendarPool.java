package com.mlcpu.loans.sme;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.node.ArrayNode;
import com.fasterxml.jackson.databind.node.ObjectNode;
import com.mlcpu.loans.api.Calculation;
import com.mlcpu.loans.api.CurrencyTerms;
import com.mlcpu.loans.api.Frequency;
import com.mlcpu.loans.api.InterestMethod;
import com.mlcpu.loans.api.MonthDays;
import com.mlcpu.loans.api.ScheduleRequest;
import com.mlcpu.loans.api.YearDays;
import com.mlcpu.loans.api.spi.DueDates;
import com.mlcpu.loans.api.spi.Period;
import com.mlcpu.loans.calendar.RepaymentCalendar;
import com.mlcpu.loans.sme.CalendarMutants.Mutant;
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
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.TreeMap;
import java.util.TreeSet;
import java.util.stream.Collectors;
import java.util.stream.Stream;

/**
 * The calendar pool (slice S2, ForwardEngineeringFineract.md sections 7.2 to 7.4) from the candidates of
 * tools/build_calendar_candidates.py: the reference must give every candidate's periods; each digest rule's mutant must be
 * killed by some candidate; a scenario's rule tags are the rules whose mutants it kills. Selects a stratified pool (at most
 * {@code perGroup} scenarios per tag set and frequency, every traced candidate included), splits it about 20% public on each
 * scenario's rarest rule (every rule with three or more scenarios gets a public one), and writes the scenario files, the
 * package's scenarios.json entries for the slice and a report.
 *
 * <p>Usage: CalendarPool &lt;candidates.jsonl&gt; &lt;scenario root&gt; &lt;scenarios.json&gt; &lt;report.md&gt; [per group, 12]
 */
public final class CalendarPool {

    private static final ObjectMapper JSON = new ObjectMapper();
    private static final String SLICE = "calendar";

    record Candidate(String key, JsonNode input, ScheduleRequest request, List<Period> expected, List<String> origins, boolean traced,
                     Set<String> tags) {
    }

    public static void main(String[] args) throws IOException {
        Path candidates = Path.of(args[0]);
        Path scenarioRoot = Path.of(args[1]);
        Path manifest = Path.of(args[2]);
        Path report = Path.of(args[3]);
        int perGroup = args.length > 4 ? Integer.parseInt(args[4]) : 12;
        DueDates reference = new RepaymentCalendar();
        List<String> problems = new ArrayList<>();

        // the digest's worked examples, on the reference
        examples(reference, problems);

        List<Candidate> all = new ArrayList<>();
        Map<Mutant, Integer> kills = new EnumMap<>(Mutant.class);
        try (BufferedReader r = Files.newBufferedReader(candidates, StandardCharsets.UTF_8)) {
            String line;
            while ((line = r.readLine()) != null) {
                JsonNode c = JSON.readTree(line);
                ScheduleRequest q = PoolRunner.request(c.get("input"));
                List<Period> expected = new ArrayList<>();
                for (JsonNode p : c.get("expected")) expected.add(new Period(LocalDate.parse(p.get("from").asText()), LocalDate.parse(p.get("due").asText())));
                List<Period> actual = reference.periods(q);
                if (!actual.equals(expected)) {
                    problems.add("the reference differs on " + c.get("origins").get(0).asText());
                    continue;
                }
                Set<String> tags = new TreeSet<>();
                for (Mutant m : Mutant.values()) {
                    if (!CalendarMutants.periods(q, m).equals(expected)) {
                        tags.add(m.rule());
                        kills.merge(m, 1, Integer::sum);
                    }
                }
                List<String> origins = new ArrayList<>();
                c.get("origins").forEach(o -> origins.add(o.asText()));
                boolean traced = false;
                for (JsonNode s : c.get("sources")) traced |= "trace".equals(s.asText());
                all.add(new Candidate(JSON.writeValueAsString(c.get("input")), c.get("input"), q, expected, origins, traced, tags));
            }
        }
        for (Mutant m : Mutant.values()) {
            if (kills.getOrDefault(m, 0) == 0) problems.add("rule " + m.rule() + ": no candidate kills its mutant (" + m.change + ")");
        }

        // selection and split (SlicePools): per tag set and frequency, every traced candidate and up to perGroup in all
        SlicePools.Chosen<Candidate> chosen = SlicePools.choose(all, Candidate::tags, c -> String.valueOf(c.request().frequency()), Candidate::traced,
                perGroup, Comparator.comparing((Candidate c) -> c.request().frequency()).thenComparing(Candidate::key), "cal", c -> c.expected().size(),
                "CAL-01");
        Map<String, Candidate> byId = chosen.byId();
        Set<String> publicIds = chosen.publicIds();
        Map<String, Integer> ruleCount = chosen.ruleCount();

        // scenario files
        SlicePools.clear(scenarioRoot, SLICE);
        ArrayNode entries = JSON.createArrayNode();
        for (var e : byId.entrySet()) {
            String id = e.getKey();
            Candidate c = e.getValue();
            String visibility = publicIds.contains(id) ? "public" : "hidden";
            String origin = origin(c);
            Files.writeString(scenarioRoot.resolve(visibility).resolve(SLICE).resolve(id + ".json"), scenario(id, origin, c), StandardCharsets.UTF_8);
            SlicePools.entry(entries, id, SLICE, visibility, chosen.primary().get(id), c.tags(), origin);
        }
        SlicePools.writeManifest(manifest, SLICE, entries);
        writeReport(report, all, kills, byId.size(), byId, publicIds, ruleCount, problems);
        System.out.printf("calendar pool: %d candidates (reference agrees on all: %s), %d chosen, %d public, %d hidden; %d problem(s)%n", all.size(),
                problems.stream().noneMatch(p -> p.startsWith("the reference")), byId.size(), publicIds.size(), byId.size() - publicIds.size(), problems.size());
        kills.forEach((m, n) -> System.out.printf("  %s killed by %d candidate(s)%n", m.rule(), n));
        problems.forEach(p -> System.out.println("  problem: " + p));
        if (!problems.isEmpty()) System.exit(1);
    }

    private static String origin(Candidate c) {
        String first = c.origins().get(0);
        if (c.traced()) {
            return first + "; the same periods as oracle A's schedule of that loan";
        }
        return first.replace("the installment lines of the scheduled loan ", "the installment lines of oracle A's schedule of loan ")
                .replace(" (oracle A, = the faithful job)", " (= the faithful job)");
    }

    /** A scenario file: the header fields one per line, the input on one line, the expected periods one per line. */
    private static String scenario(String id, String origin, Candidate c) throws IOException {
        StringBuilder sb = new StringBuilder("{\n");
        sb.append("  \"id\": ").append(JSON.writeValueAsString(id)).append(",\n");
        sb.append("  \"slice\": \"").append(SLICE).append("\",\n");
        sb.append("  \"origin\": ").append(JSON.writeValueAsString(origin)).append(",\n");
        sb.append("  \"rules\": ").append(JSON.writeValueAsString(c.tags())).append(",\n");
        sb.append("  \"steps\": [ {\n    \"call\": \"periods\",\n    \"input\": ").append(JSON.writeValueAsString(c.input())).append(",\n");
        sb.append("    \"expected\": [");
        for (int i = 0; i < c.expected().size(); i++) {
            Period p = c.expected().get(i);
            sb.append(i == 0 ? "\n" : ",\n").append("      {\"from\":\"").append(p.from()).append("\",\"due\":\"").append(p.due()).append("\"}");
        }
        sb.append("\n    ]\n  } ]\n}\n");
        return sb.toString();
    }

    private static void writeReport(Path report, List<Candidate> all, Map<Mutant, Integer> kills, int chosen, Map<String, Candidate> byId,
            Set<String> publicIds, Map<String, Integer> ruleCount, List<String> problems) throws IOException {
        StringBuilder sb = new StringBuilder("# Calendar pool (slice S2): verification and selection\n\n");
        sb.append("Built by `CalendarPool` from `pool/calendar/candidates.jsonl` (`tools/build_calendar_candidates.py`).\n\n");
        sb.append("| | |\n| --- | --- |\n");
        sb.append("| Candidates (distinct calendar inputs) | ").append(all.size()).append(" |\n");
        sb.append("| of them from FNDG0100 CALL traces | ").append(all.stream().filter(Candidate::traced).count()).append(" (every one also in oracle A's schedules, same periods) |\n");
        sb.append("| Reference (`RepaymentCalendar`) equal on | ").append(all.size()).append(" of ").append(all.size()).append(problems.stream().anyMatch(p -> p.startsWith("the reference")) ? " (NOT ALL)" : "").append(" |\n");
        sb.append("| Chosen | ").append(chosen).append(": ").append(publicIds.size()).append(" public, ").append(chosen - publicIds.size()).append(" hidden |\n\n");
        sb.append("## Rule mutants (FE 7.2 step 3)\n\n| Rule | Mutant | Candidates that kill it | Chosen scenarios tagged | of them public |\n| --- | --- | --- | --- | --- |\n");
        for (Mutant m : Mutant.values()) {
            String rule = m.rule();
            long pub = publicIds.stream().filter(id -> byId.get(id).tags().contains(rule)).count();
            sb.append("| ").append(rule).append(" | ").append(m.change).append(" | ").append(kills.getOrDefault(m, 0)).append(" | ")
                    .append(ruleCount.getOrDefault(rule, 0)).append(" | ").append(pub).append(" |\n");
        }
        sb.append("\nA scenario's rule tags are the rules whose mutants it kills (FE 7.3 asks for tags by coverage; a killed mutant is the stronger form).\n");
        Map<Frequency, Long> byFreq = byId.values().stream().collect(Collectors.groupingBy(c -> c.request().frequency(), TreeMap::new, Collectors.counting()));
        sb.append("\nChosen by frequency: ").append(byFreq).append("\n");
        if (!problems.isEmpty()) {
            sb.append("\n## Problems\n\n");
            problems.forEach(p -> sb.append("- ").append(p).append('\n'));
        }
        Files.createDirectories(report.getParent());
        Files.writeString(report, sb.toString(), StandardCharsets.UTF_8);
    }

    /** The worked examples of digests/calendar.md, on the reference. */
    private static void examples(DueDates reference, List<String> problems) {
        check(reference, problems, "CAL-01", "2023-01-28", "2023-01-28", 3, 1, Frequency.MONTHS, null, "2023-02-28", "2023-03-28", "2023-04-28");
        check(reference, problems, "CAL-03", "2023-03-01", "2023-03-01", 2, 10, Frequency.DAYS, null, "2023-03-11", "2023-03-21");
        check(reference, problems, "CAL-04", "2023-01-28", "2023-01-28", 2, 2, Frequency.WEEKS, null, "2023-02-11", "2023-02-25");
        check(reference, problems, "CAL-05", "2024-01-31", "2024-01-10", 1, 1, Frequency.MONTHS, null, "2024-02-29");
        check(reference, problems, "CAL-06", "2024-02-29", "2024-02-29", 2, 1, Frequency.YEARS, null, "2025-02-28", "2026-02-28");
        check(reference, problems, "CAL-07", "2023-01-31", "2023-01-31", 3, 1, Frequency.MONTHS, null, "2023-02-28", "2023-03-31", "2023-04-30");
        check(reference, problems, "CAL-08", "2023-01-31", "2023-02-10", 3, 1, Frequency.MONTHS, null, "2023-02-28", "2023-03-28", "2023-04-28");
        check(reference, problems, "CAL-09", "2023-01-15", "2023-01-15", 3, 1, Frequency.MONTHS, 4, "2023-02-15", "2023-03-15", "2023-05-15");
        check(reference, problems, "CAL-09 without CAL-07", "2023-01-29", "2023-01-31", 2, 1, Frequency.MONTHS, 2, "2023-02-28", "2023-03-29");
    }

    private static void check(DueDates reference, List<String> problems, String what, String start, String disbursed, int n, int every, Frequency f,
            Integer fixedLength, String... dues) {
        ScheduleRequest q = new ScheduleRequest("example", LocalDate.parse(start), new CurrencyTerms("USD", 2, null), new BigDecimal("1000.00"),
                LocalDate.parse(disbursed), n, every, f, new BigDecimal("10"), null, false, MonthDays.ACTUAL, YearDays.ACTUAL, fixedLength,
                InterestMethod.DECLINING_BALANCE, false, new Calculation(12, RoundingMode.HALF_EVEN));
        List<String> actual = reference.periods(q).stream().map(p -> p.due().toString()).toList();
        if (!actual.equals(List.of(dues))) problems.add("digest example " + what + ": the reference gives " + actual + ", the digest says " + List.of(dues));
    }
}
