package com.mlcpu.rt;

import java.io.IOException;
import java.lang.reflect.Field;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.LinkOption;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.ArrayList;
import java.util.IdentityHashMap;
import java.util.List;
import java.util.Map;
import java.util.TreeMap;

/**
 * Frozen/generated batch shadowing. Both sides run on staged datasets in separate run units; only frozen outputs
 * are published. This is a dataset sandbox, not a sandbox for arbitrary Java or external service adapters.
 */
public final class StepStrangler {
    private static final ThreadLocal<Boolean> STAGED = ThreadLocal.withInitial(() -> false);
    private StepStrangler() {}

    static JobEngine.StepResult shadow(JobEngine engine, JobEngine.Step step) throws IOException {
        if (JobEngine.normalized(step))
            throw new JobEngine.JobFrontierException("SHADOW requires generated dataset adapters, not normalized/external-port implementations");
        if (System.getProperties().stringPropertyNames().stream().anyMatch(k -> k.startsWith("mlcpu.rule.")))
            throw new JobEngine.JobFrontierException("custom rule implementations require a separately certified shadow adapter");
        for (JobEngine.Dd dd : step.dds()) {
            if (dd.dsn() != null) safeRelative(dd.dsn());
            if (dd.member() != null) safeRelative(dd.member());
        }
        Path work = Files.createTempDirectory("mlcpu-shadow-");
        try {
            Path legacy = Files.createDirectories(work.resolve("legacy"));
            Path modern = Files.createDirectories(work.resolve("modern"));
            stage(engine, legacy);
            stage(engine, modern);
            List<java.time.OffsetDateTime> clocks = new ArrayList<>();
            CobClock.State seed = CobClock.capture();
            RunRecord a = execute(engine, step, legacy, "LEGACY", seed, clocks);
            RunRecord b = execute(engine, step, modern, "MODERN", seed, clocks);
            List<String> differences = compare(a, b);
            publish(legacy, engine);
            CobClock.advanceFrom(a.clock());
            Map<String, Object> report = new java.util.LinkedHashMap<>();
            report.put("schema", "mlcpu.step-shadow.v1");
            report.put("step", step.id());
            report.put("program", step.program());
            report.put("authoritative", "LEGACY");
            report.put("equivalent", differences.isEmpty());
            report.put("promoted", false);
            report.put("legacyReturnCode", a.result().returnCode());
            report.put("modernReturnCode", b.result().returnCode());
            report.put("legacyTranscriptHash", a.transcript());
            report.put("modernTranscriptHash", b.transcript());
            report.put("legacyStateHash", a.state());
            report.put("modernStateHash", b.state());
            report.put("differences", differences);
            Files.writeString(engine.sysoutDirectory().resolve(step.id() + ".shadow-report.json"), MiniJson.write(report));
            return new JobEngine.StepResult(step.id(), step.program(), a.result().returnCode(),
                    differences.isEmpty() ? "SHADOW MATCH; legacy authoritative" : "SHADOW DIFF; legacy retained");
        } finally {
            StepEquivalence.delete(work);
        }
    }

    private record RunRecord(JobEngine.StepResult result, Path directory, String transcript, String state, CobClock.State clock, int clockReads) {}

