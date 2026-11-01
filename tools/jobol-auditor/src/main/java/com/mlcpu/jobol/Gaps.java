package com.mlcpu.jobol;

import com.google.gson.Gson;
import com.mlcpu.jobol.CompareReport.Gap;
import java.io.IOException;
import java.io.Reader;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.TreeMap;

/**
 * jobol-audit compare-gaps: aggregates the gaps of several compare reports into recurring patterns. The patterns,
 * their closability class and the generator hypotheses come from a catalogue file (design input written by people);
 * the counts and examples come from the reports, so the document is regenerated, never hand-edited.
 */
public final class Gaps {
    private Gaps() {
    }

    static final String USAGE = """
        usage: jobol-audit compare-gaps --report FILE [--report FILE ...] [options]
          --report FILE      a compare-report.json; repeatable, in the order the estates are listed
          --catalogue FILE   patterns: id, title, closability (a|b|c), description, hypothesis, matches (gap codes, '*' suffix)
          --out DIR          write compare-gaps.json and the Markdown (default: ./compare-out)
          --md FILE          the Markdown file (default: OUT/compare-gaps.md)
          --examples N       examples per pattern and estate (default 3)
        """;

    /** The catalogue file. */
    public static final class Catalogue {
        public String title = "Gap catalogue";
        public List<String> preamble = new ArrayList<>();
        public List<Pattern> patterns = new ArrayList<>();
        public List<String> split = new ArrayList<>();
        public List<String> method = new ArrayList<>();
    }

    public static final class Pattern {
        public String id;
        public String title;
        public String closability;
        public String description;
        public String hypothesis;
        public String evidence;
        public List<String> matches = new ArrayList<>();
    }

    /** compare-gaps.json */
    public static final class Result {
        public String tool = "jobol-audit compare-gaps";
        public String version = CompareReport.VERSION;
        public List<Estate> estates = new ArrayList<>();
        public List<PatternResult> patterns = new ArrayList<>();
        public Map<String, Map<String, Integer>> unclassified = new TreeMap<>();
        public Map<String, Map<String, Integer>> codes = new TreeMap<>();
    }

    public record Estate(String name, String report, double apiPct, double apiReachedPct, double namesPct, double signaturesPct,
        double signaturesReachedPct, int matched, int originalOnly, int modernOnlyInfrastructure, int modernOnlyLombok, int modernOnlyOther,
        int gaps) {
    }

    public static final class PatternResult {
        public String id;
        public String title;
        public String closability;
        public String description;
        public String hypothesis;
        public String evidence;
        public List<String> matches;
        public Map<String, Integer> counts = new LinkedHashMap<>();
        public int total;
        public Map<String, Map<String, Integer>> codes = new TreeMap<>();
        public List<Example> examples = new ArrayList<>();
    }

    public record Example(String estate, String code, String type, String member, String original, String modern, String detail) {
    }

    static int run(String[] args) {
        List<Path> reports = new ArrayList<>();
        Path catalogue = null;
        Path out = Path.of("compare-out");
        Path md = null;
        int examples = 3;
        try {
            for (int i = 0; i < args.length; i++) {
                switch (args[i]) {
                    case "--report" -> reports.add(Path.of(args[++i]));
                    case "--catalogue" -> catalogue = Path.of(args[++i]);
                    case "--out" -> out = Path.of(args[++i]);
                    case "--md" -> md = Path.of(args[++i]);
                    case "--examples" -> examples = Integer.parseInt(args[++i]);
                    case "-h", "--help" -> {
                        System.out.print(USAGE);
                        return 0;
                    }
                    default -> throw new IllegalArgumentException("unexpected argument " + args[i]);
                }
            }
        } catch (ArrayIndexOutOfBoundsException | IllegalArgumentException e) {
            System.err.println("jobol-audit compare-gaps: " + (e instanceof ArrayIndexOutOfBoundsException ? "missing option value" : e.getMessage()));
            System.err.print(USAGE);
            return 2;
        }
        if (reports.isEmpty()) {
            System.err.print(USAGE);
            return 2;
        }
        try {
            List<CompareReport> rs = new ArrayList<>();
            for (Path p : reports) rs.add(load(p, CompareReport.class));
            Catalogue c = catalogue == null ? new Catalogue() : load(catalogue, Catalogue.class);
            List<String> names = reports.stream().map(p -> p.toString().replace('\\', '/')).toList();
            Result res = aggregate(rs, names, c, examples);
            Files.createDirectories(out);
            Files.writeString(out.resolve("compare-gaps.json"), CompareWriters.gson().toJson(res) + "\n", StandardCharsets.UTF_8);
            Path mdFile = md == null ? out.resolve("compare-gaps.md") : md;
            if (mdFile.toAbsolutePath().getParent() != null) Files.createDirectories(mdFile.toAbsolutePath().getParent());
            Files.writeString(mdFile, markdown(res, c), StandardCharsets.UTF_8);
            System.out.printf("compare-gaps: %d estates, %d patterns, %d unclassified codes -> %s%n", rs.size(), res.patterns.size(),
                res.unclassified.size(), out.toAbsolutePath().normalize());
            return 0;
        } catch (IOException | RuntimeException e) {
            System.err.println("jobol-audit compare-gaps: " + e);
            return 2;
        }
    }

