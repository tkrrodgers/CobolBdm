package com.mlcpu.jobol;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.mlcpu.jobol.CompareReport.Gap;
import com.mlcpu.jobol.CompareReport.Pair;
import com.mlcpu.jobol.CompareReport.Ratio;
import com.mlcpu.jobol.CompareReport.Structure;
import com.mlcpu.jobol.CompareReport.TypeDiff;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.TreeMap;

/** compare-report.json and compare-report.md. */
public final class CompareWriters {
    private CompareWriters() {
    }

    static Gson gson() {
        return new GsonBuilder().setPrettyPrinting().disableHtmlEscaping().serializeNulls().create();
    }

    public static String json(CompareReport r) {
        return gson().toJson(r) + "\n";
    }

    static String pct(Ratio x) {
        return x.pct + "% (" + x.kept + "/" + x.total + ")";
    }

    static String esc(String s) {
        return s == null ? "" : s.replace("|", "\\|").replace("<", "&lt;").replace(">", "&gt;");
    }

    public static String markdown(CompareReport r, int examples) {
        CompareReport.Summary s = r.summary;
        StringBuilder b = new StringBuilder();
        b.append("# Distance to original: ").append(r.estate).append("\n\n");
        b.append("Tool: ").append(r.tool).append(' ').append(r.version).append(". Original: ");
        b.append(r.originalRoots.size() == 1 ? "`" + r.originalRoots.get(0) + "`" : r.originalRoots.size() + " source roots");
        b.append(". Modern: `").append(r.modernRoot).append("`. Scope: ").append(r.scope);
        if (r.cobolRoot != null) b.append(" (J3S COBOL `").append(r.cobolRoot).append("`)");
        b.append(".\n\n");
        for (String n : r.notes) b.append("- ").append(n).append('\n');
        if (!r.notes.isEmpty()) b.append('\n');

        b.append("## Summary\n\n| Measure | Value |\n|---|---|\n");
        b.append("| API preservation | ").append(pct(s.api)).append(" |\n");
        b.append("| API preservation, reached by J3S | ").append(pct(s.apiReached)).append(" |\n");
        b.append("| Name fidelity | ").append(pct(s.names)).append(" |\n");
        b.append("| Signature fidelity | ").append(pct(s.signatures)).append(" |\n");
        b.append("| Signature fidelity, reached by J3S | ").append(pct(s.signaturesReached)).append(" |\n");
        b.append("| Original types in scope | ").append(s.originalTypes).append(" (").append(s.outOfScope).append(" top-level out of scope) |\n");
        b.append("| Matched | ").append(s.matched).append(" (").append(s.matchedByFqn).append(" by name, ").append(s.matchedMoved).append(" moved) |\n");
        b.append("| Original only | ").append(s.originalOnly).append(" |\n");
        b.append("| Modern only | ").append(s.modernOnlyInfrastructure).append(" pipeline infrastructure, ").append(s.modernOnlyLombok).append(" Lombok expansion, ").append(s.modernOnlyOther).append(" other |\n\n");
        b.append("API preservation counts the public and protected fields, methods, constructors and enum constants of the original's ")
            .append("public types that are present in modern with the same name, types, static flag and visibility. Name fidelity is ")
            .append("the share of aligned identifier pairs (fields, methods, parameters, locals) whose modern spelling is exact. ")
            .append("Signature fidelity is the share of original methods and constructors present with the same name, parameter ")
            .append("types, return type and static flag. \"Reached\" leaves out methods and types no J3S frame was generated for.\n\n");

        b.append("## Names\n\n| Element | ");
        List<String> kinds = new ArrayList<>();
        for (Names.Kind k : Names.Kind.values()) kinds.add(k.id());
        b.append(String.join(" | ", kinds)).append(" |\n|---|");
        b.append("---|".repeat(kinds.size())).append('\n');
        for (Map.Entry<String, Map<String, Integer>> e : s.nameClasses.entrySet()) {
            b.append("| ").append(e.getKey()).append(" | ");
            for (String k : kinds) b.append(e.getValue().getOrDefault(k, 0)).append(" | ");
            b.setLength(b.length() - 1);
            b.append('\n');
        }
        if (!s.truncatedLengths.isEmpty()) {
            b.append("\nLength of the modern spelling of truncated names: ");
            List<String> parts = new ArrayList<>();
            s.truncatedLengths.forEach((k, v) -> parts.add(k + ":" + v));
            b.append(String.join(", ", parts)).append('\n');
        }
        if (!s.truncationCompletion.isEmpty()) {
            b.append("\nCompletion of truncated names from identifiers the modern estate spells out elsewhere: ");
            List<String> parts = new ArrayList<>();
            s.truncationCompletion.forEach((k, v) -> parts.add(k + " " + v));
            b.append(String.join(", ", parts)).append('\n');
        }
        b.append('\n');

        b.append("## Structure (matched and original-only types against their modern counterparts)\n\n");
        b.append("| | Types | LOC | Methods | Avg method lines | Max method lines | Branches | Constructors | Factories | Builders | equals+hashCode | toString |\n");
        b.append("|---|---|---|---|---|---|---|---|---|---|---|---|\n");
        row(b, "original", s.originalStructure);
        row(b, "modern", s.modernStructure);
        b.append('\n');

        b.append("## Idioms\n\n\"Original\" counts the original members that have a modern counterpart (the comparable part); ")
            .append("\"original, all\" also counts the members and types J3S never compiled.\n\n");
        b.append("| Idiom | Original, all | Original | Modern | Delta |\n|---|---|---|---|---|\n");
        for (Map.Entry<String, Pair> e : s.idioms.entrySet()) {
            b.append("| ").append(e.getKey()).append(" | ").append(e.getValue().originalAll).append(" | ").append(e.getValue().original)
                .append(" | ").append(e.getValue().modern).append(" | ").append(signed(e.getValue().modern - e.getValue().original)).append(" |\n");
        }
        b.append("\nType uses (declarations, generics, `new`, static calls):\n\n| Type | Original, all | Original | Modern |\n|---|---|---|---|\n");
        for (Map.Entry<String, Pair> e : s.typeUses.entrySet()) {
            b.append("| ").append(e.getKey()).append(" | ").append(e.getValue().originalAll).append(" | ").append(e.getValue().original)
                .append(" | ").append(e.getValue().modern).append(" |\n");
        }
        b.append('\n');

        b.append("## Top ").append(r.topByDistance.size()).append(" types by distance\n\n");
        b.append("Distance is a sum of penalties (weights in COMPARE-GAPS.md: kind 5, lost supertype 3, missing API method 3, ")
            .append("other reached missing method 2, changed signature 2, missing field 2, extra method 1, member name 1, ")
            .append("parameter/local name 0.25, ...). Methods J3S never compiled are not penalised.\n\n");
        b.append("| Type | Distance | Per member | Largest contributions |\n|---|---|---|---|\n");
        for (CompareReport.Ranked t : r.topByDistance) {
            b.append("| `").append(t.original()).append("`").append(t.original().equals(t.modern()) ? "" : " → `" + t.modern() + "`")
                .append(" | ").append(t.distance()).append(" | ").append(t.distancePerMember()).append(" | ").append(t.top()).append(" |\n");
        }
        b.append('\n');

        b.append("## Gaps by pattern\n\n| Pattern | Count | Examples |\n|---|---|---|\n");
        Map<String, List<Gap>> by = new TreeMap<>();
        for (Gap g : r.gaps) by.computeIfAbsent(g.pattern(), k -> new ArrayList<>()).add(g);
        for (Map.Entry<String, List<Gap>> e : by.entrySet()) {
            b.append("| ").append(e.getKey()).append(" | ").append(e.getValue().size()).append(" | ");
            List<String> ex = new ArrayList<>();
            for (Gap g : e.getValue().subList(0, Math.min(examples, e.getValue().size()))) ex.add(example(g));
            b.append(String.join("<br>", ex)).append(" |\n");
        }
        b.append('\n');

        b.append("## Matched types\n\n| Original | Modern | Kind | API | Signatures | Fields m/x | Methods m/x | Distance |\n|---|---|---|---|---|---|---|---|\n");
        for (TypeDiff d : r.matched) {
            b.append("| `").append(simple(d.original)).append("` | ").append(d.alignment.equals("fqn") ? "same" : "`" + d.modern + "` (" + d.alignment + ")")
                .append(" | ").append(d.kind.same() ? d.kind.original : d.kind.original + " → " + d.kind.modern)
                .append(" | ").append(d.api.total == 0 ? "-" : d.api.pct + "%").append(" | ").append(d.signatures.pct).append("% | ")
                .append(d.fields.missing.size()).append('/').append(d.fields.extra.size()).append(" | ")
                .append(d.methods.missing.size()).append('/').append(d.methods.extra.size()).append(" | ").append(d.distance).append(" |\n");
        }
        b.append('\n');
        if (!r.originalOnly.isEmpty()) {
            b.append("## Original-only types\n\n| Type | Kind | Reached by J3S | API members | Members |\n|---|---|---|---|---|\n");
            for (CompareReport.Missing t : r.originalOnly) {
                b.append("| `").append(t.fqn()).append("` | ").append(t.kind()).append(" | ").append(t.reachedByJ3s() ? "yes" : "no")
                    .append(" | ").append(t.apiMembers()).append(" | ").append(t.members()).append(" |\n");
            }
            b.append('\n');
        }
        b.append("## Modern-only types\n\n");
        List<CompareReport.ModernOnly> other = r.modernOnly.stream().filter(x -> !x.category().equals("pipeline-infrastructure")).toList();
        b.append(s.modernOnlyInfrastructure).append(" pipeline infrastructure types (the generated job, platform, record and strangler ")
            .append("layers; not gaps).");
        Map<String, Integer> reasons = new TreeMap<>();
        for (CompareReport.ModernOnly x : r.modernOnly) if (x.category().equals("pipeline-infrastructure")) reasons.merge(x.reason(), 1, Integer::sum);
        List<String> rs = new ArrayList<>();
        reasons.forEach((k, v) -> rs.add(k + " " + v));
        if (!rs.isEmpty()) b.append(" By rule: ").append(String.join(", ", rs)).append('.');
        b.append("\n\n");
        if (!other.isEmpty()) {
            b.append("| Other modern-only type | Kind | Category | Why |\n|---|---|---|---|\n");
            for (CompareReport.ModernOnly x : other) {
                b.append("| `").append(x.fqn()).append("` | ").append(x.kind()).append(" | ").append(x.category()).append(" | ")
                    .append(esc(x.reason())).append(" |\n");
            }
            b.append('\n');
        }
        if (!r.outOfScope.isEmpty()) {
            b.append("## Out of scope (not compiled by J3S, no modern counterpart)\n\n");
            for (String x : r.outOfScope) b.append("- `").append(x).append("`\n");
            b.append('\n');
        }
        if (!r.parseErrors.isEmpty()) {
            b.append("## Parse errors\n\n");
            for (String x : r.parseErrors) b.append("- ").append(esc(x)).append('\n');
        }
        return b.toString();
    }

