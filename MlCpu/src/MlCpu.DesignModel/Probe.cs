using System.Text.Json.Nodes;

namespace MlCpu.DesignModel;

/// <summary>One probed scenario: both runs' outputs, the faithful run's effects in order, and (subprograms) every CALL.</summary>
public sealed record ProbeRun(int ReturnCode, string Detail, IReadOnlyDictionary<string, byte[]> Files);

public sealed record ProbeEvent(string Op, string? Dd, int Status, string? Advancing, byte[] Data, IReadOnlyList<ProbeFrame> At);

/// <summary>A faithful paragraph on the call stack: the class (its source holds the statement tags) and the Java line.</summary>
public sealed record ProbeFrame(string Class, string Method, int Line);

public sealed record ProbeCall(IReadOnlyList<byte[]> Before, IReadOnlyList<byte[]> After);

public sealed record ProbeScenario(string Name, ProbeRun Legacy, ProbeRun Yours, IReadOnlyList<ProbeEvent> Trace, int TraceTotal,
    IReadOnlyList<ProbeCall> LegacyCalls, IReadOnlyList<ProbeCall> YourCalls);

/// <summary>One CALL of the shadow run whose results differ: the arguments before, as the faithful program left them, and as yours did.</summary>
public sealed record ShadowCall(int Seq, IReadOnlyList<byte[]> Before, IReadOnlyList<byte[]> Faithful, IReadOnlyList<byte[]> Yours, string? FaithfulEnd, string? YoursEnd,
    ShadowNested? Nested = null);

/// <summary>An argument of a logged CALL: its bytes, or for a large record (a 262 KB model) only its length and a hash.</summary>
public sealed record ShadowArg(byte[]? Bytes, int Length, string? Hash)
{
    public bool SameAs(ShadowArg o) => Bytes is not null && o.Bytes is not null ? Bytes.AsSpan().SequenceEqual(o.Bytes) : Hash is not null && Hash == o.Hash;
}

/// <summary>A CALL that a shadowed CALL made to one of its callees: the arguments before and after, and how it ended.</summary>
public sealed record ShadowNestedCall(string Program, IReadOnlyList<ShadowArg> Before, IReadOnlyList<ShadowArg> After, string? End,
    IReadOnlyList<string>? At = null, IReadOnlyList<string>? Own = null)
{
    /// <summary>The faithful paragraphs on the stack when the CALL was made ("class#method:line"), innermost first.</summary>
    public IReadOnlyList<ProbeFrame> Frames => (At ?? []).Select(Probe.Frame).ToList();
}

/// <summary>
/// The CALLs a shadowed CALL made to its callees, in the faithful run and in yours, in order (kit feature subprogram-calls);
/// the totals count CALLs past the ones logged.
/// </summary>
public sealed record ShadowNested(IReadOnlyList<ShadowNestedCall> Faithful, IReadOnlyList<ShadowNestedCall> Yours, int FaithfulTotal, int YoursTotal, bool Truncated);

/// <summary>A step's CALLs in one scenario, the faithful step's against yours, with how each run ended.</summary>
public sealed record StepCallsScenario(string Name, int FaithfulRc, string? FaithfulDetail, int YoursRc, string? YoursDetail, ShadowNested Nested);

/// <summary>A scenario of the shadow run: CALLs made, how many differ, the first differing ones.</summary>
public sealed record ShadowScenario(string Name, int Calls, int Differing, IReadOnlyList<ShadowCall> First);

/// <summary>
/// The difference probe (pipeline v2). A JUnit class compiled into the harness next to the acceptance test runs the failing
/// scenarios through the test's own scenarios(), step(), subprograms() and @BeforeAll setup (by reflection), once with the
/// faithful program and once with the model's code, and writes build/mlcpu-probe.json: every output of both runs, the
/// faithful run's READ/WRITE/REWRITE/DELETE/DISPLAY events in order, each with the faithful paragraphs on the stack, and for a
/// subprogram every CALL's arguments before and after, in both runs. FeedbackRenderer turns it into field-level feedback.
/// </summary>
public static class Probe
{
    public const string ProbeClass = "MlcpuDiffProbe";