    private static <T> T load(Path p, Class<T> c) throws IOException {
        try (Reader r = Files.newBufferedReader(p, StandardCharsets.UTF_8)) {
            return new Gson().fromJson(r, c);
        }
    }

    static boolean matches(String code, List<String> globs) {
        for (String g : globs) {
            if (g.endsWith("*") ? code.startsWith(g.substring(0, g.length() - 1)) : code.equals(g)) return true;
        }
        return false;
    }

    static Result aggregate(List<CompareReport> rs, List<String> reportNames, Catalogue c, int examples) {
        Result res = new Result();
        for (int i = 0; i < rs.size(); i++) {
            CompareReport r = rs.get(i);
            CompareReport.Summary s = r.summary;
            res.estates.add(new Estate(r.estate, reportNames.get(i), s.api.pct, s.apiReached.pct, s.names.pct, s.signatures.pct,
                s.signaturesReached.pct, s.matched, s.originalOnly, s.modernOnlyInfrastructure, s.modernOnlyLombok, s.modernOnlyOther, r.gaps.size()));
            for (Gap g : r.gaps) res.codes.computeIfAbsent(g.pattern(), k -> new LinkedHashMap<>()).merge(r.estate, 1, Integer::sum);
        }
        Set<String> claimed = new HashSet<>();
        for (Pattern p : c.patterns) {
            PatternResult pr = new PatternResult();
            pr.id = p.id;
            pr.title = p.title;
            pr.closability = p.closability;
            pr.description = p.description;
            pr.hypothesis = p.hypothesis;
            pr.evidence = p.evidence;
            pr.matches = p.matches;
            for (CompareReport r : rs) {
                int n = 0;
                List<Gap> mine = new ArrayList<>();
                for (Gap g : r.gaps) {
                    if (claimed.contains(g.pattern()) && !matchesFirst(g.pattern(), p, c)) continue;
                    if (!matches(g.pattern(), p.matches) || !matchesFirst(g.pattern(), p, c)) continue;
                    n++;
                    mine.add(g);
                    pr.codes.computeIfAbsent(g.pattern(), k -> new LinkedHashMap<>()).merge(r.estate, 1, Integer::sum);
                }
                pr.counts.put(r.estate, n);
                pr.total += n;
                pr.examples.addAll(pick(r.estate, mine, examples));
            }
            claimed.addAll(pr.codes.keySet());
            res.patterns.add(pr);
        }
        for (Map.Entry<String, Map<String, Integer>> e : res.codes.entrySet()) {
            if (!claimed.contains(e.getKey())) res.unclassified.put(e.getKey(), e.getValue());
        }
        return res;
    }

    /** A gap code belongs to the first catalogue pattern that matches it. */
    private static boolean matchesFirst(String code, Pattern p, Catalogue c) {
        for (Pattern q : c.patterns) {
            if (matches(code, q.matches)) return q == p;
        }
        return false;
    }

    /** Up to n examples, one per type first (in report order), so examples spread over the estate. */
    private static List<Example> pick(String estate, List<Gap> gaps, int n) {
        List<Example> out = new ArrayList<>();
        Set<String> types = new HashSet<>();
        for (int pass = 0; pass < 2 && out.size() < n; pass++) {
            for (Gap g : gaps) {
                if (out.size() >= n) break;
                boolean fresh = types.add(g.type() + "|" + g.pattern());
                if (pass == 0 && !fresh) continue;
                Example e = new Example(estate, g.pattern(), g.type(), g.member(), g.original(), g.modern(), g.detail());
                if (!out.contains(e)) out.add(e);
            }
        }
        return out;
    }