    static String example(Gap g) {
        StringBuilder b = new StringBuilder("`").append(simple(g.type())).append("`");
        if (!g.member().isEmpty()) b.append(" ").append(esc(g.member()));
        if (!g.original().isEmpty() || !g.modern().isEmpty()) {
            b.append(": ").append(esc(g.original().isEmpty() ? "-" : g.original())).append(" → ").append(esc(g.modern().isEmpty() ? "-" : g.modern()));
        }
        if (!g.detail().isEmpty()) b.append(" (").append(esc(g.detail())).append(")");
        return b.toString();
    }

    static String simple(String fqn) {
        int i = fqn.lastIndexOf('.');
        // keep Outer.Inner readable: strip only the lower-case package prefix
        int p = 0;
        for (String seg : fqn.split("\\.")) {
            if (!seg.isEmpty() && Character.isUpperCase(seg.charAt(0))) break;
            p += seg.length() + 1;
        }
        return p > 0 && p < fqn.length() ? fqn.substring(p) : i < 0 ? fqn : fqn.substring(i + 1);
    }

    private static String signed(int v) {
        return v > 0 ? "+" + v : String.valueOf(v);
    }

    private static void row(StringBuilder b, String label, Structure s) {
        b.append("| ").append(label).append(" | ").append(s.types).append(" | ").append(s.loc).append(" | ").append(s.methods).append(" | ")
            .append(s.avgMethodLines).append(" | ").append(s.maxMethodLines).append(" | ").append(s.branches).append(" | ")
            .append(s.constructors).append(" | ").append(s.factories).append(" | ").append(s.builders).append(" | ")
            .append(s.equalsHashCode).append(" | ").append(s.toStrings).append(" |\n");
    }
}
