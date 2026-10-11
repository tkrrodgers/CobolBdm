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
