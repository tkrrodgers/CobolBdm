package com.mlcpu.rt;

import java.io.IOException;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;

/**
 * Runs one step's faithful program and records every CALL to a subprogram: each argument's bytes before and after the
 * call (the worked examples of a subprogram capability). Arguments are recorded as ISO-8859-1 strings.
 *
 * <pre>CallTrace --program SAM1 --trace SAM2 --dd CUSTFILE=in.dat ... [--parm P] [--clock FIXED=iso] --out trace.json [--max 50]</pre>
 */
public final class CallTrace {
    private CallTrace() {}

    record Call(int seq, List<String> before, List<String> after) {}

    public static void main(String[] args) throws IOException {
        String program = null, trace = null, out = null, parm = null;
        int max = 50;
        for (int i = 0; i < args.length; i++) {
            switch (args[i]) {
                case "--program" -> program = args[++i];
                case "--trace" -> trace = args[++i].toUpperCase();
                case "--out" -> out = args[++i];
                case "--parm" -> parm = args[++i];
                case "--max" -> max = Integer.parseInt(args[++i]);
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
        final String target = trace;
        final int limit = max;
        DisplaySink.set(new DisplaySink.ToStream(OutputStream.nullOutputStream()));
        CobRt.resetRunUnit();
        ProgramRegistry.reset();
        ProgramRegistry.registerModern(target, params -> {
            List<String> before = new ArrayList<>();
            for (byte[] p : params) before.add(new String(p, StandardCharsets.ISO_8859_1));
            ProgramRegistry.callFaithful(target, params);
            List<String> after = new ArrayList<>();
            for (byte[] p : params) after.add(new String(p, StandardCharsets.ISO_8859_1));
            if (calls.size() < limit) calls.add(new Call(calls.size() + 1, before, after));
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
            CobFile.forgetOpen();
            rc = 16;
        }
        StringBuilder sb = new StringBuilder();
        sb.append("{\"schema\":\"mlcpu.call-trace.v1\",\"program\":").append(q(program)).append(",\"traced\":").append(q(target))
          .append(",\"returnCode\":").append(rc).append(",\"calls\":[");
        for (int i = 0; i < calls.size(); i++) {
            Call c = calls.get(i);
            if (i > 0) sb.append(',');
            sb.append("{\"seq\":").append(c.seq()).append(",\"before\":").append(list(c.before())).append(",\"after\":").append(list(c.after())).append('}');
        }
        sb.append("]}\n");
        Files.writeString(Path.of(out), sb.toString(), StandardCharsets.UTF_8);
        System.exit(0);
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