    public static string Source(string package, string testClass, string kind, string programId) => $$"""
        package {{package}};

        import com.mlcpu.rt.JobEngine;
        import com.mlcpu.rt.ModernProgram;
        import com.mlcpu.rt.ProgramRegistry;
        import com.mlcpu.rt.StepEquivalence;
        import java.lang.reflect.Method;
        import java.nio.charset.StandardCharsets;
        import java.nio.file.Files;
        import java.nio.file.Path;
        import java.util.*;
        import java.util.function.Supplier;
        import org.junit.jupiter.api.Test;
        import org.junit.jupiter.params.provider.Arguments;

        class {{ProbeClass}} {
            static final String KIND = "{{kind}}";
            static final String PROGRAM = "{{programId}}";
            static final int MAX_EVENTS = 8000, MAX_CALLS = 400;

            @Test
            @SuppressWarnings("unchecked")
            void probe() throws Exception {
                List<String> wanted = new ArrayList<>();
                for (String l : Files.readAllLines(Path.of("build", "mlcpu-probe-request.txt"), StandardCharsets.UTF_8)) if (!l.isBlank()) wanted.add(l.trim());
                Class<?> t = Class.forName("{{testClass}}");
                for (Method m : t.getDeclaredMethods())
                    if (m.isAnnotationPresent(org.junit.jupiter.api.BeforeAll.class)) { m.setAccessible(true); m.invoke(null); }
                Method scenarios = t.getDeclaredMethod("scenarios");
                scenarios.setAccessible(true);
                Map<String, Object[]> byName = new LinkedHashMap<>();
                for (Arguments a : ((java.util.stream.Stream<Arguments>) scenarios.invoke(null)).toList()) byName.put((String) a.get()[0], a.get());
                Map<String, Supplier<ModernProgram>> yours = Map.of();
                for (Method m : t.getDeclaredMethods())
                    if (m.getName().equals("subprograms") && m.getParameterCount() == 0) { m.setAccessible(true); yours = (Map<String, Supplier<ModernProgram>>) m.invoke(null); }
                StringBuilder out = new StringBuilder("{\"schema\":\"mlcpu.probe.v2\",\"scenarios\":[");
                boolean firstScenario = true;
                for (String name : wanted) {
                    Object[] args = byName.get(name);
                    if (args == null) continue;
                    Method step = null;
                    for (Method m : t.getDeclaredMethods()) if (m.getName().equals("step") && m.getParameterCount() == args.length - 2) step = m;
                    step.setAccessible(true);
                    JobEngine.Step s = (JobEngine.Step) (args.length == 3 ? step.invoke(null, args[1]) : step.invoke(null));
                    Map<String, byte[]> inputs = (Map<String, byte[]>) args[args.length - 1];
                    List<String> events = new ArrayList<>();
                    int[] total = {0};
                    List<String> legacyCalls = new ArrayList<>(), yourCalls = new ArrayList<>();
                    StepEquivalence.Outcome legacy, normalized;
                    com.mlcpu.rt.CobFile.TRACE = e -> {
                        total[0]++;
                        if (events.size() < MAX_EVENTS)
                            events.add("{\"op\":" + q(e.op()) + ",\"dd\":" + q(e.dd()) + ",\"status\":" + e.status() + ",\"adv\":" + q(e.advancing())
                                       + ",\"data\":" + b64(e.record()) + ",\"at\":" + frames() + "}");
                    };
                    setDisplayTrace(line -> {
                        total[0]++;
                        if (events.size() < MAX_EVENTS)
                            events.add("{\"op\":\"DISPLAY\",\"dd\":\"SYSOUT\",\"status\":0,\"adv\":null,\"data\":" + b64(line.getBytes(StandardCharsets.ISO_8859_1))
                                       + ",\"at\":" + frames() + "}");
                    });
                    try {
                        if (KIND.equals("subprogram")) {
                            JobEngine.Step main = new JobEngine.Step(s.id(), s.program(), s.parm(), s.cond(), s.factory(), null, s.dds());
                            legacy = StepEquivalence.run(main, inputs, "normalized", Map.of(PROGRAM, () -> traced(params -> ProgramRegistry.callFaithful(PROGRAM, params), legacyCalls)));
                            com.mlcpu.rt.CobFile.TRACE = null;
                            setDisplayTrace(null);
                            Supplier<ModernProgram> model = yours.get(PROGRAM);
                            normalized = StepEquivalence.run(main, inputs, "normalized", Map.of(PROGRAM, () -> traced(model.get(), yourCalls)));
                        } else {
                            legacy = StepEquivalence.run(s, inputs, "legacy");
                            com.mlcpu.rt.CobFile.TRACE = null;
                            setDisplayTrace(null);
                            normalized = StepEquivalence.run(s, inputs, "normalized", yours);
                        }
                    } finally {
                        com.mlcpu.rt.CobFile.TRACE = null;
                        setDisplayTrace(null);
                    }
                    if (!firstScenario) out.append(',');
                    firstScenario = false;
                    out.append("{\"name\":").append(q(name)).append(",\"legacy\":").append(run(legacy)).append(",\"yours\":").append(run(normalized))
                       .append(",\"traceTotal\":").append(total[0]).append(",\"trace\":[").append(String.join(",", events)).append("]")
                       .append(",\"legacyCalls\":[").append(String.join(",", legacyCalls)).append("],\"yourCalls\":[").append(String.join(",", yourCalls)).append("]}");
                }
                out.append("]}\n");
                Files.writeString(Path.of("build", "mlcpu-probe.json"), out.toString(), StandardCharsets.UTF_8);
            }

            /**
             * CALL-level shadow run (kit feature shadow-feedback): each failing scenario runs once more with the faithful
             * program answering every CALL, and the model's program answering the same CALL on its own copy of the
             * arguments; the run goes on with the faithful results, so every CALL gets the faithful caller's inputs. Writes
             * build/mlcpu-shadow.json: per scenario the CALLs made, how many differ, and the first ones in full.
             */
            @Test
            @SuppressWarnings("unchecked")
            void shadow() throws Exception {
                Path request = Path.of("build", "mlcpu-shadow-request.txt");
                if (!KIND.equals("subprogram") || !Files.exists(request)) return;
                List<String> wanted = new ArrayList<>();
                for (String l : Files.readAllLines(request, StandardCharsets.UTF_8)) if (!l.isBlank()) wanted.add(l.trim());
                Class<?> t = Class.forName("{{testClass}}");
                for (Method m : t.getDeclaredMethods())
                    if (m.isAnnotationPresent(org.junit.jupiter.api.BeforeAll.class)) { m.setAccessible(true); m.invoke(null); }
                Method scenarios = t.getDeclaredMethod("scenarios");
                scenarios.setAccessible(true);
                Map<String, Object[]> byName = new LinkedHashMap<>();
                for (Arguments a : ((java.util.stream.Stream<Arguments>) scenarios.invoke(null)).toList()) byName.put((String) a.get()[0], a.get());
                Map<String, Supplier<ModernProgram>> yours = Map.of();
                for (Method m : t.getDeclaredMethods())
                    if (m.getName().equals("subprograms") && m.getParameterCount() == 0) { m.setAccessible(true); yours = (Map<String, Supplier<ModernProgram>>) m.invoke(null); }
                Supplier<ModernProgram> model = yours.get(PROGRAM);
                // The programs it CALLs (build/mlcpu-shadow-callees.txt, kit feature subprogram-calls) answer through a tracer that
                // logs the CALLs made directly by the shadowed CALL, faithful and yours, and runs the faithful callee.
                List<String> callees = new ArrayList<>();
                Path calleeFile = Path.of("build", "mlcpu-shadow-callees.txt");
                if (Files.exists(calleeFile))
                    for (String l : Files.readAllLines(calleeFile, StandardCharsets.UTF_8)) if (!l.isBlank() && !l.trim().equals(PROGRAM)) callees.add(l.trim());
                StringBuilder out = new StringBuilder("{\"schema\":\"mlcpu.shadow.v1\",\"program\":" + q(PROGRAM) + ",\"scenarios\":[");
                boolean firstScenario = true;
                int found = 0;
                for (String name : wanted) {
                    Object[] args = byName.get(name);
                    if (args == null || model == null || found >= 3) continue;
                    Method step = null;
                    for (Method m : t.getDeclaredMethods()) if (m.getName().equals("step") && m.getParameterCount() == args.length - 2) step = m;
                    step.setAccessible(true);
                    JobEngine.Step s = (JobEngine.Step) (args.length == 3 ? step.invoke(null, args[1]) : step.invoke(null));
                    Map<String, byte[]> inputs = (Map<String, byte[]>) args[args.length - 1];
                    JobEngine.Step main = new JobEngine.Step(s.id(), s.program(), s.parm(), s.cond(), s.factory(), null, s.dds());
                    int[] seq = {0}, differing = {0};
                    List<String> first = new ArrayList<>();
                    SHADOW_KINDS.clear();
                    Map<String, Supplier<ModernProgram>> run = new HashMap<>();
                    run.put(PROGRAM, () -> shadowOf(model.get(), seq, differing, first));
                    for (String c : callees) run.put(c, () -> nestedTracer(c));
                    StepEquivalence.Outcome o = StepEquivalence.run(main, inputs, "normalized", run);
                    if (differing[0] > 0) found++;
                    if (!firstScenario) out.append(',');
                    firstScenario = false;
                    out.append("{\"name\":").append(q(name)).append(",\"calls\":").append(seq[0]).append(",\"differing\":").append(differing[0])
                       .append(",\"rc\":").append(o.returnCode()).append(",\"first\":[").append(String.join(",", first)).append("]}");
                }
                out.append("]}\n");
                Files.writeString(Path.of("build", "mlcpu-shadow.json"), out.toString(), StandardCharsets.UTF_8);
            }

            /**
             * The CALLs a step makes, the faithful step's against yours (kit feature subprogram-calls): each requested scenario runs
             * the job twice in normalized mode with the programs the step CALLs answered by the tracer (the faithful program behind
             * it), once with the faithful step (JobEngine.runFaithful as its implementation) and once with yours. Writes
             * build/mlcpu-stepcalls.json: per scenario both return codes and the CALLs of both runs, for the pipeline to align.
             */
            @Test
            @SuppressWarnings("unchecked")
            void stepCalls() throws Exception {
                Path request = Path.of("build", "mlcpu-stepcalls-request.txt");
                Path calleeFile = Path.of("build", "mlcpu-shadow-callees.txt");
                if (KIND.equals("subprogram") || !Files.exists(request) || !Files.exists(calleeFile)) return;
                List<String> wanted = new ArrayList<>(), callees = new ArrayList<>();
                for (String l : Files.readAllLines(request, StandardCharsets.UTF_8)) if (!l.isBlank()) wanted.add(l.trim());
                for (String l : Files.readAllLines(calleeFile, StandardCharsets.UTF_8)) if (!l.isBlank()) callees.add(l.trim());
                Class<?> t = Class.forName("{{testClass}}");
                for (Method m : t.getDeclaredMethods())
                    if (m.isAnnotationPresent(org.junit.jupiter.api.BeforeAll.class)) { m.setAccessible(true); m.invoke(null); }
                Method scenarios = t.getDeclaredMethod("scenarios");
                scenarios.setAccessible(true);
                Map<String, Object[]> byName = new LinkedHashMap<>();
                for (Arguments a : ((java.util.stream.Stream<Arguments>) scenarios.invoke(null)).toList()) byName.put((String) a.get()[0], a.get());
                StringBuilder out = new StringBuilder("{\"schema\":\"mlcpu.stepcalls.v1\",\"program\":" + q(PROGRAM) + ",\"scenarios\":[");
                boolean firstScenario = true;
                for (String name : wanted) {
                    Object[] args = byName.get(name);
                    if (args == null) continue;
                    Method step = null;
                    for (Method m : t.getDeclaredMethods()) if (m.getName().equals("step") && m.getParameterCount() == args.length - 2) step = m;
                    step.setAccessible(true);
                    JobEngine.Step s = (JobEngine.Step) (args.length == 3 ? step.invoke(null, args[1]) : step.invoke(null));
                    Map<String, byte[]> inputs = (Map<String, byte[]>) args[args.length - 1];
                    Map<String, Supplier<ModernProgram>> run = new HashMap<>();
                    for (String c : callees) run.put(c, () -> nestedTracer(c));
                    JobEngine.Step faithfulStep = new JobEngine.Step(s.id(), s.program(), s.parm(), s.cond(), s.factory(),
                        () -> ctx -> JobEngine.runFaithful(ctx), s.dds());
                    List<Object[]> f = NESTED = new ArrayList<>();
                    NEST_DEPTH = 0;
                    StepEquivalence.Outcome fo;
                    try {
                        fo = StepEquivalence.run(faithfulStep, inputs, "normalized", run);
                    } finally {
                        NESTED = null;
                        NEST_DEPTH = 0;
                    }
                    List<Object[]> y = NESTED = new ArrayList<>();
                    StepEquivalence.Outcome yo;
                    try {
                        yo = StepEquivalence.run(s, inputs, "normalized", run);
                    } finally {
                        NESTED = null;
                        NEST_DEPTH = 0;
                    }
                    if (!firstScenario) out.append(',');
                    firstScenario = false;
                    out.append("{\"name\":").append(q(name)).append(",\"faithfulRc\":").append(fo.returnCode()).append(",\"faithfulDetail\":").append(q(fo.detail()))
                       .append(",\"yoursRc\":").append(yo.returnCode()).append(",\"yoursDetail\":").append(q(yo.detail()))
                       .append(",\"nested\":").append(nestedJson(f, y)).append('}');
                }
                out.append("]}\n");
                Files.writeString(Path.of("build", "mlcpu-stepcalls.json"), out.toString(), StandardCharsets.UTF_8);
            }

            static final Set<String> SHADOW_KINDS = new HashSet<>();

            /** Faithful on the caller's arguments, the model on a copy; the first differing CALL of each kind is kept in full. */
            static ModernProgram shadowOf(ModernProgram yours, int[] seq, int[] differing, List<String> first) {
                return params -> {
                    int n = ++seq[0];
                    byte[][] before = new byte[params.length][];
                    byte[][] copy = new byte[params.length][];
                    for (int i = 0; i < params.length; i++) { before[i] = params[i].clone(); copy[i] = params[i].clone(); }
                    Throwable faithfulEnd = null;
                    List<Object[]> faithfulNested = NESTED = new ArrayList<>();
                    try {
                        ProgramRegistry.callFaithful(PROGRAM, params);
                    } catch (Throwable e) {
                        faithfulEnd = e;
                    } finally {
                        NESTED = null;
                    }
                    String yoursEnd = null;
                    List<Object[]> yoursNested = NESTED = new ArrayList<>();
                    try {
                        yours.call(copy);
                    } catch (Throwable e) {
                        yoursEnd = e.getClass().getSimpleName() + ": " + e.getMessage() + thrownAt(e);
                    } finally {
                        NESTED = null;
                        NEST_DEPTH = 0;
                    }
                    boolean same = (faithfulEnd == null) == (yoursEnd == null);
                    for (int i = 0; same && i < params.length; i++) same = Arrays.equals(params[i], copy[i]);
                    if (!same) {
                        differing[0]++;
                        // Kept in full: the first CALL of each kind of difference (which bytes differ, and how the CALLs ended), up to 12.
                        StringBuilder kind = new StringBuilder(faithfulEnd == null ? "-" : "F").append(yoursEnd == null ? "-" : "Y");
                        for (int i = 0; i < params.length; i++) {
                            int k = 0, n2 = Math.min(params[i].length, copy[i].length);
                            while (k < n2) {
                                if (params[i][k] == copy[i][k]) { k++; continue; }
                                int s0 = k;
                                while (k < n2 && params[i][k] != copy[i][k]) k++;
                                kind.append(';').append(i).append(':').append(s0).append('-').append(k);
                            }
                        }
                        if (first.size() < 12 && SHADOW_KINDS.add(kind.toString())) {
                            List<String> b = new ArrayList<>(), f = new ArrayList<>(), y = new ArrayList<>();
                            for (int i = 0; i < params.length; i++) { b.add(b64(before[i])); f.add(b64(params[i])); y.add(b64(copy[i])); }
                            first.add("{\"seq\":" + n + ",\"before\":[" + String.join(",", b) + "],\"faithful\":[" + String.join(",", f) + "],\"yours\":["
                                      + String.join(",", y) + "],\"faithfulEnd\":" + q(faithfulEnd == null ? null : faithfulEnd.getClass().getSimpleName() + ": " + faithfulEnd.getMessage())
                                      + ",\"yoursEnd\":" + q(yoursEnd)
                                      + (faithfulNested.isEmpty() && yoursNested.isEmpty() ? "" : ",\"nested\":" + nestedJson(faithfulNested, yoursNested)) + "}");
                        }
                    }
                    if (faithfulEnd instanceof RuntimeException re) throw re;
                    if (faithfulEnd instanceof Error er) throw er;
                    if (faithfulEnd != null) throw new IllegalStateException(faithfulEnd);
                };
            }

            /** The CALLs the shadowed CALL is making to its callees ({program, before[], after[], end}); null outside it. */
            static List<Object[]> NESTED;
            static int NEST_DEPTH;
            static boolean NESTED_TRUNCATED;
            static final int MAX_NESTED = 2000, MAX_NESTED_SHOWN = 2000, SMALL_ARGUMENT = 4096;

            /**
             * An argument as logged: its bytes when small, else its length and a hash (a 262 KB model is compared, not kept: a
             * CALL can make hundreds of CALLs).
             */
            static Object logged(byte[] b) {
                if (b.length <= SMALL_ARGUMENT) return b.clone();
                java.util.zip.CRC32 c = new java.util.zip.CRC32();
                c.update(b);
                return "{\"len\":" + b.length + ",\"hash\":\"" + Long.toHexString(c.getValue()) + "-" + Integer.toHexString(Arrays.hashCode(b)) + "\"}";
            }

            /** A callee: runs the faithful program; a CALL made directly by the shadowed CALL is logged (not the callee's own CALLs). */
            static ModernProgram nestedTracer(String id) {
                return params -> {
                    List<Object[]> log = NEST_DEPTH == 0 ? NESTED : null;
                    Object[] before = null;
                    if (log != null) {
                        before = new Object[params.length];
                        for (int i = 0; i < params.length; i++) before[i] = logged(params[i]);
                    }
                    String end = null;
                    // Where the CALL was made: the faithful paragraphs on the stack, and the model's own frames (one of the two is empty).
                    String at = log == null ? null : frames(), own = log == null ? null : ownFrames();
                    NEST_DEPTH++;
                    try {
                        ProgramRegistry.callFaithful(id, params);
                    } catch (RuntimeException e) {
                        end = e.getClass().getSimpleName() + ": " + e.getMessage();
                        throw e;
                    } finally {
                        NEST_DEPTH--;
                        if (log != null) {
                            if (log.size() < MAX_NESTED) {
                                Object[] after = new Object[params.length];
                                for (int i = 0; i < params.length; i++) after[i] = logged(params[i]);
                                log.add(new Object[] {id, before, after, end, at, own});
                            } else NESTED_TRUNCATED = true;
                        }
                    }
                };
            }

            static String argJson(Object a) {
                return a instanceof byte[] b ? b64(b) : (String) a;
            }

            static String callJson(Object[] e) {
                List<String> b = new ArrayList<>(), a = new ArrayList<>();
                for (Object x : (Object[]) e[1]) b.add(argJson(x));
                for (Object x : (Object[]) e[2]) a.add(argJson(x));
                return "{\"program\":" + q((String) e[0]) + ",\"before\":[" + String.join(",", b) + "],\"after\":[" + String.join(",", a) + "],\"end\":" + q((String) e[3])
                       + ",\"at\":" + (e.length > 4 && e[4] != null ? (String) e[4] : "[]") + ",\"own\":" + (e.length > 5 && e[5] != null ? (String) e[5] : "[]") + "}";
            }

            /** The model's own frames on the stack (its package; not the faithful legacy classes, not tests), innermost first. */
            static String ownFrames() {
                String pkg = {{ProbeClass}}.class.getPackageName() + ".";
                List<String> f = StackWalker.getInstance().walk(st -> st
                    .filter(x -> x.getClassName().startsWith(pkg) && !x.getClassName().contains(".legacy.")
                                 && !x.getClassName().endsWith("EquivalenceTest") && !x.getClassName().contains("{{ProbeClass}}"))
                    .limit(4).map(x -> q(x.getClassName().substring(pkg.length()) + "." + x.getMethodName() + "(" + x.getFileName() + ":" + x.getLineNumber() + ")"))
                    .toList());
                return "[" + String.join(",", f) + "]";
            }

            /** The CALLs to callees in both runs, in order (the first 300 of each), for the pipeline to align and compare. */
            static String nestedJson(List<Object[]> f, List<Object[]> y) {
                StringBuilder sb = new StringBuilder("{\"faithful\":[");
                for (int i = 0; i < Math.min(f.size(), MAX_NESTED_SHOWN); i++) sb.append(i > 0 ? "," : "").append(callJson(f.get(i)));
                sb.append("],\"yours\":[");
                for (int i = 0; i < Math.min(y.size(), MAX_NESTED_SHOWN); i++) sb.append(i > 0 ? "," : "").append(callJson(y.get(i)));
                sb.append("],\"faithfulTotal\":").append(f.size()).append(",\"yoursTotal\":").append(y.size())
                  .append(",\"truncated\":").append(NESTED_TRUNCATED).append('}');
                NESTED_TRUNCATED = false;
                return sb.toString();
            }

            /**
             * " (thrown by BigDecimal.pow at Class.method(File.java:n))": the JDK call that threw, when the innermost frame is one,
             * and the innermost frame of the model's own classes (not legacy, not tests).
             */
            static String thrownAt(Throwable e) {
                String pkg = {{ProbeClass}}.class.getPackageName() + ".";
                StackTraceElement[] st = e.getStackTrace();
                String by = st.length > 0 && st[0].getClassName().startsWith("java.")
                    ? "by " + st[0].getClassName().substring(st[0].getClassName().lastIndexOf('.') + 1) + "." + st[0].getMethodName() + " " : "";
                for (StackTraceElement f : st) {
                    String c = f.getClassName();
                    if (c.startsWith(pkg) && !c.contains(".legacy.") && !c.endsWith("EquivalenceTest") && !c.contains("{{ProbeClass}}"))
                        return " (thrown " + by + "at " + c.substring(pkg.length()) + "." + f.getMethodName() + "(" + f.getFileName() + ":" + f.getLineNumber() + "))";
                }
                return "";
            }

            /** CobRt.DISPLAY_TRACE when the runtime has it (published by pipeline v2); otherwise DISPLAY events are not traced. */
            static void setDisplayTrace(java.util.function.Consumer<String> c) {
                try {
                    com.mlcpu.rt.CobRt.class.getField("DISPLAY_TRACE").set(null, c);
                } catch (ReflectiveOperationException e) {
                    // older runtime: I/O events only
                }
            }

            static ModernProgram traced(ModernProgram target, List<String> calls) {
                return params -> {
                    List<String> before = new ArrayList<>();
                    for (byte[] p : params) before.add(b64(p));
                    try {
                        target.call(params);
                    } finally {
                        if (calls.size() < MAX_CALLS) {
                            List<String> after = new ArrayList<>();
                            for (byte[] p : params) after.add(b64(p));
                            calls.add("{\"before\":[" + String.join(",", before) + "],\"after\":[" + String.join(",", after) + "]}");
                        }
                    }
                };
            }

            /** The faithful paragraphs on the stack, innermost first: class#method:line. */
            static String frames() {
                List<String> f = StackWalker.getInstance().walk(st -> st
                    .filter(x -> x.getClassName().substring(x.getClassName().lastIndexOf('.') + 1).startsWith("Prog_") && x.getMethodName().matches("p\\d+_.*"))
                    .limit(16).map(x -> q(x.getClassName() + "#" + x.getMethodName() + ":" + x.getLineNumber())).toList());
                return "[" + String.join(",", f) + "]";
            }

            static String run(StepEquivalence.Outcome o) {
                StringBuilder sb = new StringBuilder("{\"rc\":").append(o.returnCode()).append(",\"detail\":").append(q(o.detail())).append(",\"files\":{");
                boolean first = true;
                for (var e : new TreeMap<>(o.files()).entrySet()) {
                    if (!first) sb.append(',');
                    first = false;
                    sb.append(q(e.getKey())).append(':').append(b64(e.getValue()));
                }
                return sb.append('}').append('}').toString();
            }

            static String b64(byte[] b) {
                return "\"" + Base64.getEncoder().encodeToString(b) + "\"";
            }

            static String q(String s) {
                if (s == null) return "null";
                StringBuilder sb = new StringBuilder("\"");
                for (char c : s.toCharArray()) {
                    if (c == '"' || c == '\\') sb.append('\\').append(c);
                    else if (c < 0x20 || c > 0x7E) sb.append(String.format("\\u%04x", (int) c));
                    else sb.append(c);
                }
                return sb.append('"').toString();
            }
        }
        """;

