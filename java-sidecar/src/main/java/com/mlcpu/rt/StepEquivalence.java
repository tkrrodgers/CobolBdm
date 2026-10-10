package com.mlcpu.rt;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.TreeMap;
import java.util.stream.Stream;

/**
 * Runs one job step twice over identical inputs, once with the faithful program ("legacy") and once with its normalized
 * implementation ("normalized"), and reports every byte difference in the datasets and SYSOUT the step leaves behind.
 * Inputs are dataset images keyed by DSN (the data directory's file names). Not thread safe: implementation selection
 * and the COBOL run unit are process-wide.
 */
public final class StepEquivalence {
    private StepEquivalence() {}

    /** Outcome of one run: return code, step detail, and every file left behind keyed by "data/DSN" or "sysout/NAME". */
    public record Outcome(int returnCode, String detail, Map<String, byte[]> files) {}

    /** The GnuCOBOL oracle's environment: pinned clock, numeric DISPLAY as digits plus sign, full binary capacity, x'00' storage. */
    public static void pinOracleEnvironment(String isoEpoch) {
        CobClock.configure("FIXED", isoEpoch);
        CobConfig.DISPLAY_NUMERIC = "gnucobol";
        CobConfig.BINARY_TRUNCATE = false;
        CobConfig.DEFAULT_BYTE = 0;
    }

    /** Differences between an outcome and oracle files (SYSOUT compared by line; the oracle wrote CRLF on Windows). */
    public static List<String> againstOracle(Outcome actual, Map<String, byte[]> expected) {
        List<String> out = new ArrayList<>();
        for (var e : expected.entrySet()) {
            byte[] a = actual.files().get(e.getKey()), b = e.getValue();
            if (a == null) { out.add(e.getKey() + ": not written"); continue; }
            if (e.getKey().startsWith("sysout/")) {
                a = new String(a, java.nio.charset.StandardCharsets.ISO_8859_1).replace("\r\n", "\n").getBytes(java.nio.charset.StandardCharsets.ISO_8859_1);
                b = new String(b, java.nio.charset.StandardCharsets.ISO_8859_1).replace("\r\n", "\n").getBytes(java.nio.charset.StandardCharsets.ISO_8859_1);
            }
            int i = java.util.Arrays.mismatch(a, b);
            if (i >= 0) out.add(e.getKey() + ": first difference at byte " + i + " (actual " + a.length + " / oracle " + b.length + ") actual «"
                    + excerpt(a, i) + "» oracle «" + excerpt(b, i) + "»");
        }
        return out;
    }

    public static synchronized Outcome run(JobEngine.Step step, Map<String, byte[]> inputs, String implementation) throws IOException {
        return run(step, inputs, implementation, Map.of());
    }

    /**
     * Runs the step with {@code implementation}; {@code subprograms} are the normalized CALL targets in play (only these:
     * any registered elsewhere are set aside for the run, so each capability is judged on its own).
     */
    public static synchronized Outcome run(JobEngine.Step step, Map<String, byte[]> inputs, String implementation,
                                           Map<String, java.util.function.Supplier<ModernProgram>> subprograms) throws IOException {
        Map<String, java.util.function.Supplier<ModernProgram>> savedCalls = new TreeMap<>(JobEngine.MODERN_PROGRAMS);
        JobEngine.MODERN_PROGRAMS.clear();
        subprograms.forEach((k, v) -> JobEngine.MODERN_PROGRAMS.put(k.toUpperCase(), v));
        try {
            return runOnce(step, inputs, implementation);
        } finally {
            JobEngine.MODERN_PROGRAMS.clear();
            JobEngine.MODERN_PROGRAMS.putAll(savedCalls);
        }
    }

