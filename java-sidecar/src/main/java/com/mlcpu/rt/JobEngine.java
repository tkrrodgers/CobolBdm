package com.mlcpu.rt;

import java.io.FileOutputStream;
import java.io.IOException;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.function.Consumer;
import java.util.function.Supplier;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * Executes a harvested JCL job (§5.6.3): COBOL program steps, DFSORT (SORT/INCLUDE with SYMNAMES), IDCAMS
 * (REPRO, DELETE, DEFINE CLUSTER), IEBGENER and IEFBR14. Datasets map to files under the data directory by DSN;
 * GDG generations map to the base name. JCL COND is retained but not executed (§13.5); an unsupported step throws
 * {@link JobFrontierException}. A step that abends ends the job.
 */
public final class JobEngine {

    public record Dd(String name, String dsn, String member, String generation, boolean dummy, boolean sysout, String instream, Integer lrecl) {}

    public record Step(String id, String program, String parm, String cond, Supplier<CobolBase> factory, Supplier<ModernStep> modern, List<Dd> dds) {
        public Step(String id, String program, String parm, String cond, Supplier<CobolBase> factory, List<Dd> dds) {
            this(id, program, parm, cond, factory, null, dds);
        }
    }

    /** "legacy" (faithful Prog_* classes) or "normalized" (ModernStep where one exists); per-program entries override. */
    public static volatile String IMPLEMENTATION = "legacy";
    public static final Map<String, String> IMPLEMENTATION_BY_PROGRAM = new java.util.concurrent.ConcurrentHashMap<>();

    /** Normalized CALLed subprograms by program id; a CALL reaches one when the job selects "normalized" for that program. */
    public static final Map<String, Supplier<ModernProgram>> MODERN_PROGRAMS = new java.util.concurrent.ConcurrentHashMap<>();

    /** Compiler, binder and assembler steps: they build load modules and touch no dataset the job reads or writes. */
    static final java.util.Set<String> BUILD_UTILITIES = java.util.Set.of("IGYCRCTL", "IGYWCL", "IGYWCLG", "IEWL", "HEWL", "IEWBLINK",
            "HEWLH096", "IEWLF440", "ASMA90", "IBMZPLI", "IEL1AA", "CCNDRVR", "DFHECP1$", "DSNHPC");

    static String implementationOf(String program) {
        return program == null ? IMPLEMENTATION : IMPLEMENTATION_BY_PROGRAM.getOrDefault(program.toUpperCase(), IMPLEMENTATION);
    }

    static boolean normalized(Step s) {
        if (s.modern() == null) return false;
        return "normalized".equalsIgnoreCase(implementationOf(s.program()));
    }

    /** CALL resolution for one step: every converted program, then the normalized subprograms the job selects. */
    private void prepareCalls() {
        ProgramRegistry.reset();
        programs.forEach((id, f) -> ProgramRegistry.register(id, f.get()));
        MODERN_PROGRAMS.forEach((id, f) -> {
            if ("normalized".equalsIgnoreCase(implementationOf(id))) ProgramRegistry.registerModern(id, f.get());
        });
    }

    /**
     * Runs the faithful program of the step behind {@code ctx} (the bridge a not-yet-modernized ModernStep placeholder
     * uses): same DDs, PARM and SYSOUT, as if the job had selected the legacy implementation.
     */
    public static int runFaithful(ModernStep.Context ctx) throws IOException {
        if (!(ctx instanceof EngineContext ec)) throw new IllegalStateException("runFaithful needs the job engine's context");
        if (ec.step.factory() == null) throw new IllegalStateException("step " + ec.step.id() + " has no faithful program");
        ec.sink.flush();
        StepResult r = ec.engine.program(ec.step);
        return r.returnCode();
    }

    public record StepResult(String id, String program, int returnCode, String detail) {}

    public static final class JobFrontierException extends RuntimeException {
        public JobFrontierException(String m) { super(m); }
    }

    public record ProgramRun(int returnCode, List<String> display, String outcome) {}

