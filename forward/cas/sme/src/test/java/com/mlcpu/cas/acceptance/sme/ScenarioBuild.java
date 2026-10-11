package com.mlcpu.cas.acceptance.sme;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.node.ArrayNode;
import com.fasterxml.jackson.databind.node.JsonNodeFactory;
import com.fasterxml.jackson.databind.node.ObjectNode;
import com.mlcpu.cas.acceptance.InMemoryPorts;
import com.mlcpu.cas.acceptance.Json;
import com.mlcpu.cas.acceptance.Scenario;
import com.mlcpu.cas.api.CustomerAccountService;
import com.mlcpu.cas.application.CasApplication;
import java.io.IOException;
import java.math.BigDecimal;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.LocalDateTime;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Random;
import java.util.Set;
import java.util.TreeMap;
import java.util.TreeSet;
import java.util.stream.Collectors;
import java.util.stream.Stream;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.condition.EnabledIfEnvironmentVariable;

/**
 * The SME's scenario build (Phase 0, forward/cas/sme): legacy drafts (the faithful programs' runs, exported by the
 * ForwardExportTest classes in the published estates) and SME-authored scenarios become acceptance scenarios. Expected
 * values come from the reference implementation; every legacy-derived scenario is cross-checked against the faithful
 * program's outcome and the differences are written for review (cross-check.md). Rules are tagged, and the scenarios are
 * split into public and hidden by stratified sampling on each scenario's primary (rarest) rule.
 *
 * Environment: CAS_DRAFTS (drafts dir), CAS_OUT (forward/cas dir; scenarios go to service/cas-acceptance/src/test/resources).
 */
@EnabledIfEnvironmentVariable(named = "CAS_DRAFTS", matches = ".+")
class ScenarioBuild {
    static final JsonNodeFactory F = JsonNodeFactory.instance;
    static final long SEED = 20260010L;

    /** A built scenario before the split. */
    record Built(String id, String slice, String origin, Set<String> rules, ObjectNode json, List<String> crossCheck, boolean legacy) {}

    final List<Built> built = new ArrayList<>();

    @Test
    void build() throws IOException {
        Path drafts = Path.of(System.getenv("CAS_DRAFTS"));
        Path out = Path.of(System.getenv("CAS_OUT"));
        int post = 0, single = 0, interest = 0, report = 0, cust = 0;
        for (Path f : list(drafts.resolve("accounts"))) {
            ObjectNode d = (ObjectNode) Json.MAPPER.readTree(f.toFile());
            String name = f.getFileName().toString();
            if (name.startsWith("batch-")) built.add(fromDraft(d, String.format("post-batch-%02d", ++post), "accounts"));
            else if (name.startsWith("single-")) built.add(fromDraft(d, String.format("post-%03d", ++single), "accounts"));
            else if (name.startsWith("interest-")) built.add(fromDraft(d, String.format("interest-%02d", ++interest), "accounts"));
        }
        for (Path f : list(drafts.resolve("reporting")))
            built.add(fromDraft((ObjectNode) Json.MAPPER.readTree(f.toFile()), String.format("report-%02d", ++report), "reporting"));
        for (Path f : list(drafts.resolve("customers")))
            built.add(fromDraft((ObjectNode) Json.MAPPER.readTree(f.toFile()), String.format("cust-%03d", ++cust), "customers"));
        sampleSingles();
        for (Authored.Spec spec : Authored.all(drafts)) built.add(authored(spec));
        write(out);
    }

    static List<Path> list(Path dir) throws IOException {
        if (!Files.isDirectory(dir)) return List.of();
        try (Stream<Path> s = Files.list(dir)) {
            return s.filter(p -> p.toString().endsWith(".json")).sorted().toList();
        }
    }

    // ------------------------------------------------------------------------------------------------ reference runs

