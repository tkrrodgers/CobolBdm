package com.mlcpu.jobol;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonArray;
import com.google.gson.JsonObject;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/** Renders a report as JSON (for tools and LLMs), Markdown (for people) and SARIF 2.1.0 (for SonarQube, IDEs, CI). */
public final class Writers {
    private Writers() {
    }

    private static Gson gson() {
        return new GsonBuilder().setPrettyPrinting().disableHtmlEscaping().create();
    }

    public static String json(Report r) {
        return gson().toJson(r) + "\n";
    }

    public static String markdown(Report r, int top) {
        StringBuilder b = new StringBuilder();
        Report.Summary s = r.summary;
        b.append("# JOBOL audit\n\n");
        b.append("Root: `").append(r.root).append("`  \n");
        b.append("Tool: ").append(r.tool).append(' ').append(r.version).append(", ruleset `").append(r.rulesetHash).append("`\n\n");
        b.append("## Gate: ").append(r.gate.passed ? "PASS" : "FAIL").append("\n\n");
        for (String why : r.gate.reasons) b.append("- ").append(why).append('\n');
        if (r.gate.reasons.isEmpty()) b.append("- all thresholds met\n");
        b.append('\n');
        b.append("| Estate JOBOL index | Grade | Modern lane share | Files | Parsed | Types |\n|---|---|---|---|---|---|\n");
        b.append("| ").append(s.estateIndex).append(" / 100 | ").append(s.estateGrade).append(" | ").append(s.modernLanePct)
            .append("% | ").append(s.files).append(" | ").append(s.parsed).append(" | ").append(s.types).append(" |\n\n");
        b.append("The index is 0 for idiomatic Java and 100 for COBOL in Java syntax (LOC-weighted over the modern lane). ")
            .append("Grades: A ≤ 10, B ≤ 25, C ≤ 45, D ≤ 70, F above.\n\n");

        b.append("## Lanes\n\n| Lane | Files | Types | Lines | Audited by |\n|---|---|---|---|---|\n");
        for (Map.Entry<String, Report.Lane> e : s.lanes.entrySet()) {
            String how = switch (e.getKey()) {
                case "modern" -> "all rules";
                case "compat" -> "runtime-safety rules only";
                default -> "not scored";
            };
            b.append("| ").append(e.getKey()).append(" | ").append(e.getValue().files).append(" | ").append(e.getValue().types)
                .append(" | ").append(e.getValue().loc).append(" | ").append(how).append(" |\n");
        }
        b.append('\n');

        b.append("## Modern-lane grades\n\n| A | B | C | D | F |\n|---|---|---|---|---|\n| ");
        for (String g : List.of("A", "B", "C", "D", "F")) b.append(s.grades.getOrDefault(g, 0)).append(" | ");
        b.setLength(b.length() - 1);
        b.append("\n\n");

        if (r.baseline != null) {
            Report.Delta d = r.baseline;
            b.append("## Change since baseline\n\n");
            b.append("Index ").append(d.previousIndex).append(" → ").append(d.currentIndex).append(" (")
                .append(d.change > 0 ? "+" : "").append(d.change).append(")");
            if (!d.comparable) b.append(", **different ruleset: not comparable**");
            b.append(". New findings ").append(d.newFindings).append(" (").append(d.newBlockers).append(" BLOCKER), resolved ")
                .append(d.resolvedFindings).append(". Types added ").append(d.added.size()).append(", removed ")
                .append(d.removed.size()).append(".\n\n");
            appendDeltas(b, "Regressed", d.regressed, top);
            appendDeltas(b, "Improved", d.improved, top);
        }

        b.append("## Findings by rule\n\n| Rule | Name | Severity | Count | Fix |\n|---|---|---|---|---|\n");
        Map<String, Report.RuleRef> refs = new LinkedHashMap<>();
        for (Report.RuleRef ref : r.rules) refs.put(ref.id(), ref);
        for (Map.Entry<String, Integer> e : s.findingsByRule.entrySet()) {
            Report.RuleRef ref = refs.get(e.getKey());
            if (ref == null) continue;
            b.append("| ").append(ref.id()).append(" | ").append(ref.name()).append(" | ").append(ref.severity()).append(" | ")
                .append(e.getValue()).append(" | ").append(ref.fix().replace("|", "\\|")).append(" |\n");
        }
        if (s.findingsByRule.isEmpty()) b.append("| - | none | - | 0 | - |\n");
        b.append('\n');

        b.append("## Worst modern-lane types (top ").append(top).append(")\n\n");
        int shown = 0;
        for (TypeReport t : r.types) {
            if (!t.zone().equals("modern") || t.index() == 0) continue;
            if (shown++ >= top) break;
            b.append("### ").append(t.type()).append(" — ").append(t.index()).append(" (").append(t.grade()).append(")\n\n");
            b.append('`').append(t.file()).append("`, ").append(t.loc()).append(" lines, ")
                .append(t.metrics().get("mutableFields")).append(" mutable fields, ")
                .append(t.metrics().get("paragraphMethods")).append(" paragraph methods\n\n");
            Map<String, Integer> seen = new LinkedHashMap<>();
            for (Finding f : t.findings()) {
                int n = seen.merge(f.rule(), 1, Integer::sum);
                if (n <= 2) {
                    b.append("- ").append(f.rule()).append(' ').append(f.severity()).append(" L").append(f.line()).append(": ")
                        .append(f.message().replace("|", "\\|")).append('\n');
                }
            }
            List<String> more = new ArrayList<>();
            for (Map.Entry<String, Integer> e : seen.entrySet()) if (e.getValue() > 2) more.add(e.getKey() + " +" + (e.getValue() - 2));
            if (!more.isEmpty()) b.append("- more: ").append(String.join(", ", more)).append('\n');
            b.append('\n');
        }
        if (shown == 0) b.append("No modern-lane type has findings.\n\n");

        if (!r.parseErrors.isEmpty()) {
            b.append("## Parse errors\n\n");
            for (Report.ParseError e : r.parseErrors) b.append("- `").append(e.file()).append("`: ").append(e.message()).append('\n');
            b.append('\n');
        }
        return b.toString();
    }

