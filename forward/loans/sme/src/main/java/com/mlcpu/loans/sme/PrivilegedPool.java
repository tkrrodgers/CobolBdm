package com.mlcpu.loans.sme;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.node.ArrayNode;
import com.mlcpu.loans.api.ScheduleRequest;
import com.mlcpu.loans.api.ScheduleService;
import com.mlcpu.loans.application.LoansApplication;
import com.mlcpu.loans.schedule.AmortizationMutants.Mutant;
import com.mlcpu.loans.sme.SchedulePool.Candidate;
import com.mlcpu.loans.terms.TermsReview;
import java.io.BufferedReader;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.List;
import java.util.Map;
import java.util.Objects;
import java.util.Set;
import java.util.TreeMap;
import java.util.TreeSet;
import java.util.stream.Stream;

/**
 * The privileged schedule scenarios (ForwardEngineeringFineract.md F5, decision D-6, section 10 "envelope value"): loans beyond
 * the legacy envelope and inside the service's limits (tools/make_privileged.py), their expected values from oracle A without
 * the envelope (fineract-cobol-gen FnHarness --forward-limits, with A' for D-2; tools/build_schedule_pool.py LABEL=...). The
 * reference must give each one's outcome; tags by the schedule mutants they kill (as SchedulePool); per axis, outcome and tag
 * set up to {@code perGroup} (one for the long axes: a scenario of 1,200 lines is large). All go to the privileged folder: the
 * judge counts them apart and no model sees them.
 *
 * <p>Usage: PrivilegedPool &lt;privileged pool dir&gt; &lt;scenario root&gt; &lt;scenarios.json&gt; &lt;report.md&gt; [per group 2]
 */
public final class PrivilegedPool {

    private static final ObjectMapper JSON = new ObjectMapper();
    private static final String SLICE = "schedule";
    private static final String VISIBILITY = "privileged";

    public static void main(String[] args) throws IOException {
        Path pool = Path.of(args[0]);
        Path scenarioRoot = Path.of(args[1]);
        Path manifest = Path.of(args[2]);
        Path report = Path.of(args[3]);
        int perGroup = args.length > 4 ? Integer.parseInt(args[4]) : 2;
        List<JsonNode> read = new ArrayList<>();
        List<Path> files;
        try (Stream<Path> s = Files.list(pool)) {
            files = s.filter(p -> p.toString().endsWith(".jsonl")).sorted().toList();
        }
        for (Path f : files) {
            try (BufferedReader r = Files.newBufferedReader(f, StandardCharsets.UTF_8)) {
                String line;
                while ((line = r.readLine()) != null) read.add(JSON.readTree(line));
            }
        }
        ScheduleService reference = LoansApplication.quotes();
        TermsReview terms = new TermsReview();
        List<String> problems = Collections.synchronizedList(new ArrayList<>());
        List<Candidate> all = read.parallelStream().map(sc -> {
            JsonNode step = sc.get("steps").get(0);
            ScheduleRequest q = PoolRunner.request(step.get("input"));
            JsonNode expected = step.get("expected");
            JsonNode actual = PoolRunner.toJson(reference.schedule(q));
            if (!actual.equals(expected)) {
                problems.add("the reference differs on " + sc.get("id").asText() + ": " + firstDifference(expected, actual));
                return null;
            }
            Set<String> tags = new TreeSet<>();
            for (Mutant m : Mutant.values()) {
                JsonNode got;
                try {
                    got = PoolRunner.toJson(SchedulePool.mutated(q, terms, m));
                } catch (RuntimeException | StackOverflowError e) {
                    got = JSON.createObjectNode().put("exception", e.toString());
                }
                if (!got.equals(expected)) tags.add(m.rule());
            }
            String outcome = "Scheduled".equals(expected.path("@type").asText()) ? "Scheduled" : "Rejected " + expected.path("reason").asText();
            return new Candidate(sc.get("id").asText(), step.get("input"), expected, sc.get("origin").asText(), q, tags, outcome);
        }).filter(Objects::nonNull).toList();

        // the long axes, one per group; the others, perGroup
        List<Candidate> chosenPool = new ArrayList<>();
        Map<String, List<Candidate>> byAxis = new TreeMap<>();
        for (Candidate c : all) byAxis.computeIfAbsent(axis(c.id()), k -> new ArrayList<>()).add(c);
        Map<String, SlicePools.Chosen<Candidate>> perAxis = new TreeMap<>();
        for (var e : byAxis.entrySet()) {
            int n = e.getKey().equals("long") || e.getKey().equals("combined") ? 1 : perGroup;
            var ch = SlicePools.choose(e.getValue(), Candidate::tags, Candidate::outcome, c -> false, n,
                    Comparator.comparing(Candidate::id), "x", c -> 0, "AMZ-01");
            perAxis.put(e.getKey(), ch);
            chosenPool.addAll(ch.byId().values());
        }
        chosenPool.sort(Comparator.comparing(Candidate::id));
        SlicePools.clear(scenarioRoot, SLICE, List.of(VISIBILITY));
        ArrayNode entries = JSON.createArrayNode();
        int i = 0;
        for (Candidate c : chosenPool) {
            String id = String.format("privileged-%04d", ++i);
            Files.writeString(scenarioRoot.resolve(VISIBILITY).resolve(SLICE).resolve(id + ".json"), SchedulePool.scenario(id, c), StandardCharsets.UTF_8);
            String primary = c.tags().isEmpty() ? "AMZ-01" : c.tags().iterator().next();
            SlicePools.entry(entries, id, SLICE, VISIBILITY, primary, c.tags(), c.origin());
        }
        SlicePools.writeManifest(manifest, SLICE, entries, List.of(VISIBILITY));
        writeReport(report, read, all, byAxis, chosenPool, problems);
        System.out.printf("privileged pool: %d read, the reference agrees on %d; %d chosen; %d problem(s)%n", read.size(), all.size(), chosenPool.size(),
                problems.size());
        problems.stream().limit(20).forEach(p -> System.out.println("  problem: " + p));
        if (!problems.isEmpty()) System.exit(1);
    }