    /** Runs the steps on the reference implementation and fills in each step's "expect" and the scenario's "expectState". */
    static ObjectNode withExpectations(ObjectNode scenario, List<String> stores) {
        Scenario s = new Scenario(scenario.path("id").asText(), scenario.path("slice").asText(), "", scenario);
        InMemoryPorts p = s.load();
        CustomerAccountService service = CasApplication.create(p.ports());
        for (JsonNode step : scenario.path("steps")) {
            p.clock.set(LocalDateTime.parse(step.path("at").asText()));
            ((ObjectNode) step).set("expect", Scenario.execute(service, step.path("command").asText(), step.path("input")));
        }
        ObjectNode state = Scenario.state(p);
        ObjectNode expectState = scenario.putObject("expectState");
        for (String store : stores) expectState.set(store, state.get(store));
        return scenario;
    }

    static List<String> storesOf(String command) {
        return switch (command) {
            case "postTransaction", "postBatch" -> List.of("accounts", "categoryBalances", "ledger", "rejections");
            case "accrueInterest" -> List.of("accounts", "ledger");
            case "applyCustomerChanges" -> List.of("customers");
            default -> List.of();
        };
    }

    Built fromDraft(ObjectNode d, String id, String slice) {
        String command = d.path("command").asText();
        String origin = "legacy " + d.path("program").asText() + ": '" + d.path("name").asText()
                        + "' (the faithful program, byte-identical to GnuCOBOL, run on the published estate's equivalence-test inputs)";
        ObjectNode s = F.objectNode();
        s.put("id", id);
        s.put("slice", slice);
        s.put("origin", origin);
        s.putArray("rules");
        s.set("initial", d.path("initial"));
        ObjectNode step = s.putArray("steps").addObject();
        step.put("at", d.path("at").asText());
        step.put("command", command);
        step.set("input", d.path("input"));
        withExpectations(s, storesOf(command));
        Set<String> rules = Tags.of(s, d.path("name").asText());
        ArrayNode r = (ArrayNode) s.get("rules");
        rules.forEach(r::add);
        List<String> cc = CrossCheck.of(d, s);
        return new Built(id, slice, origin, rules, s, cc, true);
    }

    Built authored(Authored.Spec spec) {
        ObjectNode s = spec.scenario();
        List<String> stores = new ArrayList<>();
        for (JsonNode step : s.path("steps"))
            for (String st : storesOf(step.path("command").asText())) if (!stores.contains(st)) stores.add(st);
        if (spec.extraStores() != null) for (String st : spec.extraStores()) if (!stores.contains(st)) stores.add(st);
        withExpectations(s, stores);
        Set<String> rules = new TreeSet<>(spec.rules());
        rules.addAll(Tags.of(s, spec.title()));
        ArrayNode r = s.putArray("rules");
        rules.forEach(r::add);
        return new Built(s.path("id").asText(), s.path("slice").asText(), s.path("origin").asText(), rules, s, List.of(), false);
    }

    /**
     * Keeps every single-transaction scenario that is rejected or otherwise distinct, and a seeded quarter of the plainly posted
     * ones (the CardDemo day alone has 262 of them), so posting does not swamp the suite.
     */
    void sampleSingles() {
        Random random = new Random(SEED);
        List<Built> keep = new ArrayList<>();
        Map<String, Integer> seen = new HashMap<>();
        for (Built b : built) {
            if (!b.id().matches("post-\\d{3}")) {
                keep.add(b);
                continue;
            }
            String signature = String.join(",", b.rules());
            int count = seen.merge(signature, 1, Integer::sum);
            boolean plainPost = b.rules().contains("POST-10") && !b.rules().contains("POST-11");
            if (!plainPost || count <= 2 || random.nextInt(6) == 0) keep.add(b);
        }
        built.clear();
        built.addAll(keep);
    }

    // ------------------------------------------------------------------------------------------------ split and write