    private static void appendDeltas(StringBuilder b, String title, List<Report.TypeDelta> ds, int top) {
        if (ds.isEmpty()) return;
        b.append("**").append(title).append("**\n\n");
        for (int i = 0; i < Math.min(top, ds.size()); i++) {
            Report.TypeDelta d = ds.get(i);
            b.append("- `").append(d.key()).append("` ").append(d.previous()).append(" → ").append(d.current()).append('\n');
        }
        if (ds.size() > top) b.append("- ... ").append(ds.size() - top).append(" more\n");
        b.append('\n');
    }

    public static String sarif(Report r) {
        JsonObject root = new JsonObject();
        root.addProperty("$schema", "https://json.schemastore.org/sarif-2.1.0.json");
        root.addProperty("version", "2.1.0");
        JsonObject run = new JsonObject();
        JsonObject driver = new JsonObject();
        driver.addProperty("name", r.tool);
        driver.addProperty("version", r.version);
        JsonArray rules = new JsonArray();
        Map<String, Integer> ruleIndex = new LinkedHashMap<>();
        for (Report.RuleRef ref : r.rules) {
            JsonObject rule = new JsonObject();
            rule.addProperty("id", ref.id());
            rule.addProperty("name", ref.name());
            rule.add("shortDescription", text(ref.summary()));
            rule.add("fullDescription", text(ref.summary()));
            rule.add("help", text(ref.fix()));
            JsonObject conf = new JsonObject();
            conf.addProperty("level", Severity.valueOf(ref.severity()).sarifLevel);
            rule.add("defaultConfiguration", conf);
            JsonObject props = new JsonObject();
            JsonArray tags = new JsonArray();
            tags.add("jobol");
            tags.add("maintainability");
            props.add("tags", tags);
            rule.add("properties", props);
            ruleIndex.put(ref.id(), rules.size());
            rules.add(rule);
        }
        driver.add("rules", rules);
        JsonObject tool = new JsonObject();
        tool.add("driver", driver);
        run.add("tool", tool);
        JsonObject bases = new JsonObject();
        JsonObject src = new JsonObject();
        src.addProperty("uri", Path.of(r.root).toUri().toString());
        bases.add("SRCROOT", src);
        run.add("originalUriBaseIds", bases);
        JsonArray results = new JsonArray();
        for (TypeReport t : r.types) {
            if (!t.zone().equals("modern") && !t.zone().equals("compat")) continue;
            for (Finding f : t.findings()) {
                JsonObject res = new JsonObject();
                res.addProperty("ruleId", f.rule());
                Integer idx = ruleIndex.get(f.rule());
                if (idx != null) res.addProperty("ruleIndex", idx);
                res.addProperty("level", f.severity().sarifLevel);
                res.add("message", text(f.message() + " [" + t.type() + "]"));
                JsonObject phys = new JsonObject();
                JsonObject art = new JsonObject();
                art.addProperty("uri", f.file());
                art.addProperty("uriBaseId", "SRCROOT");
                phys.add("artifactLocation", art);
                JsonObject region = new JsonObject();
                region.addProperty("startLine", Math.max(1, f.line()));
                phys.add("region", region);
                JsonObject loc = new JsonObject();
                loc.add("physicalLocation", phys);
                JsonArray locs = new JsonArray();
                locs.add(loc);
                res.add("locations", locs);
                results.add(res);
            }
        }
        run.add("results", results);
        JsonArray runs = new JsonArray();
        runs.add(run);
        root.add("runs", runs);
        return gson().toJson(root) + "\n";
    }

    private static JsonObject text(String s) {
        JsonObject o = new JsonObject();
        o.addProperty("text", s);
        return o;
    }
}
