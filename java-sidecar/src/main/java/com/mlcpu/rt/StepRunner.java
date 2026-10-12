package com.mlcpu.rt;

import java.io.FileOutputStream;
import java.io.IOException;
import java.io.OutputStream;
import java.math.BigDecimal;
import java.nio.charset.StandardCharsets;

/**
 * Runs one JCL step: binds DD names to paths, passes PARM as the z/OS halfword-length parameter area, writes
 * DISPLAY output to SYSOUT, and exits with RETURN-CODE (an abend exits with 16 after printing the abend line).
 *
 * <pre>StepRunner --program CBTRN02C --dd DALYTRAN=in.dat --dd TRANFILE=out.dat [--parm 2022071800] [--sysout f]</pre>
 */
public final class StepRunner {
    private StepRunner() {}

    public static void main(String[] args) throws IOException {
        System.exit(run(args));
    }

    public static int run(String[] args) throws IOException {
        String program = null, parm = null, sysout = null;
        for (int i = 0; i < args.length; i++) {
            switch (args[i]) {
                case "--program" -> program = args[++i];
                case "--parm" -> parm = args[++i];
                case "--sysout" -> sysout = args[++i];
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
        if (program == null) throw new IllegalArgumentException("--program is required");
        OutputStream os = sysout == null ? System.out : new FileOutputStream(sysout);
        DisplaySink.ToStream sink = new DisplaySink.ToStream(os);
        DisplaySink.set(sink);
        CobRt.resetRunUnit();
        int rc;
        try {
            CobolBase p = ProgramRegistry.resolve(program);
            if (p == null) throw new IllegalArgumentException("program not found: " + program);
            CobField[] using = new CobField[0];
            if (parm != null) {
                byte[] text = parm.getBytes(StandardCharsets.ISO_8859_1);
                byte[] area = new byte[2 + 100];
                java.util.Arrays.fill(area, (byte) ' ');
                area[0] = (byte) (text.length >> 8);
                area[1] = (byte) text.length;
                System.arraycopy(text, 0, area, 2, text.length);
                using = new CobField[]{CobField.temp(area)};
            }
            try {
                p.call(using, false);
            } catch (CobRt.StopRun s) {
                // normal end of the run unit
            }
            CobFile.closeAllOpen();
            rc = CobRt.RETURN_CODE.dec().intValue();
        } catch (CobRt.Abend a) {
            CobFile.closeAllOpen(); // implicit CLOSE, as GnuCOBOL does when the CEE3ABD stub or a runtime error ends the run unit
            sink.write(JobEngine.abendLine(a.code), true);
            rc = 16;
        } catch (CobRt.FrontierException | CobRt.RuntimeCheck | CobRt.LoopLimit | CobRt.CallNotFound e) {
            CobFile.closeAllOpen(); // GnuCOBOL closes open files when a runtime error ends the run unit
            sink.write("RUNTIME ERROR " + e.getMessage(), true);
            rc = 20;
        } finally {
            sink.flush();
            if (sysout != null) os.close();
        }
        return rc;
    }

    static BigDecimal rc() { return CobRt.RETURN_CODE.dec(); }
}
