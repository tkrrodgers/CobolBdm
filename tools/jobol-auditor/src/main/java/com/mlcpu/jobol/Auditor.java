package com.mlcpu.jobol;

import com.github.javaparser.JavaParser;
import com.github.javaparser.ParseResult;
import com.github.javaparser.ParserConfiguration;
import com.github.javaparser.ast.CompilationUnit;
import com.google.gson.Gson;
import java.io.IOException;
import java.nio.ByteBuffer;
import java.nio.charset.CharacterCodingException;
import java.nio.charset.CodingErrorAction;
import java.nio.charset.StandardCharsets;
import java.nio.file.FileVisitResult;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.SimpleFileVisitor;
import java.nio.file.attribute.BasicFileAttributes;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.HashMap;
import java.util.HashSet;
import java.util.HexFormat;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.TreeMap;

/** Walks an estate, parses every .java file, assigns lanes, analyzes, scores and gates. Deterministic: no clock, sorted output. */
public final class Auditor {
    private final Config cfg;
    private final Analyzer analyzer;
    private final JavaParser parser;

    public Auditor(Config cfg) {
        this.cfg = cfg;
        this.analyzer = new Analyzer(cfg);
        this.parser = new JavaParser(new ParserConfiguration()
            .setLanguageLevel(ParserConfiguration.LanguageLevel.JAVA_21)
            .setAttributeComments(true));
    }

    public Report audit(Path root) throws IOException {
        Path base = Files.isDirectory(root) ? root : root.getParent();
        List<Path> files = collect(root);
        Report r = new Report();
        r.root = root.toAbsolutePath().normalize().toString().replace('\\', '/');
        r.rulesetHash = rulesetHash();
        for (Zone z : Zone.values()) if (z != Zone.EXCLUDED) r.summary.lanes.put(z.id(), new Report.Lane());
        Set<String> filesSeen = new HashSet<>();
        for (Path p : files) {
            String rel = base.relativize(p).toString().replace('\\', '/');
            Zone zone = cfg.zoneOf(rel);
            if (zone == Zone.EXCLUDED) continue;
            r.summary.files++;
            ParseResult<CompilationUnit> pr = parser.parse(read(p));
            if (!pr.isSuccessful() || pr.getResult().isEmpty()) {
                String msg = pr.getProblems().isEmpty() ? "unparseable" : pr.getProblems().get(0).getVerboseMessage();
                r.parseErrors.add(new Report.ParseError(rel, Analyzer.abbreviate(msg, 200)));
                continue;
            }
            r.summary.parsed++;
            Report.Lane lane = r.summary.lanes.get(zone.id());
            if (filesSeen.add(rel)) lane.files++;
            for (TypeReport t : analyzer.analyze(pr.getResult().get(), rel, zone)) {
                r.types.add(t);
                lane.types++;
                lane.loc += t.loc();
            }
        }
        r.summary.types = r.types.size();
        r.types.sort(Comparator.comparingInt(TypeReport::index).reversed().thenComparing(TypeReport::key));
        summarize(r);
        for (Rules.Spec s : cfg.effectiveRules()) {
            List<String> zs = s.zones().stream().map(Zone::id).sorted().toList();
            r.rules.add(new Report.RuleRef(s.id(), s.name(), s.severity().name(), zs, s.cap(), s.summary(), s.fix()));
        }
        gate(r);
        return r;
    }