    /** Runs one program as a step outside a job (typed services): DDs bound to files, optional PARM, DISPLAY captured. */
    public static synchronized ProgramRun runProgram(CobolBase main, Map<String, Path> dds, String parm) {
        CobFile.DD.clear();
        dds.forEach((dd, p) -> CobFile.DD.put(dd.toUpperCase(), p.toString()));
        ProgramRegistry.reset();
        ProgramRegistry.register(main.programId(), main);
        CobRt.resetRunUnit();
        DisplaySink prev = DisplaySink.current();
        DisplaySink.Capturing cap = new DisplaySink.Capturing();
        DisplaySink.set(cap);
        String outcome = "COMPLETED";
        int rc;
        try {
            main.call(parm == null ? new CobField[0] : new CobField[]{parmArea(parm)}, false);
            CobFile.closeAllOpen();
            rc = CobRt.RETURN_CODE.dec().intValue();
        } catch (CobRt.StopRun e) {
            CobFile.closeAllOpen();
            rc = CobRt.RETURN_CODE.dec().intValue();
        } catch (CobRt.Abend a) {
            CobFile.closeAllOpen(); // implicit CLOSE, as GnuCOBOL does when the CEE3ABD stub or a runtime error ends the run unit
            outcome = "ABEND:" + a.code;
            rc = 16;
        } finally {
            DisplaySink.set(prev);
        }
        return new ProgramRun(rc, List.copyOf(cap.lines()), outcome);
    }

    /** z/OS PARM linkage: halfword length, then the text, in a 102-byte area. */
    public static CobField parmArea(String parm) {
        byte[] text = parm.getBytes(StandardCharsets.ISO_8859_1);
        byte[] area = new byte[2 + 100];
        java.util.Arrays.fill(area, (byte) ' ');
        area[0] = (byte) (text.length >> 8);
        area[1] = (byte) text.length;
        System.arraycopy(text, 0, area, 2, text.length);
        return CobField.temp(area);
    }

    private final Path dataDir;
    private final Path sysoutDir;
    private final Path ctlDir;
    private final Map<String, Supplier<CobolBase>> programs;
    private final Consumer<String> log;

    public JobEngine(Path dataDir, Path sysoutDir, Path ctlDir, Map<String, Supplier<CobolBase>> programs, Consumer<String> log) {
        this.dataDir = dataDir;
        this.sysoutDir = sysoutDir;
        this.ctlDir = ctlDir;
        this.programs = programs;
        this.log = log;
    }

    public List<StepResult> run(List<Step> steps, java.util.Set<String> only) throws IOException {
        Files.createDirectories(dataDir);
        Files.createDirectories(sysoutDir);
        List<StepResult> results = new ArrayList<>();
        for (Step s : steps) {
            if (!only.isEmpty() && !only.contains(s.id())) continue;
            if (s.cond() != null) log.accept(s.id() + ": COND=" + s.cond() + " retained, not evaluated");
            StepResult r;
            try {
                r = execute(s);
            } catch (JobFrontierException e) {
                r = new StepResult(s.id(), s.program(), 12, "FRONTIER " + e.getMessage());
            }
            log.accept(String.format("%-20s %-10s rc=%d %s", s.id(), s.program(), r.returnCode(), r.detail()));
            results.add(r);
            if (r.returnCode() >= 12) break;
        }
        return results;
    }

    private StepResult execute(Step s) throws IOException {
        String pgm = s.program() == null ? "" : s.program().toUpperCase();
        if (normalized(s)) return modern(s);
        if (s.factory() != null) return program(s);
        return switch (pgm) {
            case "SORT", "ICEMAN", "SYNCSORT", "DFSORT" -> sort(s);
            case "IDCAMS" -> idcams(s);
            case "IEBGENER", "ICEGENER" -> {
                Files.copy(path(s, dd(s, "SYSUT1")), path(s, dd(s, "SYSUT2")), java.nio.file.StandardCopyOption.REPLACE_EXISTING);
                yield new StepResult(s.id(), pgm, 0, "copied SYSUT1 to SYSUT2");
            }
            case "IEFBR14" -> new StepResult(s.id(), pgm, 0, "no operation");
            case String b when BUILD_UTILITIES.contains(b) -> new StepResult(s.id(), pgm, 0, "build step (compile/link): no effect on datasets");
            default -> throw new JobFrontierException("program " + pgm + " is neither converted nor a supported utility");
        };
    }

