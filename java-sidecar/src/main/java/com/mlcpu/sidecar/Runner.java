package com.mlcpu.sidecar;

import com.mlcpu.rt.CobRt;
import com.mlcpu.rt.CobolBase;
import com.mlcpu.rt.ProgramRegistry;
import com.mlcpu.rt.ProgramState;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;

/** Runs a compiled program in an isolated class loader with a time budget (I10). */
public final class Runner {
    public static final int RUN_TIMEOUT_SECONDS = 10;

    public static final class Outcome {
        public boolean ran;
        public String error;
        public String abend;
        public ProgramState state;
    }

    public Outcome run(Map<String, byte[]> classes, String mainClass, Map<String, Object> inputs) {
        Outcome o = new Outcome();
        ExecutorService ex = Executors.newSingleThreadExecutor(r -> {
            Thread t = new Thread(null, r, "cobol-run", 256L << 20);
            t.setDaemon(true);
            return t;
        });
        try {
            ClassLoader cl = new JavaCompilerService.MemoryLoader(classes, Runner.class.getClassLoader());
            Future<ProgramState> f = ex.submit(() -> {
                synchronized (Runner.class) {
                    ProgramRegistry.reset();
                    ProgramRegistry.useLoader(cl);
                    CobRt.resetRunUnit();
                    CobolBase p = (CobolBase) Class.forName(mainClass, true, cl).getDeclaredConstructor().newInstance();
                    ProgramRegistry.register(p.programId(), p);
                    ProgramState in = new ProgramState();
                    if (inputs != null) inputs.forEach(in::set);
                    try {
                        return p.run(in);
                    } finally {
                        ProgramRegistry.useLoader(null);
                    }
                }
            });
            o.state = f.get(RUN_TIMEOUT_SECONDS, TimeUnit.SECONDS);
            o.ran = true;
        } catch (java.util.concurrent.TimeoutException e) {
            o.error = "run exceeded " + RUN_TIMEOUT_SECONDS + "s";
        } catch (java.util.concurrent.ExecutionException e) {
            Throwable c = e.getCause();
            if (c instanceof CobRt.Abend a) {
                o.ran = true;
                o.abend = a.getMessage();
            } else o.error = c.getClass().getSimpleName() + ": " + c.getMessage();
        } catch (Exception e) {
            o.error = e.toString();
        } finally {
            ex.shutdownNow();
        }
        return o;
    }
}