    private static RunRecord execute(JobEngine source, JobEngine.Step step, Path directory, String mode,
                                     CobClock.State seed, List<java.time.OffsetDateTime> clocks) throws IOException {
        try (RunUnit unit = new RunUnit(); RunUnit.Scope scope = unit.enter()) {
            boolean previous = STAGED.get();
            STAGED.set(true);
            try {
            JobEngine.STRANGLER = mode;
            seed.restore();
            int[] clockReads = {0};
            if (mode.equals("LEGACY")) CobClock.observe(clocks::add);
            else {
                CobClock.observe(null);
                CobClock.replay(() -> {
                    int position = clockReads[0]++;
                    if (position >= clocks.size()) throw new CobRt.RuntimeCheck("shadow requested an extra clock value");
                    return clocks.get(position);
                });
            }
            MessageDigest trace = digest();
            CobFile.TRACE = e -> {
                update(trace, e.op()); update(trace, e.file()); update(trace, e.dd());
                update(trace, Integer.toString(e.status())); update(trace, String.valueOf(e.advancing()));
                update(trace, e.record());
            };
            CobRt.DISPLAY_TRACE = line -> { update(trace, "DISPLAY"); update(trace, line); };
            JobEngine runner = new JobEngine(directory.resolve("data"), directory.resolve("sysout"),
                    directory.resolve("ctl"), source.programFactories(), line -> {});
            JobEngine.StepResult result = runner.run(List.of(step), java.util.Set.of()).get(0);
            return new RunRecord(result, directory, java.util.HexFormat.of().formatHex(trace.digest()), stateHash(),
                    CobClock.capture(), mode.equals("LEGACY") ? clocks.size() : clockReads[0]);
            } finally { STAGED.set(previous); }
        }
    }

    private static List<String> compare(RunRecord legacy, RunRecord modern) throws IOException {
        List<String> differences = new ArrayList<>();
        if (legacy.result().returnCode() != modern.result().returnCode()) differences.add("return code differs");
        if (legacy.clockReads() != modern.clockReads()) differences.add("clock interaction count differs");
        if (!legacy.transcript().equals(modern.transcript())) differences.add("ordered I/O/DISPLAY transcript differs");
        if (!legacy.state().equals(modern.state())) differences.add("COBOL storage image or alias topology differs");
        Map<String, Path> a = outputs(legacy.directory()), b = outputs(modern.directory());
        java.util.Set<String> names = new java.util.TreeSet<>(a.keySet());
        names.addAll(b.keySet());
        for (String name : names) {
            if (!a.containsKey(name) || !b.containsKey(name)) differences.add(name + ": creation/deletion differs");
            else {
                long offset = Files.mismatch(a.get(name), b.get(name));
                if (offset >= 0) differences.add(name + ": first differing byte " + offset);
            }
        }
        return differences;
    }

    private static String stateHash() {
        MessageDigest hash = digest();
        IdentityHashMap<byte[], Integer> buffers = new IdentityHashMap<>();
        for (var program : new TreeMap<>(ProgramRegistry.programs()).entrySet()) {
            Object storage = program.getValue();
            try {
                for (Class<?> c = storage.getClass(); c != null; c = c.getSuperclass())
                    for (Field f : c.getDeclaredFields())
                        if (f.getName().equals("_storage") && f.getType().getSimpleName().equals("Storage")) {
                            f.setAccessible(true);
                            storage = f.get(program.getValue());
                        }
                Map<String, Area> areas = new TreeMap<>();
                for (Class<?> c = storage.getClass(); c != null; c = c.getSuperclass())
                    for (Field f : c.getDeclaredFields())
                        if (f.getType() == Area.class && !java.lang.reflect.Modifier.isStatic(f.getModifiers())) {
                            f.setAccessible(true);
                            areas.put(f.getName(), (Area) f.get(storage));
                        }
                for (var entry : areas.entrySet()) {
                    Area area = entry.getValue();
                    update(hash, program.getKey() + "." + entry.getKey());
                    update(hash, area.size + ":" + area.base);
                    Integer id = buffers.get(area.b);
                    if (id == null) {
                        id = buffers.size();
                        buffers.put(area.b, id);
                        update(hash, area.b);
                    }
                    update(hash, Integer.toString(id));
                }
            } catch (IllegalAccessException e) {
                throw new IllegalStateException("cannot compare program storage: " + program.getKey(), e);
            }
        }
        update(hash, CobRt.RETURN_CODE.bytes());
        update(hash, CobRt.TALLY.bytes());
        update(hash, CobRt.SORT_RETURN.bytes());
        return java.util.HexFormat.of().formatHex(hash.digest());
    }