    // ------------------------------------------------------------------ normalized step

    /** The context a normalized step sees; also what {@link #runFaithful} needs to run the faithful program instead. */
    private final class EngineContext implements ModernStep.Context {
        final Step step;
        final JobEngine engine = JobEngine.this;
        final DisplaySink.ToStream sink;

        EngineContext(Step step, DisplaySink.ToStream sink) {
            this.step = step;
            this.sink = sink;
        }

        @Override public Path dataset(String dd) {
            try {
                return path(step, dd(step, dd));
            } catch (IOException e) {
                throw new java.io.UncheckedIOException(e);
            }
        }
        @Override public String parm() { return step.parm(); }
        @Override public void display(String line) { sink.write(line, true); }
        @Override public java.time.OffsetDateTime now() { return CobClock.now(); }
        @Override public String displayNumeric() { return CobConfig.DISPLAY_NUMERIC; }
        @Override public byte storageDefault() { return CobConfig.DEFAULT_BYTE; }
        @Override public void call(String program, byte[]... parameters) {
            ModernProgram.callProgram(program, parameters);
        }
    }

    private StepResult modern(Step s) throws IOException {
        Path sysout = sysoutDir.resolve(s.id() + ".SYSOUT.txt");
        for (Dd d : s.dds()) if (d.name().equals("SYSOUT")) sysout = path(s, d);
        CobFile.DD.clear();
        for (Dd d : s.dds()) if (!d.name().isEmpty()) CobFile.DD.put(d.name(), path(s, d).toString());
        prepareCalls();
        CobRt.resetRunUnit();
        try (OutputStream os = new FileOutputStream(sysout.toFile(), true)) {
            DisplaySink.ToStream sink = new DisplaySink.ToStream(os);
            DisplaySink prev = DisplaySink.current();
            DisplaySink.set(sink);
            EngineContext ctx = new EngineContext(s, sink);
            try {
                int rc = s.modern().get().run(ctx);
                return new StepResult(s.id(), s.program(), rc, "normalized; SYSOUT " + sysout.getFileName());
            } catch (ModernStep.Abend a) {
                sink.write(abendLine(a.code), true);
                return new StepResult(s.id(), s.program(), 16, "normalized ABEND " + a.code);
            } catch (CobRt.Abend a) {
                sink.write(abendLine(a.code), true);
                return new StepResult(s.id(), s.program(), 16, "normalized ABEND " + a.code + " (in a CALLed program)");
            } catch (IOException | RuntimeException e) {
                sink.write("RUNTIME ERROR " + e, true);
                return new StepResult(s.id(), s.program(), 20, "normalized " + e);
            } catch (Exception e) {
                sink.write("RUNTIME ERROR " + e, true);
                return new StepResult(s.id(), s.program(), 20, "normalized " + e);
            } finally {
                sink.flush();
                DisplaySink.set(prev);
            }
        }
    }

    /** The system's abend line (user abend code), identical whichever implementation abended. */
    /**
     * The SYSOUT line of an abend: what the oracle's CEE3ABD stub DISPLAYs before its STOP RUN (return code 16), so a
     * step that abends has the same SYSOUT on GnuCOBOL, in the faithful translation and in a modern implementation.
     */
    static String abendLine(int code) {
        return "ABEND CEE3ABD";
    }

    // ------------------------------------------------------------------ COBOL program step

