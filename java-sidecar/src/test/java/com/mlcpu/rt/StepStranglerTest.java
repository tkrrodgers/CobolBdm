package com.mlcpu.rt;

import static org.junit.jupiter.api.Assertions.*;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import java.util.Map;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

class StepStranglerTest {
    @TempDir Path temporary;

    @Test
    void shadowReplaysWallClockAndPublishesOnlyTheFrozenSideEvenOnDrift() throws Exception {
        for (String value : List.of("BASE", "MUT!")) {
            Path directory = temporary.resolve(value);
            JobEngine.Step step = step(() -> new Writer(value, false), () -> new Writer("BASE", false));
            JobEngine.State saved = JobEngine.capture();
            CobClock.State savedClock = CobClock.capture();
            try {
                JobEngine.STRANGLER = "SHADOW";
                CobClock.configure("WALL", null);
                List<JobEngine.StepResult> result = new JobEngine(directory.resolve("data"), directory.resolve("sysout"),
                        directory.resolve("ctl"), Map.of(), line -> {}).run(List.of(step), java.util.Set.of());
                assertEquals(0, result.get(0).returnCode());
                byte[] output = Files.readAllBytes(directory.resolve("data").resolve("OUT.dat"));
                assertEquals(25, output.length);
                assertEquals("BASE", new String(output, 0, 4, java.nio.charset.StandardCharsets.ISO_8859_1));
                Object parsed = MiniJson.parse(Files.readString(directory.resolve("sysout/S.shadow-report.json")));
                Map<?, ?> report = assertInstanceOf(Map.class, parsed);
                assertEquals(value.equals("BASE"), report.get("equivalent"));
                assertEquals("LEGACY", report.get("authoritative"));
                assertEquals(false, report.get("promoted"));
                if (!value.equals("BASE")) assertFalse(((List<?>) report.get("differences")).isEmpty());
            } finally {
                saved.restore();
                savedClock.restore();
            }
        }
    }

    @Test
    void extraClockReadsAreReportedAndCannotChangeTheAuthoritativeFiles() throws Exception {
        JobEngine.State saved = JobEngine.capture();
        try {
            JobEngine.STRANGLER = "SHADOW";
            var step = step(() -> new Writer("BASE", true), () -> new Writer("BASE", false));
            new JobEngine(temporary.resolve("data"), temporary.resolve("sysout"), temporary.resolve("ctl"), Map.of(), line -> {})
                    .run(List.of(step), java.util.Set.of());
            Map<?, ?> report = assertInstanceOf(Map.class, MiniJson.parse(Files.readString(temporary.resolve("sysout/S.shadow-report.json"))));
            assertEquals(false, report.get("equivalent"));
            assertTrue(((List<?>) report.get("differences")).contains("clock interaction count differs"));
            assertEquals("BASE", Files.readString(temporary.resolve("data/OUT.dat")).substring(0, 4));
        } finally { saved.restore(); }
    }

    @Test
    void missingBaselineAndUnboundOrExternalDatasetsFailClosed() throws Exception {
        JobEngine.State saved = JobEngine.capture();
        try {
            JobEngine.STRANGLER = "SHADOW";
            JobEngine engine = new JobEngine(temporary.resolve("data"), temporary.resolve("sysout"), temporary.resolve("ctl"), Map.of(), line -> {});
            JobEngine.Step missing = new JobEngine.Step("S", "WRITE", null, null, () -> new Writer("BASE", false), List.of());
            assertEquals(12, engine.run(List.of(missing), java.util.Set.of()).get(0).returnCode());
            JobEngine.Step escaped = new JobEngine.Step("S", "WRITE", null, null, () -> new Writer("BASE", false), null,
                    List.of(new JobEngine.Dd("OUT", "..\\escape.dat", null, null, false, false, null, 25)), () -> new Writer("BASE", false));
            assertEquals(12, engine.run(List.of(escaped), java.util.Set.of()).get(0).returnCode());
            assertFalse(Files.exists(temporary.resolve("escape.dat")));
        } finally { saved.restore(); }
    }

    private static JobEngine.Step step(java.util.function.Supplier<CobolBase> factory, java.util.function.Supplier<CobolBase> baseline) {
        return new JobEngine.Step("S", "WRITE", null, null, factory, null,
                List.of(new JobEngine.Dd("OUT", "OUT.dat", null, null, false, false, null, 25)), baseline);
    }

    private static final class Writer extends CobolBase {
        final Area image = new Area(25);
        final CobField value = CobField.alnum("VALUE", image, 0, 4, false, null, null);
        final CobField timestamp = CobField.alnum("TIMESTAMP", image, 4, 21, false, null, null);
        final CobFile output = new CobFile("OUTPUT", "OUT", CobFile.Org.SEQUENTIAL, CobFile.Acc.SEQUENTIAL,
                image, 25, 25, null, null, null, null, false);
        private final String text;
        private final boolean extraClock;
        Writer(String text, boolean extraClock) { this.text = text; this.extraClock = extraClock; }
        @Override protected void _init() { image.fill((byte) ' '); }
        @Override protected void _dispatch(int pc) {
            output.open("OUTPUT");
            CobRt.move(text, value);
            CobRt.move(CobClock.currentDate(), timestamp);
            output.write(25);
            output.close();
            if (extraClock) CobClock.now();
            CobRt.display(false, "done");
        }
        @Override protected int _count() { return 1; }
        @Override protected Area[] _linkage() { return new Area[0]; }
        @Override protected Map<String, CobField> _fields() { return Map.of("VALUE", value, "TIMESTAMP", timestamp); }
        @Override public String programId() { return "WRITE"; }
    }
}