    void write(Path out) throws IOException {
        Map<String, Integer> frequency = new TreeMap<>();
        for (Built b : built) for (String r : b.rules()) frequency.merge(r, 1, Integer::sum);
        Map<String, String> primary = new HashMap<>();
        for (Built b : built)
            primary.put(b.id(), b.rules().stream().min(Comparator.comparing((String r) -> frequency.get(r)).thenComparing(r -> r)).orElse("none"));
        Map<String, List<Built>> byPrimary = new TreeMap<>();
        for (Built b : built) byPrimary.computeIfAbsent(primary.get(b.id()), k -> new ArrayList<>()).add(b);
        Random random = new Random(SEED);
        Set<String> publicIds = new TreeSet<>();
        for (var e : byPrimary.entrySet()) {
            List<Built> group = new ArrayList<>(e.getValue());
            group.sort(Comparator.comparing(Built::id));
            Collections.shuffle(group, random);
            int n = (int) Math.round(group.size() * 0.2);
            if (n == 0 && group.size() >= 3) n = 1;
            for (int i = 0; i < n; i++) publicIds.add(group.get(i).id());
        }
        // Every rule with three or more scenarios gets at least one public scenario (the protocol's split rule).
        for (String rule : frequency.keySet()) {
            List<Built> with = built.stream().filter(b -> b.rules().contains(rule)).sorted(Comparator.comparing(Built::id)).toList();
            if (with.size() >= 3 && with.stream().noneMatch(b -> publicIds.contains(b.id())))
                publicIds.add(with.get(random.nextInt(with.size())).id());
        }

        Path resources = out.resolve("service/cas-acceptance/src/test/resources/scenarios");
        if (Files.isDirectory(resources))
            try (Stream<Path> s = Files.walk(resources)) {
                for (Path p : s.sorted(Comparator.reverseOrder()).toList()) Files.delete(p);
            }
        ArrayNode manifest = F.arrayNode();
        for (Built b : built.stream().sorted(Comparator.comparing(Built::slice).thenComparing(Built::id)).toList()) {
            String visibility = publicIds.contains(b.id()) ? "public" : "hidden";
            Path file = resources.resolve(visibility).resolve(b.slice()).resolve(b.id() + ".json");
            Files.createDirectories(file.getParent());
            Files.writeString(file, Json.MAPPER.writerWithDefaultPrettyPrinter().writeValueAsString(b.json()) + "\n");
            ObjectNode m = manifest.addObject();
            m.put("id", b.id());
            m.put("slice", b.slice());
            m.put("visibility", visibility);
            m.put("primaryRule", primary.get(b.id()));
            ArrayNode rs = m.putArray("rules");
            b.rules().forEach(rs::add);
            m.put("origin", b.origin());
        }
        ObjectNode root = F.objectNode();
        root.put("schema", "mlcpu.cas-scenarios.v1");
        root.put("seed", SEED);
        root.put("note", "Built by forward/cas/sme (ScenarioBuild). Expected values: the reference implementation, cross-checked against the faithful programs (cross-check.md).");
        root.set("scenarios", manifest);
        Files.writeString(out.resolve("scenarios.json"), Json.MAPPER.writerWithDefaultPrettyPrinter().writeValueAsString(root) + "\n");

        StringBuilder cc = new StringBuilder("# Cross-check: reference implementation against the faithful programs\n\n");
        cc.append("Every legacy-derived scenario's reference result compared with the faithful program's decoded outcome. A difference\n")
          .append("must be explained by a decision D-n of SPEC.md section 8; the SME's verdict follows each.\n\n");
        Map<String, Integer> verdicts = new TreeMap<>();
        for (Built b : built.stream().filter(Built::legacy).sorted(Comparator.comparing(Built::id)).toList()) {
            String verdict = b.crossCheck().isEmpty() ? "agrees" : b.crossCheck().get(0).startsWith("explained") ? "explained" : "UNEXPLAINED";
            verdicts.merge(verdict, 1, Integer::sum);
            if (b.crossCheck().isEmpty()) continue;
            cc.append("## ").append(b.id()).append(": ").append(b.origin().replaceAll("^legacy (\\w+): '([^']*)'.*", "$1 '$2'")).append("\n\n");
            for (String l : b.crossCheck()) cc.append("* ").append(l).append('\n');
            cc.append('\n');
        }
        cc.insert(cc.indexOf("\n\n") + 2, "Totals: " + verdicts + "\n\n");
        Files.writeString(out.resolve("cross-check.md"), cc.toString());

        Map<String, Long> counts = built.stream().collect(Collectors.groupingBy(b -> b.slice() + "/" + (publicIds.contains(b.id()) ? "public" : "hidden"), TreeMap::new, Collectors.counting()));
        System.out.println("scenarios: " + built.size() + " " + counts + "; cross-check " + verdicts);
        System.out.println("rules: " + frequency);
    }
}