    private static Outcome runOnce(JobEngine.Step step, Map<String, byte[]> inputs, String implementation) throws IOException {
        Path dir = Files.createTempDirectory("mlcpu-eq-" + implementation + "-");
        try {
            Path data = Files.createDirectories(dir.resolve("data"));
            Path sysout = dir.resolve("sysout");
            for (var e : inputs.entrySet()) Files.write(data.resolve(e.getKey()), e.getValue());
            String previous = JobEngine.IMPLEMENTATION;
            Map<String, String> byProgram = new TreeMap<>(JobEngine.IMPLEMENTATION_BY_PROGRAM);
            JobEngine.IMPLEMENTATION_BY_PROGRAM.clear();
            JobEngine.IMPLEMENTATION = implementation;
            JobEngine.StepResult r;
            try {
                r = new JobEngine(data, sysout, dir.resolve("ctl"), Map.of(), line -> {}).run(List.of(step), java.util.Set.of()).get(0);
            } finally {
                JobEngine.IMPLEMENTATION = previous;
                JobEngine.IMPLEMENTATION_BY_PROGRAM.putAll(byProgram);
            }
            Map<String, byte[]> files = new TreeMap<>();
            for (Path root : List.of(data, sysout)) {
                if (!Files.isDirectory(root)) continue;
                try (Stream<Path> s = Files.list(root)) {
                    for (Path p : s.toList()) files.put(root.getFileName() + "/" + p.getFileName(), Files.readAllBytes(p));
                }
            }
            return new Outcome(r.returnCode(), r.detail(), files);
        } finally {
            delete(dir);
        }
    }

    /** Differences between the faithful and the normalized outcome; empty when they are byte-identical. */
    public static List<String> differences(Outcome legacy, Outcome normalized) {
        List<String> out = new ArrayList<>();
        if (legacy.returnCode() != normalized.returnCode())
            out.add("return code: legacy " + legacy.returnCode() + " (" + legacy.detail() + "), normalized " + normalized.returnCode() + " (" + normalized.detail() + ")");
        var names = new java.util.TreeSet<>(legacy.files().keySet());
        names.addAll(normalized.files().keySet());
        for (String n : names) {
            byte[] a = legacy.files().get(n), b = normalized.files().get(n);
            if (a == null || b == null) {
                out.add(n + ": only the " + (a == null ? "normalized" : "legacy") + " implementation wrote it");
                continue;
            }
            int i = java.util.Arrays.mismatch(a, b);
            if (i < 0) continue;
            out.add(n + ": first difference at byte " + i + " (legacy " + a.length + " / normalized " + b.length + " bytes) legacy «"
                    + excerpt(a, i) + "» normalized «" + excerpt(b, i) + "»");
        }
        return out;
    }

    /** Runs both implementations and returns the differences (empty = equivalent). */
    public static List<String> compare(JobEngine.Step step, Map<String, byte[]> inputs) throws IOException {
        return differences(run(step, inputs, "legacy"), run(step, inputs, "normalized"));
    }

    /**
     * A CALLed subprogram's capability: runs the step (its main program faithful both times) once with the faithful
     * subprogram and once with {@code modern} answering every CALL to {@code program}; returns the differences.
     */
    public static List<String> compareCall(JobEngine.Step step, String program, java.util.function.Supplier<ModernProgram> modern,
                                           Map<String, byte[]> inputs) throws IOException {
        JobEngine.Step faithfulMain = new JobEngine.Step(step.id(), step.program(), step.parm(), step.cond(), step.factory(), null, step.dds());
        return differences(run(faithfulMain, inputs, "legacy"), run(faithfulMain, inputs, "normalized", Map.of(program, modern)));
    }

    /** Dataset images from the classpath, next to {@code anchor}: {@code dir/name}, keyed by {@code prefix + name}. */
    public static Map<String, byte[]> resources(Class<?> anchor, String dir, String prefix, String... names) throws IOException {
        Map<String, byte[]> out = new TreeMap<>();
        for (String n : names) {
            try (var in = anchor.getResourceAsStream(dir + "/" + n)) {
                if (in == null) throw new IOException("missing test resource " + dir + "/" + n + " next to " + anchor.getName());
                out.put(prefix + n, in.readAllBytes());
            }
        }
        return out;
    }