    static String markdown(Result res, Catalogue c) {
        StringBuilder b = new StringBuilder("# ").append(c.title).append("\n\n");
        b.append("<!-- Generated by jobol-audit compare-gaps from the compare reports and compare-catalogue.json; do not edit by hand. -->\n\n");
        for (String l : c.preamble) b.append(l).append('\n');
        b.append("\n## Estates\n\n| Estate | API | API (reached) | Names | Signatures | Signatures (reached) | Matched | Original only | Modern only: infrastructure / Lombok expansion / other | Gap instances |\n");
        b.append("|---|---|---|---|---|---|---|---|---|---|\n");
        for (Estate e : res.estates) {
            b.append("| ").append(e.name()).append(" | ").append(e.apiPct()).append("% | ").append(e.apiReachedPct()).append("% | ")
                .append(e.namesPct()).append("% | ").append(e.signaturesPct()).append("% | ").append(e.signaturesReachedPct()).append("% | ")
                .append(e.matched()).append(" | ").append(e.originalOnly()).append(" | ").append(e.modernOnlyInfrastructure()).append(" / ")
                .append(e.modernOnlyLombok()).append(" / ").append(e.modernOnlyOther()).append(" | ").append(e.gaps()).append(" |\n");
        }
        b.append("\n## Patterns\n\nClosability: **a** closable from COBOL evidence alone (generalises to estates with no original Java); ")
            .append("**b** closable only with external knowledge (JDK idioms, library conventions); **c** not closable or not important.\n\n");
        b.append("| Id | Pattern | Class |");
        for (Estate e : res.estates) b.append(' ').append(e.name()).append(" |");
        b.append(" Total |\n|---|---|---|").append("---|".repeat(res.estates.size())).append("---|\n");
        List<PatternResult> sorted = new ArrayList<>(res.patterns);
        for (PatternResult p : sorted) {
            b.append("| [").append(p.id).append("](#").append(anchor(p.id, p.title)).append(") | ").append(p.title).append(" | ").append(p.closability).append(" |");
            for (Estate e : res.estates) b.append(' ').append(p.counts.getOrDefault(e.name(), 0)).append(" |");
            b.append(' ').append(p.total).append(" |\n");
        }
        b.append('\n');
        for (PatternResult p : sorted) {
            b.append("### ").append(p.id).append(": ").append(p.title).append("\n\n");
            b.append("Class **").append(p.closability).append("**. ").append(p.description).append("\n\n");
            b.append("Counts: ");
            List<String> cs = new ArrayList<>();
            for (Estate e : res.estates) cs.add(e.name() + " " + p.counts.getOrDefault(e.name(), 0));
            b.append(String.join(", ", cs)).append(" (total ").append(p.total).append("). Gap codes: ");
            List<String> codes = new ArrayList<>();
            for (Map.Entry<String, Map<String, Integer>> e : p.codes.entrySet()) {
                int n = e.getValue().values().stream().mapToInt(Integer::intValue).sum();
                codes.add("`" + e.getKey() + "` " + n);
            }
            b.append(codes.isEmpty() ? "none in these estates" : String.join(", ", codes)).append(".\n\n");
            if (!p.examples.isEmpty()) {
                b.append("| Estate | Type | Member | Original | Modern | Detail |\n|---|---|---|---|---|---|\n");
                for (Example x : p.examples) {
                    b.append("| ").append(x.estate()).append(" | `").append(CompareWriters.simple(x.type())).append("` | ")
                        .append(CompareWriters.esc(x.member())).append(" | ").append(CompareWriters.esc(x.original())).append(" | ")
                        .append(CompareWriters.esc(x.modern())).append(" | ").append(CompareWriters.esc(x.detail())).append(" |\n");
                }
                b.append('\n');
            }
            if (p.evidence != null && !p.evidence.isEmpty()) b.append("**COBOL-side evidence.** ").append(p.evidence).append("\n\n");
            if (p.hypothesis != null && !p.hypothesis.isEmpty()) b.append("**Generic rule hypothesis.** ").append(p.hypothesis).append("\n\n");
        }
        if (!res.unclassified.isEmpty()) {
            b.append("## Gap codes no pattern claims\n\n| Code |");
            for (Estate e : res.estates) b.append(' ').append(e.name()).append(" |");
            b.append("\n|---|").append("---|".repeat(res.estates.size())).append('\n');
            for (Map.Entry<String, Map<String, Integer>> e : res.unclassified.entrySet()) {
                b.append("| `").append(e.getKey()).append("` |");
                for (Estate es : res.estates) b.append(' ').append(e.getValue().getOrDefault(es.name(), 0)).append(" |");
                b.append('\n');
            }
            b.append('\n');
        }
        if (!c.split.isEmpty()) {
            b.append("## Train/test split\n\n");
            for (String l : c.split) b.append(l).append('\n');
            b.append('\n');
        }
        if (!c.method.isEmpty()) {
            b.append("## Method\n\n");
            for (String l : c.method) b.append(l).append('\n');
            b.append('\n');
        }
        return b.toString();
    }

    private static String anchor(String id, String title) {
        return (id + "-" + title).toLowerCase().replaceAll("[^a-z0-9 -]", "").replace(' ', '-');
    }
}
