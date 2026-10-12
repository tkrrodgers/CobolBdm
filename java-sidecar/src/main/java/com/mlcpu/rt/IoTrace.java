package com.mlcpu.rt;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;

/**
 * Runs one step's faithful program and records its observable effects in order: every READ (record and file status),
 * WRITE (with ADVANCING), REWRITE and DELETE, and every DISPLAY line — the order-of-effects trace a worked example shows.
 *
 * <pre>IoTrace --program SAM1 --dd CUSTFILE=in.dat ... [--parm P] [--clock FIXED=iso] --out io.json [--max 400]</pre>
 */
public final class IoTrace {
    private IoTrace() {}

    record Event(String op, String file, String dd, int status, String advancing, String data) {}

    public static void main(String[] args) throws IOException {
        String program = null, out = null, parm = null;
        int max = 400;
        for (int i = 0; i < args.length; i++) {
            switch (args[i]) {
                case "--program" -> program = args[++i];
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
        if (program == null || out == null) throw new IllegalArgumentException("--program and --out are required");
        List<Event> events = new ArrayList<>();
        final int limit = max;
        int[] total = {0};
        CobFile.TRACE = e -> {
            total[0]++;
            if (events.size() < limit)
                events.add(new Event(e.op(), e.file(), e.dd(), e.status(), e.advancing(), new String(e.record(), StandardCharsets.ISO_8859_1)));
        };
        DisplaySink.set(new DisplaySink() {
            private final StringBuilder pending = new StringBuilder();
            @Override
            public void write(String text, boolean newline) {
                pending.append(text);
                if (!newline) return;
                total[0]++;
                if (events.size() < limit) events.add(new Event("DISPLAY", null, "SYSOUT", 0, null, pending.toString()));
                pending.setLength(0);
            }
        });
        CobRt.resetRunUnit();
        ProgramRegistry.reset();
        CobolBase main = ProgramRegistry.resolve(program);
        if (main == null) throw new IllegalArgumentException("program not found: " + program);
        int rc;
        String outcome = "normal end";
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
            outcome = "abend " + a.code;
        } finally {
            CobFile.TRACE = null;
        }
        StringBuilder sb = new StringBuilder();
        sb.append("{\"schema\":\"mlcpu.io-trace.v1\",\"program\":").append(CallTrace.q(program)).append(",\"returnCode\":").append(rc)
          .append(",\"outcome\":").append(CallTrace.q(outcome)).append(",\"events\":").append(total[0]).append(",\"trace\":[");
        for (int i = 0; i < events.size(); i++) {
            Event e = events.get(i);
            if (i > 0) sb.append(',');
            sb.append("{\"op\":").append(CallTrace.q(e.op())).append(",\"file\":").append(e.file() == null ? "null" : CallTrace.q(e.file()))
              .append(",\"dd\":").append(CallTrace.q(e.dd())).append(",\"status\":").append(e.status())
              .append(",\"advancing\":").append(e.advancing() == null ? "null" : CallTrace.q(e.advancing()))
              .append(",\"data\":").append(CallTrace.q(e.data())).append('}');
        }
        sb.append("]}\n");
        Files.writeString(Path.of(out), sb.toString(), StandardCharsets.UTF_8);
        System.exit(0);
    }
}