    private void summarize(Report r) {
        Report.Summary s = r.summary;
        long weighted = 0;
        long modernLoc = 0;
        for (String g : List.of("A", "B", "C", "D", "F")) s.grades.put(g, 0);
        for (Severity v : Severity.values()) s.findingsBySeverity.put(v.name(), 0);
        Map<String, Integer> byRule = new TreeMap<>();
        Map<String, Integer> signals = new TreeMap<>();
        for (TypeReport t : r.types) {
            if (!t.zone().equals(Zone.MODERN.id()) && !t.zone().equals(Zone.COMPAT.id())) continue;
            for (Finding f : t.findings()) {
                s.findingsBySeverity.merge(f.severity().name(), 1, Integer::sum);
                byRule.merge(f.rule(), 1, Integer::sum);
            }
            if (!t.zone().equals(Zone.MODERN.id())) continue;
            weighted += (long) t.index() * t.loc();
            modernLoc += t.loc();
            s.grades.merge(t.grade(), 1, Integer::sum);
            t.signals().forEach((k, v) -> signals.merge(k, v, Integer::sum));
        }
        s.findingsByRule.putAll(byRule);
        s.estateIndex = modernLoc == 0 ? 0 : Math.round(10.0 * weighted / modernLoc) / 10.0;
        s.estateGrade = modernLoc == 0 ? "-" : Scorer.grade(s.estateIndex);
        long audited = 0;
        for (String lane : List.of("modern", "compat", "faithful")) audited += s.lanes.get(lane).loc;
        s.modernLanePct = audited == 0 ? 0 : Math.round(1000.0 * modernLoc / audited) / 10.0;
        double kloc = modernLoc / 1000.0;
        for (Map.Entry<String, Integer> e : signals.entrySet()) {
            s.signalsPerKloc.put(e.getKey(), kloc == 0 ? 0 : Math.round(10.0 * e.getValue() / kloc) / 10.0);
        }
    }

    private void gate(Report r) {
        Config.Gate g = cfg.gate;
        r.gate.thresholds = g;
        List<String> why = r.gate.reasons;
        Report.Lane modern = r.summary.lanes.get("modern");
        if (modern.types == 0) {
            why.add("no modern-lane code: every audited file is in the faithful, compat or test lane");
        }
        int blockers = r.summary.findingsBySeverity.getOrDefault("BLOCKER", 0);
        if (g.failOnBlocker && blockers > 0) {
            why.add(blockers + " BLOCKER finding(s) in the modern/compat lanes");
        }
        if (r.summary.estateIndex > g.maxEstateIndex) {
            why.add("estate JOBOL index " + r.summary.estateIndex + " exceeds " + g.maxEstateIndex);
        }
        int worstAllowed = Scorer.rank(g.worstGradeAllowed);
        int worse = 0;
        for (TypeReport t : r.types) {
            if (t.zone().equals("modern") && Scorer.rank(t.grade()) > worstAllowed) worse++;
        }
        if (worse > 0) why.add(worse + " modern type(s) graded worse than " + g.worstGradeAllowed);
        r.gate.passed = why.isEmpty();
    }

    /** Adds the comparison with a previous report and applies the regression threshold. */
    public void compare(Report r, Path baselineFile) throws IOException {
        Report prev;
        try (var reader = Files.newBufferedReader(baselineFile, StandardCharsets.UTF_8)) {
            prev = new Gson().fromJson(reader, Report.class);
        }
        Report.Delta d = new Report.Delta();
        d.baselineFile = baselineFile.toAbsolutePath().normalize().toString().replace('\\', '/');
        d.comparable = r.rulesetHash.equals(prev.rulesetHash);
        d.previousIndex = prev.summary.estateIndex;
        d.currentIndex = r.summary.estateIndex;
        d.change = Math.round(10.0 * (d.currentIndex - d.previousIndex)) / 10.0;
        Map<String, Integer> before = new HashMap<>();
        Map<String, Integer> beforeFindings = new HashMap<>();
        for (TypeReport t : prev.types) {
            if (!"modern".equals(t.zone())) continue;
            before.put(t.key(), t.index());
            for (Finding f : t.findings()) beforeFindings.merge(findingKey(f), 1, Integer::sum);
        }
        Set<String> seen = new HashSet<>();
        for (TypeReport t : r.types) {
            if (!"modern".equals(t.zone())) continue;
            seen.add(t.key());
            Integer p = before.get(t.key());
            if (p == null) d.added.add(t.key());
            else if (t.index() > p) d.regressed.add(new Report.TypeDelta(t.key(), p, t.index()));
            else if (t.index() < p) d.improved.add(new Report.TypeDelta(t.key(), p, t.index()));
            for (Finding f : t.findings()) {
                String k = findingKey(f);
                int left = beforeFindings.getOrDefault(k, 0);
                if (left > 0) {
                    beforeFindings.put(k, left - 1);
                } else {
                    d.newFindings++;
                    if (f.severity() == Severity.BLOCKER) d.newBlockers++;
                }
            }
        }
        for (int left : beforeFindings.values()) d.resolvedFindings += left;
        for (String k : before.keySet()) if (!seen.contains(k)) d.removed.add(k);
        d.regressed.sort(Comparator.comparingInt((Report.TypeDelta x) -> x.current() - x.previous()).reversed()
            .thenComparing(Report.TypeDelta::key));
        d.improved.sort(Comparator.comparingInt((Report.TypeDelta x) -> x.current() - x.previous())
            .thenComparing(Report.TypeDelta::key));
        d.added.sort(null);
        d.removed.sort(null);
        r.baseline = d;
        if (!d.comparable) {
            r.gate.reasons.add("note: baseline used a different ruleset (" + prev.rulesetHash + "); regression check skipped");
        } else if (d.change > cfg.gate.maxRegression) {
            r.gate.reasons.add("estate JOBOL index regressed by " + d.change + " (allowed " + cfg.gate.maxRegression + ")");
            r.gate.passed = false;
        }
    }