    private StepResult program(Step s) throws IOException {
        CobFile.DD.clear();
        Path sysout = sysoutDir.resolve(s.id() + ".SYSOUT.txt");
        for (Dd d : s.dds()) {
            if (d.name().isEmpty()) continue;
            Path p = path(s, d);
            if (d.name().equals("SYSOUT")) sysout = p;
            CobFile.DD.put(d.name(), p.toString());
        }
        prepareCalls();
        CobolBase main = s.factory().get();
        ProgramRegistry.register(s.program(), main);
        CobRt.resetRunUnit();
        try (OutputStream os = new FileOutputStream(sysout.toFile(), true)) {
            DisplaySink.ToStream sink = new DisplaySink.ToStream(os);
            DisplaySink prev = DisplaySink.current();
            DisplaySink.set(sink);
            try {
                CobField[] using = new CobField[0];
                if (s.parm() != null) {
                    byte[] text = s.parm().getBytes(StandardCharsets.ISO_8859_1);
                    byte[] area = new byte[2 + 100];
                    java.util.Arrays.fill(area, (byte) ' ');
                    area[0] = (byte) (text.length >> 8);
                    area[1] = (byte) text.length;
                    System.arraycopy(text, 0, area, 2, text.length);
                    using = new CobField[]{CobField.temp(area)};
                }
                try {
                    main.call(using, false);
                } catch (CobRt.StopRun e) {
                    // end of run unit
                }
                CobFile.closeAllOpen();
                return new StepResult(s.id(), s.program(), CobRt.RETURN_CODE.dec().intValue(), "SYSOUT " + sysout.getFileName());
            } catch (CobRt.Abend a) {
                CobFile.closeAllOpen(); // implicit CLOSE, as GnuCOBOL does when the CEE3ABD stub or a runtime error ends the run unit
                sink.write(abendLine(a.code), true);
                return new StepResult(s.id(), s.program(), 16, "ABEND " + a.code);
            } catch (CobRt.FrontierException | CobRt.RuntimeCheck | CobRt.LoopLimit | CobRt.CallNotFound e) {
                CobFile.closeAllOpen(); // implicit CLOSE, as GnuCOBOL does when the CEE3ABD stub or a runtime error ends the run unit
                sink.write("RUNTIME ERROR " + e.getMessage(), true);
                return new StepResult(s.id(), s.program(), 20, e.getMessage());
            } finally {
                sink.flush();
                DisplaySink.set(prev);
            }
        }
    }

    // ------------------------------------------------------------------ utilities

    private StepResult sort(Step s) throws IOException {
        String sysin = text(s, dd(s, "SYSIN"));
        List<String> cards = SortReplay.statements(sysin);
        Dd sym = find(s, "SYMNAMES");
        if (sym != null) cards = SortReplay.resolveSymbols(cards, text(s, sym));
        List<Path> inputs = new ArrayList<>();
        Integer lrecl = null;
        boolean inSortin = false;
        for (Dd d : s.dds()) {
            if (d.name().equals("SORTIN")) inSortin = true;
            else if (!d.name().isEmpty()) inSortin = false;
            if (!inSortin) continue;
            inputs.add(path(s, d));
            if (lrecl == null) lrecl = d.lrecl();
        }
        Dd out = dd(s, "SORTOUT");
        if (out.lrecl() != null) lrecl = out.lrecl();
        if (lrecl == null) throw new JobFrontierException(s.id() + ": record length of SORTIN is not established");
        try {
            int[] n = SortReplay.run(cards, inputs, lrecl, path(s, out));
            return new StepResult(s.id(), "SORT", 0, n[1] + " of " + n[0] + " records");
        } catch (SortReplay.Refused r) {
            throw new JobFrontierException(r.getMessage());
        }
    }

    private static final Pattern REPRO = Pattern.compile("REPRO\\s+(INFILE|INDATASET)\\(([^)]+)\\)\\s+(OUTFILE|OUTDATASET)\\(([^)]+)\\)", Pattern.CASE_INSENSITIVE);
    private static final Pattern DELETE = Pattern.compile("^DELETE\\s+([A-Z0-9#@$.]+)", Pattern.CASE_INSENSITIVE);
    private static final Pattern DEFINE_CLUSTER = Pattern.compile("DEFINE\\s+CLUSTER\\s*\\(\\s*NAME\\(([^)]+)\\)", Pattern.CASE_INSENSITIVE);

