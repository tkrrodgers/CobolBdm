package com.mlcpu.rt;

import java.io.IOException;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;

/**
 * Runs one step's faithful program and records CALLs to a subprogram: each argument's bytes before and after the call
 * (the worked examples of a subprogram capability). Arguments are recorded as ISO-8859-1 strings.
 *
 * <p>At most {@code --max} calls are kept, spread evenly over the whole run: calls are kept at a stride that doubles
 * (dropping every other kept call) whenever the buffer overflows. A subprogram called thousands of times per run is then
 * shown on early and late inputs alike, not only on the first record. Deterministic.
 *
 * <p>{@code --probe-out} writes a second, larger sample of the same run (same rule, {@code --probe-max} calls) for the
 * library-equivalence probe ({@link EquivalenceProbe}); the first sample is unchanged by it.
 *
 * <pre>CallTrace --program SAM1 --trace SAM2 --dd CUSTFILE=in.dat ... [--parm P] [--clock FIXED=iso] --out trace.json [--max 50]
 *           [--probe-out probe-trace.json --probe-max 500]</pre>
 */
public final class CallTrace {
    private CallTrace() {}

    record Call(int seq, List<String> before, List<String> after) {}

    public static void main(String[] args) throws IOException {
        String program = null, trace = null, out = null, parm = null, probeOut = null;
        int max = 50, probeMax = 500;
        for (int i = 0; i < args.length; i++) {
            switch (args[i]) {
                case "--program" -> program = args[++i];
                case "--trace" -> trace = args[++i].toUpperCase();
                case "--out" -> out = args[++i];
                case "--parm" -> parm = args[++i];
                case "--max" -> max = Integer.parseInt(args[++i]);
                case "--probe-out" -> probeOut = args[++i];
                case "--probe-max" -> probeMax = Integer.parseInt(args[++i]);
                case "--dd" -> {
                    String[] kv = args[++i].split("=", 2);
                    CobFile.DD.put(kv[0].toUpperCase(), kv[1]);
                }
                case "--clock" -> {
                    String[] kv = args[++i].split("=", 2);
                    CobClock.configure(kv[0], kv.length > 1 ? kv[1] : "");
                }
                default -> throw new IllegalArgumentException("unknown argument " + args[i]);
            }
        }
        if (program == null || trace == null || out == null) throw new IllegalArgumentException("--program, --trace and --out are required");
        List<Call> calls = new ArrayList<>();
        List<Call> probeCalls = new ArrayList<>();
        final String target = trace;
        final int limit = Math.max(2, max);
        final int probeLimit = Math.max(2, probeMax);
        final boolean probing = probeOut != null;
        final long[] seen = {0};
        final long[] stride = {1};
        final long[] probeStride = {1};
        DisplaySink.set(new DisplaySink.ToStream(OutputStream.nullOutputStream()));
        CobRt.resetRunUnit();
        ProgramRegistry.reset();
        ProgramRegistry.registerModern(target, params -> {
            long n = ++seen[0];
            boolean keep = (n - 1) % stride[0] == 0;
            boolean probe = probing && (n - 1) % probeStride[0] == 0;
            if (!keep && !probe) {
                ProgramRegistry.callFaithful(target, params);
                return;
            }
            List<String> before = new ArrayList<>();
            for (byte[] p : params) before.add(new String(p, StandardCharsets.ISO_8859_1));
            ProgramRegistry.callFaithful(target, params);
            List<String> after = new ArrayList<>();
            for (byte[] p : params) after.add(new String(p, StandardCharsets.ISO_8859_1));
            Call call = new Call((int) n, before, after);
            if (keep) {
                calls.add(call);
                if (calls.size() > limit) thin(calls, stride);
            }
            if (probe) {
                probeCalls.add(call);
                if (probeCalls.size() > probeLimit) thin(probeCalls, probeStride);
            }
        });
        int rc;
        CobolBase main = ProgramRegistry.resolve(program);
        if (main == null) throw new IllegalArgumentException("program not found: " + program);
        try {
            CobField[] using = parm == null ? new CobField[0] : new CobField[]{JobEngine.parmArea(parm)};
            try {
                main.call(using, false);
            } catch (CobRt.StopRun s) {
                // end of run unit
            }
            CobFile.closeAllOpen();
            rc = CobRt.RETURN_CODE.dec().intValue();
        } catch (CobRt.Abend a) {
            CobFile.closeAllOpen(); // implicit CLOSE, as GnuCOBOL does when the CEE3ABD stub or a runtime error ends the run unit
            rc = 16;
        }
        Files.writeString(Path.of(out), json(program, target, rc, seen[0], calls), StandardCharsets.UTF_8);
        if (probing) Files.writeString(Path.of(probeOut), json(program, target, rc, seen[0], probeCalls), StandardCharsets.UTF_8);
        System.exit(0);
    }

    /** Keeps every other call and doubles the stride: the sample stays spread over the whole run. */
    static void thin(List<Call> calls, long[] stride) {
        List<Call> kept = new ArrayList<>();
        for (int i = 0; i < calls.size(); i += 2) kept.add(calls.get(i));
        calls.clear();
        calls.addAll(kept);
        stride[0] *= 2;
    }

    static String json(String program, String target, int rc, long total, List<Call> calls) {
        StringBuilder sb = new StringBuilder();
        sb.append("{\"schema\":\"mlcpu.call-trace.v1\",\"program\":").append(q(program)).append(",\"traced\":").append(q(target))
          .append(",\"returnCode\":").append(rc).append(",\"total\":").append(total).append(",\"calls\":[");
        for (int i = 0; i < calls.size(); i++) {
            Call c = calls.get(i);
            if (i > 0) sb.append(',');
            sb.append("{\"seq\":").append(c.seq()).append(",\"before\":").append(list(c.before())).append(",\"after\":").append(list(c.after())).append('}');
        }
        return sb.append("]}\n").toString();
    }

    static String list(List<String> l) {
        StringBuilder sb = new StringBuilder("[");
        for (int i = 0; i < l.size(); i++) {
            if (i > 0) sb.append(',');
            sb.append(q(l.get(i)));
        }
        return sb.append(']').toString();
    }

    static String q(String s) {
        StringBuilder sb = new StringBuilder("\"");
        for (char c : s.toCharArray()) {
            if (c == '"' || c == '\\') sb.append('\\').append(c);
            else if (c < 0x20 || c > 0x7E) sb.append(String.format("\\u%04x", (int) c));
            else sb.append(c);
        }
        return sb.append('"').toString();
    }
}
