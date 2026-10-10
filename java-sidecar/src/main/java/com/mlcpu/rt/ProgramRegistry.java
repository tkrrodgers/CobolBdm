package com.mlcpu.rt;

import java.util.HashMap;
import java.util.Map;

/**
 * CALL target resolution: normalized subprograms ({@link ModernProgram}) the job selected first, then registered
 * instances, then generated classes by naming convention. One instance per run unit.
 */
public final class ProgramRegistry {
    private ProgramRegistry() {}

    private static final Map<String, CobolBase> instances = new HashMap<>();
    private static final Map<String, CobolBase> faithful = new HashMap<>();
    private static final Map<String, ModernProgram> modern = new HashMap<>();
    private static ClassLoader loader;

    public static synchronized void register(String programId, CobolBase program) {
        instances.put(programId.toUpperCase(), program);
    }

    /** A normalized subprogram that answers CALLs to {@code programId} in this run unit. */
    public static synchronized void registerModern(String programId, ModernProgram program) {
        modern.put(programId.toUpperCase(), program);
    }

    public static synchronized ModernProgram modern(String programId) {
        return modern.get(programId.toUpperCase());
    }

    public static synchronized void useLoader(ClassLoader cl) {
        loader = cl;
    }

    public static synchronized void reset() {
        instances.clear();
        faithful.clear();
        modern.clear();
    }

    public static String className(String programId) {
        StringBuilder sb = new StringBuilder("Prog_");
        for (char c : programId.toUpperCase().toCharArray()) sb.append(Character.isLetterOrDigit(c) ? c : '_');
        return sb.toString();
    }

    public static synchronized CobolBase resolve(String programId) {
        String id = programId.toUpperCase();
        CobolBase p = instances.get(id);
        if (p != null) return p;
        p = load(id);
        if (p != null) instances.put(id, p);
        return p;
    }

    private static CobolBase load(String id) {
        ClassLoader cl = loader != null ? loader : Thread.currentThread().getContextClassLoader();
        for (String pkg : new String[]{"com.mlcpu.core.", "com.mlcpu.gen."}) {
            try {
                Class<?> c = Class.forName(pkg + className(id), true, cl);
                return (CobolBase) c.getDeclaredConstructor().newInstance();
            } catch (ClassNotFoundException e) {
                // try next package
            } catch (ReflectiveOperationException e) {
                throw new IllegalStateException("cannot instantiate " + id, e);
            }
        }
        return null;
    }

    /**
     * Calls a normalized subprogram: each argument's bytes are copied into an array, the program runs, and the arrays are
     * copied back into the caller's storage (BY REFERENCE).
     */
    public static void callModern(ModernProgram target, CobField[] args) {
        byte[][] params = new byte[args.length][];
        for (int i = 0; i < args.length; i++) params[i] = args[i] == null ? new byte[0] : args[i].bytes();
        try {
            target.call(params);
        } catch (ModernStep.Abend a) {
            throw new CobRt.Abend(a.code, a.getMessage());
        } catch (RuntimeException e) {
            throw e;
        } catch (Exception e) {
            throw new IllegalStateException("normalized subprogram failed: " + e, e);
        }
        for (int i = 0; i < args.length; i++) {
            if (args[i] == null) continue;
            System.arraycopy(params[i], 0, args[i].buf(), args[i].pos(), Math.min(params[i].length, args[i].len));
        }
    }

    /**
     * Runs the faithful {@code Prog_*} translation of a subprogram, bypassing any normalized registration (the bridge a
     * not-yet-modernized {@link ModernProgram} placeholder uses). Its WORKING-STORAGE persists across calls in the run unit.
     */
    public static void callFaithful(String programId, byte[][] parameters) {
        String id = programId.toUpperCase();
        CobolBase p;
        synchronized (ProgramRegistry.class) {
            p = faithful.get(id);
            if (p == null) {
                p = load(id);
                if (p == null) throw new CobRt.CallNotFound(id);
                faithful.put(id, p);
            }
        }
        CobField[] args = new CobField[parameters.length];
        for (int i = 0; i < parameters.length; i++) args[i] = CobField.temp(parameters[i]);
        p.call(args, true);
    }
}
