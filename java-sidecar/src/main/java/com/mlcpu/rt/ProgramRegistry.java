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
    private static final Map<String, java.util.function.Supplier<ModernProgram>> modernFactories = new HashMap<>();
    private static ClassLoader loader;
    private static boolean baselineNames;

    public static void register(String programId, CobolBase program) {
        try (RunUnit.Guard guard = RunUnit.guard()) {
            targets.clear();
            instances.put(programId.toUpperCase(java.util.Locale.ROOT), java.util.Objects.requireNonNull(program));
        }
    }

    /** A normalized subprogram that answers CALLs to {@code programId} in this run unit. */
    public static void registerModern(String programId, ModernProgram program) {
        try (RunUnit.Guard guard = RunUnit.guard()) {
            targets.clear();
            String id = programId.toUpperCase(java.util.Locale.ROOT);
            modernFactories.remove(id);
            modern.put(id, java.util.Objects.requireNonNull(program));
        }
    }

    public static void registerModernFactory(String programId, java.util.function.Supplier<ModernProgram> factory) {
        try (RunUnit.Guard guard = RunUnit.guard()) {
            String id = programId.toUpperCase(java.util.Locale.ROOT);
            modern.remove(id);
            modernFactories.put(id, java.util.Objects.requireNonNull(factory));
            targets.clear();
        }
    }

    public static ModernProgram modern(String programId) {
        try (RunUnit.Guard guard = RunUnit.guard()) {
            String id = programId.toUpperCase(java.util.Locale.ROOT);
            ModernProgram result = modern.get(id);
            if (result == null && modernFactories.containsKey(id)) {
                result = java.util.Objects.requireNonNull(modernFactories.get(id).get(), "modern program factory returned null: " + id);
                modern.put(id, result);
            }
            return result;
        }
    }

    public static void useLoader(ClassLoader cl) {
        try (RunUnit.Guard guard = RunUnit.guard()) {
            targets.clear();
            loader = cl;
        }
    }

    static void useBaselineNames(boolean value) { baselineNames = value; targets.clear(); }
    static Map<String, CobolBase> programs() { return Map.copyOf(instances); }

    /** A CALL target by the name as written: normalized and resolved once per registry state. */
    record Target(String id, ModernProgram modern, CobolBase program) {}

    private static final Map<String, Target> targets = new java.util.concurrent.ConcurrentHashMap<>();

    /** The CALL target for a name as written; read without a lock once resolved. */
    static Target target(String name) {
        Target t = targets.get(name);
        return t != null ? t : targetSlow(name);
    }

    private static Target targetSlow(String name) {
        Target t = targets.get(name);
        if (t != null) return t;
        String id = name.trim().toUpperCase(java.util.Locale.ROOT);
        ModernProgram m = modern(id);
        t = new Target(id, m, m != null ? null : resolve(id));
        if (t.program() != null || t.modern() != null) targets.put(name, t);
        return t;
    }

    public static void reset() {
        try (RunUnit.Guard guard = RunUnit.guard()) {
            targets.clear();
            instances.clear();
            faithful.clear();
            modern.clear();
            modernFactories.clear();
        }
    }

    public static String className(String programId) {
        StringBuilder sb = new StringBuilder("Prog_");
        for (char c : programId.toUpperCase().toCharArray()) sb.append(Character.isLetterOrDigit(c) ? c : '_');
        return sb.toString();
    }

    public static CobolBase resolve(String programId) {
        try (RunUnit.Guard guard = RunUnit.guard()) {
            String id = programId.toUpperCase(java.util.Locale.ROOT);
            CobolBase p = instances.get(id);
            if (p != null) return p;
            p = load(id);
            if (p != null) instances.put(id, p);
            return p;
        }
    }

    private static CobolBase load(String id) {
        ClassLoader cl = loader != null ? loader : Thread.currentThread().getContextClassLoader();
        for (String pkg : new String[]{"com.mlcpu.core.", "com.mlcpu.gen."}) {
            try {
                String name = baselineNames ? className(id).replace("Prog_", "Baseline_") : className(id);
                Class<?> c = Class.forName(pkg + name, true, cl);
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
        try (RunUnit.Guard guard = RunUnit.guard()) {
            callModernBound(target, args);
        }
    }

    private static void callModernBound(ModernProgram target, CobField[] args) {
        if (target instanceof ModernProgramViews views) {
            try {
                views.callViews(CallArgument.snapshot(args));
            } catch (ModernStep.Abend a) {
                throw new CobRt.Abend(a.code, a.getMessage());
            } catch (RuntimeException e) {
                throw e;
            } catch (Exception e) {
                throw new IllegalStateException("normalized subprogram failed", e);
            }
            return;
        }
        for (int i = 0; i < args.length; i++)
            for (int j = 0; j < i; j++) {
                CobField a = args[i], b = args[j];
                if (a == null || b == null || a.buf() != b.buf()) continue;
                boolean overlap = a.pos() < (long) b.pos() + b.len && b.pos() < (long) a.pos() + a.len;
                boolean whole = a.pos() == 0 && a.len == a.buf().length && b.pos() == 0 && b.len == b.buf().length;
                if (overlap && !whole)
                    throw new CobRt.RuntimeCheck("overlapping CALL arguments require ModernProgramViews; byte[][] cannot preserve their aliasing");
            }
        byte[][] params = new byte[args.length][];
        boolean[] shared = new boolean[args.length];
        for (int i = 0; i < args.length; i++) {
            CobField a = args[i];
            if (a == null) params[i] = new byte[0];
            else if (a.pos() == 0 && a.len == a.buf().length) { params[i] = a.buf(); shared[i] = true; }
            else params[i] = a.bytes();
        }
        RuntimeException failure = null;
        try {
            target.call(params);
        } catch (ModernStep.Abend a) {
            failure = new CobRt.Abend(a.code, a.getMessage());
        } catch (RuntimeException e) {
            failure = e;
        } catch (Exception e) {
            failure = new IllegalStateException("normalized subprogram failed: " + e, e);
        }
        for (int i = 0; i < args.length; i++) {
            if (args[i] == null || shared[i]) continue;
            if (params[i] == null || params[i].length != args[i].len) {
                CobRt.RuntimeCheck invalid = new CobRt.RuntimeCheck("normalized CALL changed argument length at position " + (i + 1));
                if (failure == null) failure = invalid; else failure.addSuppressed(invalid);
                continue;
            }
            System.arraycopy(params[i], 0, args[i].buf(), args[i].pos(), args[i].len);
        }
        if (failure != null) throw failure;
    }

    /**
     * Runs the faithful {@code Prog_*} translation of a subprogram, bypassing any normalized registration (the bridge a
     * not-yet-modernized {@link ModernProgram} placeholder uses). Its WORKING-STORAGE persists across calls in the run unit.
     */
    public static void callFaithful(String programId, byte[][] parameters) {
        String id = programId.toUpperCase();
        CobolBase p;
        try (RunUnit.Guard guard = RunUnit.guard()) {
            p = faithful.get(id);
            if (p == null) {
                p = load(id);
                if (p == null) throw new CobRt.CallNotFound(id);
                faithful.put(id, p);
            }
            CobField[] args = new CobField[parameters.length];
            for (int i = 0; i < parameters.length; i++) args[i] = CobField.temp(parameters[i]);
            p.call(args, true);
        }
    }

    public static void cancel(String name) {
        try (RunUnit.Guard guard = RunUnit.guard()) {
            String id = name.trim().toUpperCase(java.util.Locale.ROOT);
            if (modern.containsKey(id) || modernFactories.containsKey(id)) {
                if (!modernFactories.containsKey(id))
                    throw new CobRt.RuntimeCheck("CANCEL of a modern program requires a resettable factory: " + id);
                modern.remove(id);
            }
            CobolBase program = instances.get(id);
            if (program != null) program.cancel();
            CobolBase baseline = faithful.get(id);
            if (baseline != null && baseline != program) baseline.cancel();
            targets.clear();
        }
    }

    record State(Map<String, CobolBase> programs, Map<String, CobolBase> baseline,
                 Map<String, ModernProgram> normalized, Map<String, java.util.function.Supplier<ModernProgram>> factories,
                 Map<String, Target> resolved, ClassLoader classLoader, boolean frozenNames) {
        void restore() {
            instances.clear(); instances.putAll(programs);
            faithful.clear(); faithful.putAll(baseline);
            modern.clear(); modern.putAll(normalized);
            modernFactories.clear(); modernFactories.putAll(factories);
            targets.clear(); targets.putAll(resolved);
            loader = classLoader;
            baselineNames = frozenNames;
        }
    }
    static State capture() {
        return new State(new HashMap<>(instances), new HashMap<>(faithful), new HashMap<>(modern), new HashMap<>(modernFactories), new HashMap<>(targets), loader, baselineNames);
    }
    static State fresh() { return new State(Map.of(), Map.of(), Map.of(), Map.of(), Map.of(), loader, false); }
}