    /** Like {@link #resources} but names without a resource are skipped (a dataset the scenario does not have). */
    public static Map<String, byte[]> optionalResources(Class<?> anchor, String dir, String prefix, String... names) throws IOException {
        Map<String, byte[]> out = new TreeMap<>();
        for (String n : names) {
            try (var in = anchor.getResourceAsStream(dir + "/" + n)) {
                if (in != null) out.put(prefix + n, in.readAllBytes());
            }
        }
        return out;
    }

    /** The records of a dataset image: fixed-length {@code lrecl}, or (lrecl 0) variable with 4-byte record descriptors. */
    public static List<byte[]> records(byte[] file, int lrecl) {
        List<byte[]> out = new ArrayList<>();
        if (lrecl > 0) {
            for (int i = 0; i * lrecl < file.length; i++)
                out.add(java.util.Arrays.copyOfRange(file, i * lrecl, Math.min(file.length, (i + 1) * lrecl)));
            return out;
        }
        int at = 0;
        while (at + 4 <= file.length) {
            int len = ((file[at] & 0xFF) << 8) | (file[at + 1] & 0xFF);
            out.add(java.util.Arrays.copyOfRange(file, at + 4, Math.min(file.length, at + 4 + len)));
            at += 4 + len;
        }
        return out;
    }

    /** The inverse of {@link #records}. */
    public static byte[] join(List<byte[]> records, int lrecl) {
        java.io.ByteArrayOutputStream out = new java.io.ByteArrayOutputStream();
        for (byte[] r : records) {
            if (lrecl == 0) {
                out.write((r.length >> 8) & 0xFF);
                out.write(r.length & 0xFF);
                out.write(0);
                out.write(0);
            }
            out.writeBytes(r);
        }
        return out.toByteArray();
    }

    /** The first {@code n} records. */
    public static byte[] firstRecords(byte[] file, int lrecl, int n) {
        List<byte[]> r = records(file, lrecl);
        return join(r.subList(0, Math.min(n, r.size())), lrecl);
    }

    /** The records in reverse order. */
    public static byte[] reverseRecords(byte[] file, int lrecl) {
        List<byte[]> r = new ArrayList<>(records(file, lrecl));
        java.util.Collections.reverse(r);
        return join(r, lrecl);
    }

    /** Every record twice, in order (duplicates). */
    public static byte[] duplicateRecords(byte[] file, int lrecl) {
        List<byte[]> r = new ArrayList<>();
        for (byte[] x : records(file, lrecl)) { r.add(x); r.add(x); }
        return join(r, lrecl);
    }

    /** Edits a fixed-length record file: the function gets (index, copy of the record) and returns the record to keep, or null to drop it. */
    public static byte[] editRecords(byte[] file, int lrecl, java.util.function.BiFunction<Integer, byte[], byte[]> edit) {
        java.io.ByteArrayOutputStream out = new java.io.ByteArrayOutputStream(file.length);
        for (int i = 0; i * lrecl < file.length; i++) {
            byte[] r = java.util.Arrays.copyOfRange(file, i * lrecl, Math.min(file.length, (i + 1) * lrecl));
            byte[] kept = edit.apply(i, r);
            if (kept != null) out.writeBytes(kept);
        }
        return out.toByteArray();
    }

    static String excerpt(byte[] b, int at) {
        int from = Math.max(0, at - 20), to = Math.min(b.length, at + 40);
        StringBuilder sb = new StringBuilder();
        for (int i = from; i < to; i++) {
            int c = b[i] & 0xFF;
            sb.append(c >= 0x20 && c < 0x7F ? (char) c : c == '\n' ? "\\n" : String.format("\\x%02x", c));
        }
        return sb.toString();
    }

    static void delete(Path dir) throws IOException {
        try (Stream<Path> s = Files.walk(dir)) {
            for (Path p : s.sorted(java.util.Comparator.reverseOrder()).toList()) Files.deleteIfExists(p);
        }
    }
}