    /** The D-6 axis of a privileged scenario id (privileged-long-p12-half_even-..., privileged-envelope-envelope-1-...). */
    static String axis(String id) {
        String rest = id.substring("privileged-".length());
        return rest.substring(0, rest.indexOf('-'));
    }

    /** The first differing path between two JSON trees, for the report. */
    static String firstDifference(JsonNode e, JsonNode a) {
        return diff("", e, a);
    }

    private static String diff(String path, JsonNode e, JsonNode a) {
        if (e.equals(a)) return null;
        if (e.isObject() && a.isObject()) {
            for (var it = e.fieldNames(); it.hasNext(); ) {
                String k = it.next();
                String d = diff(path + "." + k, e.get(k), a.path(k));
                if (d != null) return d;
            }
        } else if (e.isArray() && a.isArray() && e.size() == a.size()) {
            for (int i = 0; i < e.size(); i++) {
                String d = diff(path + "[" + i + "]", e.get(i), a.get(i));
                if (d != null) return d;
            }
        }
        String es = e.toString(), as = a.toString();
        return path + ": expected " + (es.length() > 120 ? es.substring(0, 120) + "..." : es) + ", got " + (as.length() > 120 ? as.substring(0, 120) + "..." : as);
    }

    private static void writeReport(Path report, List<JsonNode> read, List<Candidate> all, Map<String, List<Candidate>> byAxis, List<Candidate> chosen,
            List<String> problems) throws IOException {
        Map<String, Integer> readByAxis = new TreeMap<>();
        for (JsonNode sc : read) readByAxis.merge(axis(sc.get("id").asText()), 1, Integer::sum);
        StringBuilder sb = new StringBuilder("# Privileged schedule scenarios (decision D-6): verification and selection\n\n");
        sb.append("Built by `PrivilegedPool` from `pool/privileged` (requests: `tools/make_privileged.py`; expected values: oracle A' D-2 without the ")
                .append("legacy envelope, `FnHarness --forward-limits`; `tools/build_schedule_pool.py LABEL=...`). The judge counts them apart (privileged); ")
                .append("no model sees them.\n\n");
        sb.append("| Axis | Read | Reference equal | Scheduled | Rejected | Chosen |\n| --- | --- | --- | --- | --- | --- |\n");
        for (var e : readByAxis.entrySet()) {
            List<Candidate> ok = byAxis.getOrDefault(e.getKey(), List.of());
            long sched = ok.stream().filter(c -> c.outcome().equals("Scheduled")).count();
            long ch = chosen.stream().filter(c -> axis(c.id()).equals(e.getKey())).count();
            sb.append("| ").append(e.getKey()).append(" | ").append(e.getValue()).append(" | ").append(ok.size()).append(" | ").append(sched).append(" | ")
                    .append(ok.size() - sched).append(" | ").append(ch).append(" |\n");
        }
        sb.append("| **all** | ").append(read.size()).append(" | ").append(all.size()).append(" | | | ").append(chosen.size()).append(" |\n");
        Map<String, Integer> outcomes = new TreeMap<>();
        for (Candidate c : chosen) outcomes.merge(c.outcome(), 1, Integer::sum);
        sb.append("\nChosen by outcome: ");
        outcomes.forEach((k, v) -> sb.append(k).append(" ").append(v).append("; "));
        sb.append("\n");
        if (!problems.isEmpty()) {
            sb.append("\n## Problems\n\n");
            problems.stream().sorted().forEach(p -> sb.append("- ").append(p).append('\n'));
        }
        Files.createDirectories(report.getParent());
        Files.writeString(report, sb.toString(), StandardCharsets.UTF_8);
    }
}