    private static void stage(JobEngine engine, Path directory) throws IOException {
        copyTree(engine.dataDirectory(), directory.resolve("data"));
        copyTree(engine.sysoutDirectory(), directory.resolve("sysout"));
        copyTree(engine.controlDirectory(), directory.resolve("ctl"));
    }

    private static void copyTree(Path from, Path to) throws IOException {
        Files.createDirectories(to);
        if (!Files.exists(from)) return;
        try (var paths = Files.walk(from)) {
            for (Path source : paths.toList()) {
                if (Files.isSymbolicLink(source)) throw new IOException("SHADOW does not follow symbolic links: " + source);
                Path target = to.resolve(from.relativize(source));
                if (Files.isDirectory(source, LinkOption.NOFOLLOW_LINKS)) Files.createDirectories(target);
                else Files.copy(source, target, StandardCopyOption.REPLACE_EXISTING);
            }
        }
    }

    private static Map<String, Path> outputs(Path directory) throws IOException {
        Map<String, Path> files = new TreeMap<>();
        for (String kind : List.of("data", "sysout")) {
            Path root = directory.resolve(kind);
            if (!Files.exists(root)) continue;
            try (var paths = Files.walk(root)) {
                for (Path file : paths.filter(Files::isRegularFile).toList())
                    files.put(kind + "/" + root.relativize(file).toString().replace('\\', '/'), file);
            }
        }
        return files;
    }

    private static void publish(Path baseline, JobEngine engine) throws IOException {
        for (String kind : List.of("data", "sysout")) {
            Path source = baseline.resolve(kind), destination = kind.equals("data") ? engine.dataDirectory() : engine.sysoutDirectory();
            Map<String, Path> current = new TreeMap<>(), wanted = new TreeMap<>();
            try (var paths = Files.walk(destination)) {
                for (Path file : paths.filter(Files::isRegularFile).toList()) current.put(destination.relativize(file).toString(), file);
            }
            try (var paths = Files.walk(source)) {
                for (Path file : paths.filter(Files::isRegularFile).toList()) wanted.put(source.relativize(file).toString(), file);
            }
            for (var entry : wanted.entrySet()) {
                Path target = destination.resolve(entry.getKey());
                if (Files.exists(target) && Files.mismatch(entry.getValue(), target) < 0) continue;
                Files.createDirectories(target.getParent());
                Path staged = Files.createTempFile(target.getParent(), ".shadow-", ".tmp");
                try {
                    Files.copy(entry.getValue(), staged, StandardCopyOption.REPLACE_EXISTING);
                    Files.move(staged, target, StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING);
                } finally { Files.deleteIfExists(staged); }
            }
            for (var entry : current.entrySet()) if (!wanted.containsKey(entry.getKey())) Files.delete(entry.getValue());
        }
    }

    private static void safeRelative(String name) {
        Path path = Path.of(name);
        if (path.isAbsolute() || path.normalize().startsWith(".."))
            throw new JobEngine.JobFrontierException("SHADOW cannot bind an external dataset path: " + name);
    }

    static void validateAssignment(String assign) {
        if (STAGED.get() && !CobFile.DD.containsKey(assign))
            throw new CobRt.RuntimeCheck("SHADOW requires an explicitly bound DD for " + assign);
    }

    private static MessageDigest digest() {
        try { return MessageDigest.getInstance("SHA-256"); }
        catch (NoSuchAlgorithmException e) { throw new IllegalStateException("SHA-256 unavailable", e); }
    }

    private static void update(MessageDigest hash, String value) { update(hash, value.getBytes(StandardCharsets.UTF_8)); }
    private static void update(MessageDigest hash, byte[] value) {
        hash.update(java.nio.ByteBuffer.allocate(4).putInt(value.length).array());
        hash.update(value);
    }
}