    /** Lines shift between generations, so findings are matched by rule, place and message, not line. */
    private static String findingKey(Finding f) {
        return f.rule() + "|" + f.file() + "|" + f.type() + "|" + f.message();
    }

    private List<Path> collect(Path root) throws IOException {
        List<Path> out = new ArrayList<>();
        if (Files.isRegularFile(root)) {
            out.add(root);
            return out;
        }
        Set<String> skip = new HashSet<>(cfg.excludeDirs);
        Files.walkFileTree(root, new SimpleFileVisitor<>() {
            @Override
            public FileVisitResult preVisitDirectory(Path dir, BasicFileAttributes attrs) {
                Path name = dir.getFileName();
                return !dir.equals(root) && name != null && skip.contains(name.toString())
                    ? FileVisitResult.SKIP_SUBTREE : FileVisitResult.CONTINUE;
            }

            @Override
            public FileVisitResult visitFile(Path file, BasicFileAttributes attrs) {
                String n = file.getFileName().toString();
                if (n.endsWith(".java") && !n.equals("package-info.java") && !n.equals("module-info.java")) out.add(file);
                return FileVisitResult.CONTINUE;
            }
        });
        out.sort(Comparator.comparing(p -> p.toString().replace('\\', '/')));
        return out;
    }

    private static String read(Path p) throws IOException {
        byte[] bytes = Files.readAllBytes(p);
        try {
            return StandardCharsets.UTF_8.newDecoder()
                .onMalformedInput(CodingErrorAction.REPORT)
                .onUnmappableCharacter(CodingErrorAction.REPORT)
                .decode(ByteBuffer.wrap(bytes)).toString();
        } catch (CharacterCodingException e) {
            return new String(bytes, StandardCharsets.ISO_8859_1);
        }
    }

    private String rulesetHash() {
        StringBuilder sb = new StringBuilder(Report.VERSION).append('\n');
        for (Rules.Spec s : cfg.effectiveRules()) {
            sb.append(s.id()).append(s.severity()).append(s.zones()).append(s.cap()).append('\n');
        }
        for (Config.ZoneGlobs z : cfg.zones) sb.append(z.zone).append(z.globs).append('\n');
        sb.append(cfg.legacyRuntimePackages).append(cfg.legacyRuntimeTypes).append(cfg.cobolPrefixes).append(cfg.moneyWords);
        try {
            byte[] h = MessageDigest.getInstance("SHA-256").digest(sb.toString().getBytes(StandardCharsets.UTF_8));
            return HexFormat.of().formatHex(h, 0, 8);
        } catch (NoSuchAlgorithmException e) {
            throw new IllegalStateException(e);
        }
    }
}
