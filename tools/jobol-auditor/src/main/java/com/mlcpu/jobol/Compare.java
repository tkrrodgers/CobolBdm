package com.mlcpu.jobol;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;

/** jobol-audit compare: the distance of a modern estate from its original Java. Exit 0, or 2 on a usage or I/O error. */
public final class Compare {
    private Compare() {
    }

    static final String USAGE = """
        usage: jobol-audit compare --original DIR [--original DIR ...] --modern DIR [options]
          --original DIR     original source root; repeatable; a '*' segment expands (vendor/fineract/*/src/main/java)
          --modern DIR       modern source root (src/main/java of the generated estate)
          --cobol DIR        J3S COBOL of the estate: CLASS headers give the reached classes, FRAME comments the reached
                             methods; sets --scope reached
          --scope S          all | reached (default: reached with --cobol, else all). reached: original types J3S compiled
                             or that the modern estate names (by fully qualified or unique simple name)
          --name NAME        estate name in the report (default: the modern root's estate directory)
          --out DIR          write compare-report.json/.md to DIR (default: ./compare-out)
          --config FILE      lanes and compareInfrastructureGlobs (default: built-in)
          --examples N       examples per gap pattern in the Markdown report (default 3)
          --quiet            no console summary
        """;

    static int run(String[] args) {
        List<String> originals = new ArrayList<>();
        Path modern = null;
        Path cobol = null;
        Path out = Path.of("compare-out");
        Path configFile = null;
        String scope = null;
        String name = null;
        int examples = 3;
        boolean quiet = false;
        try {
            for (int i = 0; i < args.length; i++) {
                String a = args[i];
                switch (a) {
                    case "--original" -> originals.add(args[++i]);
                    case "--modern" -> modern = Path.of(args[++i]);
                    case "--cobol" -> cobol = Path.of(args[++i]);
                    case "--scope" -> {
                        scope = args[++i];
                        if (!scope.equals("all") && !scope.equals("reached")) throw new IllegalArgumentException("--scope all|reached");
                    }
                    case "--name" -> name = args[++i];
                    case "--out" -> out = Path.of(args[++i]);
                    case "--config" -> configFile = Path.of(args[++i]);
                    case "--examples" -> examples = Integer.parseInt(args[++i]);
                    case "--quiet" -> quiet = true;
                    case "-h", "--help" -> {
                        System.out.print(USAGE);
                        return 0;
                    }
                    default -> throw new IllegalArgumentException("unexpected argument " + a);
                }
            }
        } catch (ArrayIndexOutOfBoundsException | IllegalArgumentException e) {
            System.err.println("jobol-audit compare: " + (e instanceof ArrayIndexOutOfBoundsException ? "missing option value" : e.getMessage()));
            System.err.print(USAGE);
            return 2;
        }
        List<Path> roots = new ArrayList<>();
        for (String o : originals) roots.addAll(Comparer.expand(o));
        if (roots.isEmpty() || modern == null || !Files.isDirectory(modern) || roots.stream().anyMatch(p -> !Files.isDirectory(p))
            || cobol != null && !Files.isDirectory(cobol)) {
            System.err.println("jobol-audit compare: --original and --modern must name existing directories" + (cobol != null ? " (and --cobol)" : ""));
            System.err.print(USAGE);
            return 2;
        }
        if (name == null) name = estateName(modern);
        try {
            Config cfg = configFile == null ? Config.defaults() : Config.load(configFile);
            CompareReport r = new Comparer(cfg).compare(name, roots, modern, cobol, scope);
            Files.createDirectories(out);
            Files.writeString(out.resolve("compare-report.json"), CompareWriters.json(r), StandardCharsets.UTF_8);
            Files.writeString(out.resolve("compare-report.md"), CompareWriters.markdown(r, examples), StandardCharsets.UTF_8);
            if (!quiet) {
                CompareReport.Summary s = r.summary;
                System.out.printf("compare %s: %d matched (%d moved), %d original-only, %d modern-only (%d infrastructure)%n", r.estate,
                    s.matched, s.matchedMoved, s.originalOnly, s.modernOnlyInfrastructure + s.modernOnlyOther, s.modernOnlyInfrastructure);
                System.out.printf("  API %s (reached %s) | names %s | signatures %s (reached %s)%n", CompareWriters.pct(s.api),
                    CompareWriters.pct(s.apiReached), CompareWriters.pct(s.names), CompareWriters.pct(s.signatures), CompareWriters.pct(s.signaturesReached));
                System.out.println("  reports: " + out.toAbsolutePath().normalize());
            }
            return 0;
        } catch (IOException | RuntimeException e) {
            System.err.println("jobol-audit compare: " + e);
            return 2;
        }
    }

    /** .../kontor-zins-m5/src/main/java is kontor-zins-m5. */
    static String estateName(Path modern) {
        Path p = modern.toAbsolutePath().normalize();
        for (int i = 0; i < 3 && p.getParent() != null; i++) {
            String n = p.getFileName().toString();
            if (!n.equals("java") && !n.equals("main") && !n.equals("src")) break;
            p = p.getParent();
        }
        return p.getFileName().toString();
    }
}
