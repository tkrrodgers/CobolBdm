package com.mlcpu.loans.sme;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.node.ArrayNode;
import com.fasterxml.jackson.databind.node.ObjectNode;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.TreeMap;
import java.util.TreeSet;
import java.util.function.Function;
import java.util.function.Predicate;
import java.util.stream.Stream;

/**
 * What every slice pool shares (ForwardEngineeringFineract.md sections 7.3, 7.4, 10): the stratified selection, the public and
 * hidden split, the scenario folders and the package's scenarios.json.
 */
final class SlicePools {

    static final ObjectMapper JSON = new ObjectMapper();

    private SlicePools() {
    }

    /** A chosen pool: ids in order, the public ids, each id's primary (rarest) rule and the count of scenarios per rule. */
    record Chosen<C>(Map<String, C> byId, Set<String> publicIds, Map<String, String> primary, Map<String, Integer> ruleCount) {
    }

    /**
     * Per group (tag set and the group key), every candidate {@code keep} accepts and up to {@code perGroup} in all, spread
     * over the group; ids {@code prefix-0001} in the order {@code order}. About one in five public per rarest rule (the third,
     * eighth, ...), then every rule with three or more scenarios gets a public one (its smallest, by {@code size}).
     */
    static <C> Chosen<C> choose(List<C> all, Function<C, Set<String>> tags, Function<C, String> groupKey, Predicate<C> keep, int perGroup,
            Comparator<C> order, String prefix, Function<C, Integer> size, String baseRule) {
        Map<String, List<C>> groups = new TreeMap<>();
        for (C c : all) groups.computeIfAbsent(String.join(",", tags.apply(c)) + "|" + groupKey.apply(c), k -> new ArrayList<>()).add(c);
        List<C> chosen = new ArrayList<>();
        for (List<C> g : groups.values()) {
            List<C> pick = new ArrayList<>(g.stream().filter(keep).toList());
            List<C> rest = g.stream().filter(keep.negate()).toList();
            int take = Math.min(Math.max(0, perGroup - pick.size()), rest.size());
            for (int i = 0; i < take; i++) pick.add(rest.get((int) ((long) i * rest.size() / take)));
            chosen.addAll(pick);
        }
        chosen.sort(order);
        Map<String, C> byId = new LinkedHashMap<>();
        for (int i = 0; i < chosen.size(); i++) byId.put(String.format("%s-%04d", prefix, i + 1), chosen.get(i));
        Map<String, Integer> ruleCount = new TreeMap<>();
        for (C c : chosen) for (String t : tags.apply(c)) ruleCount.merge(t, 1, Integer::sum);
        Map<String, String> primary = new LinkedHashMap<>();
        for (var e : byId.entrySet()) {
            primary.put(e.getKey(), tags.apply(e.getValue()).stream().min(Comparator.comparing((String t) -> ruleCount.get(t)).thenComparing(t -> t))
                    .orElse(baseRule));
        }
        Set<String> publicIds = new TreeSet<>();
        Map<String, List<String>> byPrimary = new TreeMap<>();
        primary.forEach((id, rule) -> byPrimary.computeIfAbsent(rule, k -> new ArrayList<>()).add(id));
        for (List<String> ids : byPrimary.values()) {
            for (int i = 2; i < ids.size(); i += 5) publicIds.add(ids.get(i));
        }
        for (String rule : ruleCount.keySet()) {
            if (ruleCount.get(rule) < 3) continue;
            if (publicIds.stream().noneMatch(id -> tags.apply(byId.get(id)).contains(rule))) {
                byId.entrySet().stream().filter(e -> tags.apply(e.getValue()).contains(rule))
                        .min(Comparator.comparing((Map.Entry<String, C> e) -> size.apply(e.getValue())).thenComparing(Map.Entry::getKey))
                        .ifPresent(e -> publicIds.add(e.getKey()));
            }
        }
        return new Chosen<>(byId, publicIds, primary, ruleCount);
    }

    /** Empties (or makes) the slice's public and hidden scenario folders. */
    static void clear(Path scenarioRoot, String slice) throws IOException {
        clear(scenarioRoot, slice, List.of("public", "hidden"));
    }

    /** Empties (or makes) the slice's folders of those visibilities. */
    static void clear(Path scenarioRoot, String slice, List<String> visibilities) throws IOException {
        for (String v : visibilities) {
            Path dir = scenarioRoot.resolve(v).resolve(slice);
            if (Files.isDirectory(dir)) {
                try (Stream<Path> files = Files.list(dir)) {
                    for (Path f : files.toList()) Files.delete(f);
                }
            }
            Files.createDirectories(dir);
        }
    }

    /** A manifest entry of scenarios.json. */
    static ObjectNode entry(ArrayNode entries, String id, String slice, String visibility, String primary, Set<String> rules, String origin) {
        ObjectNode m = entries.addObject();
        m.put("id", id);
        m.put("slice", slice);
        m.put("visibility", visibility);
        m.put("primaryRule", primary);
        ArrayNode r = m.putArray("rules");
        rules.forEach(r::add);
        m.put("origin", origin);
        return m;
    }

    /** Replaces the slice's public and hidden entries of the package's scenarios.json (the other slices' and its privileged ones stay). */
    static void writeManifest(Path manifest, String slice, ArrayNode entries) throws IOException {
        writeManifest(manifest, slice, entries, List.of("public", "hidden"));
    }

    /** Replaces the slice's entries of those visibilities in the package's scenarios.json. */
    static void writeManifest(Path manifest, String slice, ArrayNode entries, List<String> visibilities) throws IOException {
        ObjectNode m;
        if (Files.exists(manifest)) {
            m = (ObjectNode) JSON.readTree(manifest.toFile());
        } else {
            m = JSON.createObjectNode();
            m.put("schema", "mlcpu.loans-scenarios.v1");
            m.put("note", "Built by forward/loans/sme (CalendarPool, ...). Expected values: the faithful programs (CALL traces and the job's "
                    + "schedules, = oracle A by G2 and G5), every one reproduced by the reference implementation.");
            m.putArray("scenarios");
        }
        ArrayNode kept = JSON.createArrayNode();
        for (JsonNode s : m.get("scenarios")) {
            if (!slice.equals(s.path("slice").asText()) || !visibilities.contains(s.path("visibility").asText())) kept.add(s);
        }
        kept.addAll(entries);
        List<JsonNode> sorted = new ArrayList<>();
        kept.forEach(sorted::add);
        sorted.sort(Comparator.comparing((JsonNode s) -> s.path("slice").asText()).thenComparing(s -> s.path("id").asText()));
        ArrayNode out = m.putArray("scenarios");
        sorted.forEach(out::add);
        Files.writeString(manifest, JSON.writerWithDefaultPrettyPrinter().writeValueAsString(m) + "\n", StandardCharsets.UTF_8);
    }
}