    static byte[] B64(JsonNode? n) => n is null ? [] : Convert.FromBase64String(n.GetValue<string>());

    static ShadowArg Arg(JsonNode? n)
    {
        if (n is JsonObject o) return new(null, o["len"]!.GetValue<int>(), o["hash"]!.GetValue<string>());
        var b = B64(n);
        return new(b, b.Length, null);
    }

    static ShadowNestedCall NestedCall(JsonNode n) => new(n["program"]!.GetValue<string>(),
        n["before"]!.AsArray().Select(Arg).ToList(), n["after"]!.AsArray().Select(Arg).ToList(), n["end"]?.GetValue<string>(),
        n["at"]?.AsArray().Select(x => x!.GetValue<string>()).ToList(), n["own"]?.AsArray().Select(x => x!.GetValue<string>()).ToList());

    /// <summary>A faithful frame as the probe writes it: "class#method:line".</summary>
    public static ProbeFrame Frame(string s)
    {
        var hash = s.IndexOf('#');
        var colon = s.LastIndexOf(':');
        return new ProbeFrame(s[..hash], s[(hash + 1)..colon], int.Parse(s[(colon + 1)..]));
    }

    static ShadowNested? NestedOf(JsonNode? n) => n is null ? null : new(
        n["faithful"]!.AsArray().Select(c => NestedCall(c!)).ToList(), n["yours"]!.AsArray().Select(c => NestedCall(c!)).ToList(),
        n["faithfulTotal"]!.GetValue<int>(), n["yoursTotal"]!.GetValue<int>(), n["truncated"]?.GetValue<bool>() ?? false);

