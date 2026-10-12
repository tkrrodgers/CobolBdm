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
        targets.clear();
        instances.put(programId.toUpperCase(), program);
    }

    /** A normalized subprogram that answers CALLs to {@code programId} in this run unit. */
    public static synchronized void registerModern(String programId, ModernProgram program) {
        targets.clear();
        modern.put(programId.toUpperCase(), program);
    }

    public static synchronized ModernProgram modern(String programId) {
        return modern.get(programId.toUpperCase());
    }

    public static synchronized void useLoader(ClassLoader cl) {
        targets.clear();
        loader = cl;
    }

    /** A CALL target by the name as written: normalized and resolved once per registry state. */
    record Target(String id, ModernProgram modern, CobolBase program) {}

    private static final Map<String, Target> targets = new java.util.concurrent.ConcurrentHashMap<>();

    /** The CALL target for a name as written; read without a lock once resolved. */
    static Target target(String name) {
        Target t = targets.get(name);
        return t != null ? t : targetSlow(name);
    }

    private static synchronized Target targetSlow(String name) {
        Target t = targets.get(name);
        if (t != null) return t;
        String id = name.trim().toUpperCase();
        ModernProgram m = modern.get(id);
        t = new Target(id, m, m != null ? null : resolve(id));
        if (t.program() != null || t.modern() != null) targets.put(name, t);
        return t;
    }

    public static synchronized void reset() {
        targets.clear();
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
     * Calls a normalized subprogram BY REFERENCE. An argument that is a whole storage array (a level-01 record passed as
     * such: offset 0, the array's length) is passed as that array, so the callee reads and writes the caller's bytes
     * directly; any other argument is copied into an array and copied back after the call. The J3S estate passes its
     * 477 MB heap record in every CALL: copying it twice per CALL would never finish.
     */
    public static void callModern(ModernProgram target, CobField[] args) {
        byte[][] params = new byte[args.length][];
        boolean[] shared = new boolean[args.length];
        for (int i = 0; i < args.length; i++) {
            CobField a = args[i];
            if (a == null) params[i] = new byte[0];
            else if (a.pos() == 0 && a.len == a.buf().length) { params[i] = a.buf(); shared[i] = true; }
            else params[i] = a.bytes();
        }
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
            if (args[i] == null || shared[i]) continue;
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