    private StepResult idcams(Step s) throws IOException {
        String sysin = text(s, dd(s, "SYSIN"));
        // Commands end where a line does not continue with '-'.
        List<String> commands = new ArrayList<>();
        StringBuilder cur = new StringBuilder();
        for (String raw : sysin.split("\n")) {
            String line = raw.replaceAll("/\\*.*?\\*/", "").stripTrailing();
            if (line.isBlank()) continue;
            boolean cont = line.endsWith("-");
            cur.append(' ').append(cont ? line.substring(0, line.length() - 1) : line);
            if (!cont) { commands.add(cur.toString().trim()); cur.setLength(0); }
        }
        if (cur.length() > 0) commands.add(cur.toString().trim());
        List<String> done = new ArrayList<>();
        for (String c : commands) {
            String u = c.toUpperCase().replaceAll("\\s+", " ");
            Matcher m;
            if ((m = REPRO.matcher(u)).find()) {
                Path in = m.group(1).equals("INFILE") ? path(s, dd(s, m.group(2).trim())) : dataDir.resolve(m.group(2).trim());
                Path out = m.group(3).equals("OUTFILE") ? path(s, dd(s, m.group(4).trim())) : dataDir.resolve(m.group(4).trim());
                if (out.getParent() != null) Files.createDirectories(out.getParent());
                Files.copy(in, out, java.nio.file.StandardCopyOption.REPLACE_EXISTING);
                done.add("REPRO " + in.getFileName() + " -> " + out.getFileName());
            } else if ((m = DELETE.matcher(u)).find()) {
                Files.deleteIfExists(dataDir.resolve(m.group(1)));
                done.add("DELETE " + m.group(1));
            } else if ((m = DEFINE_CLUSTER.matcher(u)).find()) {
                Path p = dataDir.resolve(m.group(1).trim());
                if (!Files.exists(p)) Files.write(p, new byte[0]);
                done.add("DEFINE " + m.group(1).trim());
            } else if (u.startsWith("DEFINE ALTERNATEINDEX") || u.startsWith("DEFINE PATH") || u.startsWith("BLDINDEX")
                    || u.startsWith("SET ") || u.startsWith("IF ") || u.startsWith("LISTCAT")) {
                done.add(u.split(" ")[0] + " " + u.split(" ").length + " (no effect on record images)");
            } else {
                throw new JobFrontierException(s.id() + ": IDCAMS command not supported: " + c);
            }
        }
        return new StepResult(s.id(), "IDCAMS", 0, String.join("; ", done));
    }

    // ------------------------------------------------------------------ datasets

    private static Dd find(Step s, String name) {
        for (Dd d : s.dds()) if (d.name().equals(name)) return d;
        return null;
    }

    private static Dd dd(Step s, String name) {
        Dd d = find(s, name);
        if (d == null) throw new JobFrontierException(s.id() + ": DD " + name + " is not allocated");
        return d;
    }

    private String text(Step s, Dd d) throws IOException {
        if (d.instream() != null) return d.instream();
        return Files.readString(path(s, d), StandardCharsets.ISO_8859_1);
    }

    private final Map<String, Integer> instreamSeq = new LinkedHashMap<>();

    /** File for a DD: in-stream data and SYSOUT go to the sysout directory, members to the control-card library. */
    public Path path(Step s, Dd d) throws IOException {
        String ddName = d.name().isEmpty() ? "CONCAT" : d.name();
        if (d.sysout()) return sysoutDir.resolve(s.id() + "." + ddName + ".txt");
        if (d.instream() != null) {
            int n = instreamSeq.merge(s.id() + "." + ddName, 1, Integer::sum);
            Path p = sysoutDir.resolve(s.id() + "." + ddName + (n > 1 ? "." + n : "") + ".instream");
            Files.writeString(p, d.instream(), StandardCharsets.ISO_8859_1);
            return p;
        }
        if (d.dummy() || d.dsn() == null) {
            Path p = sysoutDir.resolve(s.id() + "." + ddName + ".dummy");
            Files.write(p, new byte[0], StandardOpenOption.CREATE, StandardOpenOption.TRUNCATE_EXISTING);
            return p;
        }
        if (d.member() != null) {
            Path card = ctlDir.resolve(d.member() + ".ctl");
            if (Files.exists(card)) return card;
            return dataDir.resolve(d.dsn() + "(" + d.member() + ")");
        }
        return dataDir.resolve(d.dsn());
    }
}