    /// <summary>build/mlcpu-stepcalls.json: a step's CALLs per scenario, the faithful step's against yours.</summary>
    public static IReadOnlyList<StepCallsScenario> ParseStepCalls(string json) =>
        JsonNode.Parse(json)!["scenarios"]!.AsArray().Select(s => new StepCallsScenario(s!["name"]!.GetValue<string>(),
            s["faithfulRc"]!.GetValue<int>(), s["faithfulDetail"]?.GetValue<string>(), s["yoursRc"]!.GetValue<int>(), s["yoursDetail"]?.GetValue<string>(),
            NestedOf(s["nested"])!)).ToList();

    public static IReadOnlyList<ShadowScenario> ParseShadow(string json)
    {
        static byte[] B(JsonNode? n) => B64(n);
        static ShadowNested? Nested(JsonNode? n) => NestedOf(n);
        return JsonNode.Parse(json)!["scenarios"]!.AsArray().Select(s => new ShadowScenario(s!["name"]!.GetValue<string>(), s["calls"]!.GetValue<int>(),
            s["differing"]!.GetValue<int>(), s["first"]!.AsArray().Select(c => new ShadowCall(c!["seq"]!.GetValue<int>(),
                c["before"]!.AsArray().Select(B).ToList(), c["faithful"]!.AsArray().Select(B).ToList(), c["yours"]!.AsArray().Select(B).ToList(),
                c["faithfulEnd"]?.GetValue<string>(), c["yoursEnd"]?.GetValue<string>(), Nested(c["nested"]))).ToList())).ToList();
    }

