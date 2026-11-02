package com.mlcpu.jobol;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import java.util.Set;

/**
 * jobol-audit: is this generated Java modern Java, or COBOL in Java syntax?
 * Exit codes: 0 gate passed (or --no-gate), 1 gate failed, 2 usage or I/O error.
 */
public final class Main {
    private static final String USAGE = """
        usage: jobol-audit <estate-dir|file.java> [options]
               jobol-audit compare --original DIR --modern DIR [--cobol DIR] [--out DIR]   (see compare --help)
               jobol-audit compare-gaps --report FILE [--report FILE ...] [--catalogue FILE] [--out DIR]
          --out DIR          write jobol-report.json/.md/.sarif to DIR (default: ./jobol-out)
          --format LIST      comma list of json,md,sarif (default: all)
          --config FILE      lane globs, rule overrides and gate (default: built-in, see --print-config)
          --baseline FILE    a previous jobol-report.json: report the change and fail on regression
          --max-index N      override gate.maxEstateIndex
          --loc MODE         physical (default, ruleset 1.1.0) | code (ruleset 1.2.0: sizes and estate weights count
                             code lines only; comments and blank lines never move a score)
          --top N            types listed in the Markdown report and on the console (default 25)
          --no-gate          always exit 0 (report only)
          --quiet            no console summary
          --print-config     print the built-in configuration and exit
          --rules            print the rule catalog and exit
        """;

    public static void main(String[] args) {
        System.exit(run(args));
    }

    static int run(String[] args) {
        if (args.length > 0 && args[0].equals("compare")) return Compare.run(java.util.Arrays.copyOfRange(args, 1, args.length));
        if (args.length > 0 && args[0].equals("compare-gaps")) return Gaps.run(java.util.Arrays.copyOfRange(args, 1, args.length));
        Path target = null;
        Path out = Path.of("jobol-out");
        Path configFile = null;
        Path baseline = null;
        Set<String> formats = Set.of("json", "md", "sarif");
        Double maxIndex = null;
        int top = 25;
        boolean gate = true;
        boolean quiet = false;
        String loc = null;
        try {
            for (int i = 0; i < args.length; i++) {
                String a = args[i];
                switch (a) {
                    case "--out" -> out = Path.of(args[++i]);
                    case "--format" -> formats = Set.of(args[++i].split(","));
                    case "--config" -> configFile = Path.of(args[++i]);
                    case "--baseline" -> baseline = Path.of(args[++i]);
                    case "--max-index" -> maxIndex = Double.parseDouble(args[++i]);
                    case "--top" -> top = Integer.parseInt(args[++i]);
                    case "--no-gate" -> gate = false;
                    case "--quiet" -> quiet = true;
                    case "--loc" -> {
                        loc = args[++i];
                        if (!loc.equals("physical") && !loc.equals("code")) throw new IllegalArgumentException("--loc physical|code");
                    }
                    case "--print-config" -> {
                        System.out.print(Config.defaultsText());
                        return 0;
                    }
                    case "--rules" -> {
                        for (Rules.Spec s : Rules.ALL) {
                            System.out.printf("%s %-32s %-7s %s%n        fix: %s%n", s.id(), s.name(), s.severity(), s.summary(), s.fix());
                        }
                        return 0;
                    }
                    case "-h", "--help" -> {
                        System.out.print(USAGE);
                        return 0;
                    }
                    default -> {
                        if (a.startsWith("--") || target != null) throw new IllegalArgumentException("unexpected argument " + a);
                        target = Path.of(a);
                    }
                }
            }
        } catch (ArrayIndexOutOfBoundsException | IllegalArgumentException e) {
            System.err.println("jobol-audit: " + (e instanceof ArrayIndexOutOfBoundsException ? "missing option value" : e.getMessage()));
            System.err.print(USAGE);
            return 2;
        }
        if (target == null || !Files.exists(target)) {
            System.err.println("jobol-audit: " + (target == null ? "no estate given" : "not found: " + target));
            System.err.print(USAGE);
            return 2;
        }
        try {
            Config cfg = configFile == null ? Config.defaults() : Config.load(configFile);
            if (maxIndex != null) cfg.gate.maxEstateIndex = maxIndex;
            if (loc != null) cfg.loc = loc;
            Auditor auditor = new Auditor(cfg);
            Report r = auditor.audit(target);
            if (baseline != null) auditor.compare(r, baseline);
            Files.createDirectories(out);
            if (formats.contains("json")) write(out.resolve("jobol-report.json"), Writers.json(r));
            if (formats.contains("md")) write(out.resolve("jobol-report.md"), Writers.markdown(r, top));
            if (formats.contains("sarif")) write(out.resolve("jobol-report.sarif"), Writers.sarif(r));
            if (!quiet) console(r, out, Math.min(top, 10));
            return !gate || r.gate.passed ? 0 : 1;
        } catch (IOException | RuntimeException e) {
            System.err.println("jobol-audit: " + e);
            return 2;
        }
    }

    private static void write(Path p, String text) throws IOException {
        Files.writeString(p, text, StandardCharsets.UTF_8);
    }

    private static void console(Report r, Path out, int top) {
        Report.Summary s = r.summary;
        System.out.printf("JOBOL audit of %s%n", r.root);
        System.out.printf("  gate %s | estate index %.1f (%s) | modern lane %.1f%% | %d files, %d types, %d parse errors%n",
            r.gate.passed ? "PASS" : "FAIL", s.estateIndex, s.estateGrade, s.modernLanePct, s.files, s.types, r.parseErrors.size());
        System.out.printf("  grades A %d  B %d  C %d  D %d  F %d | findings %s%n", s.grades.get("A"), s.grades.get("B"),
            s.grades.get("C"), s.grades.get("D"), s.grades.get("F"), s.findingsBySeverity);
        if (r.baseline != null) {
            System.out.printf("  baseline %.1f -> %.1f (%+.1f), %d new / %d resolved findings%s%n", r.baseline.previousIndex,
                r.baseline.currentIndex, r.baseline.change, r.baseline.newFindings, r.baseline.resolvedFindings,
                r.baseline.comparable ? "" : " [different ruleset]");
        }
        for (String why : r.gate.reasons) System.out.println("  - " + why);
        List<TypeReport> worst = r.types.stream().filter(t -> t.zone().equals("modern") && t.index() > 0).limit(top).toList();
        if (!worst.isEmpty()) System.out.println("  worst modern types:");
        for (TypeReport t : worst) System.out.printf("    %3d %s  %s (%s)%n", t.index(), t.grade(), t.type(), t.file());
        System.out.println("  reports: " + out.toAbsolutePath().normalize());
    }
}