    public static IReadOnlyList<ProbeScenario> Parse(string json)
    {
        var root = JsonNode.Parse(json)!;
        static byte[] B(JsonNode? n) => n is null ? [] : Convert.FromBase64String(n.GetValue<string>());
        static ProbeRun Run(JsonNode n) => new(n["rc"]!.GetValue<int>(), n["detail"]?.GetValue<string>() ?? "",
            n["files"]!.AsObject().ToDictionary(kv => kv.Key, kv => B(kv.Value), StringComparer.Ordinal));
        static ProbeFrame Frame(string s)
        {
            var hash = s.IndexOf('#');
            var colon = s.LastIndexOf(':');
            return new ProbeFrame(s[..hash], s[(hash + 1)..colon], int.Parse(s[(colon + 1)..]));
        }
        static List<ProbeCall> Calls(JsonNode? n) => n?.AsArray().Select(c => new ProbeCall(
            c!["before"]!.AsArray().Select(B).ToList(), c["after"]!.AsArray().Select(B).ToList())).ToList() ?? [];
        return root["scenarios"]!.AsArray().Select(s => new ProbeScenario(
            s!["name"]!.GetValue<string>(), Run(s["legacy"]!), Run(s["yours"]!),
            s["trace"]!.AsArray().Select(e => new ProbeEvent(e!["op"]!.GetValue<string>(), e["dd"]?.GetValue<string>(), e["status"]!.GetValue<int>(),
                e["adv"]?.GetValue<string>(), B(e["data"]), e["at"]!.AsArray().Select(f => Frame(f!.GetValue<string>())).ToList())).ToList(),
            s["traceTotal"]!.GetValue<int>(), Calls(s["legacyCalls"]), Calls(s["yourCalls"]))).ToList();
    }
}
