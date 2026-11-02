package com.mlcpu.rt;

import static org.junit.jupiter.api.Assertions.*;

import java.math.BigDecimal;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import org.junit.jupiter.api.Test;

class RunUnitTest {
    @Test
    void unitsRetainStateButDoNotShareRegistersRegistryExternalDataDdClockOrOutput() {
        DisplaySink before = DisplaySink.current();
        byte beforeDefault = CobConfig.DEFAULT_BYTE;
        byte[] beforeRegister = CobRt.RETURN_CODE.buf();
        try (RunUnit a = new RunUnit(); RunUnit b = new RunUnit()) {
            Counter first;
            Area external;
            try (RunUnit.Scope scope = a.enter()) {
                first = new Counter();
                ProgramRegistry.register("COUNT", first);
                first.call(new CobField[0], false);
                external = CobRt.externalArea("SHARED", 4);
                external.b[0] = 42;
                CobFile.DD.put("INPUT", "a.dat");
                CobConfig.DEFAULT_BYTE = 'A';
                CobClock.configure("FIXED", "2026-01-01T00:00:00Z");
                CobRt.RETURN_CODE.setDec(BigDecimal.valueOf(17), false);
                FrontierHits.record("A:1", "test");
                DisplaySink.current().write("A", true);
            }
            try (RunUnit.Scope scope = b.enter()) {
                assertNull(ProgramRegistry.resolve("COUNT"));
                assertNotSame(external, CobRt.externalArea("SHARED", 4));
                assertEquals(0, CobRt.externalArea("SHARED", 4).b[0]);
                assertFalse(CobFile.DD.containsKey("INPUT"));
                assertEquals(0, CobRt.RETURN_CODE.intValue());
                assertEquals(List.of(), FrontierHits.hits());
                assertEquals(List.of(), DisplaySink.current().lines());
                assertEquals(beforeDefault, CobConfig.DEFAULT_BYTE);
                CobConfig.DEFAULT_BYTE = 'B';
                CobRt.RETURN_CODE.setDec(BigDecimal.valueOf(99), false);
                CobFile.DD.put("INPUT", "b.dat");
            }
            try (RunUnit.Scope scope = a.enter()) {
                assertSame(first, ProgramRegistry.resolve("COUNT"));
                first.call(new CobField[0], false);
                assertEquals(2, first.count.intValue());
                assertSame(external, CobRt.externalArea("SHARED", 4));
                assertEquals(42, external.b[0]);
                assertEquals("a.dat", CobFile.DD.get("INPUT"));
                assertEquals(17, CobRt.RETURN_CODE.intValue());
                assertEquals('A', CobConfig.DEFAULT_BYTE);
                assertEquals(2026, CobClock.now().getYear());
                assertEquals(List.of("A"), DisplaySink.current().lines());
                assertEquals(1, FrontierHits.hits().size());
            }
            assertSame(before, DisplaySink.current());
            assertSame(beforeRegister, CobRt.RETURN_CODE.buf());
            assertEquals(beforeDefault, CobConfig.DEFAULT_BYTE);
        }
    }

    @Test
    void concurrentRequestsAreIsolatedAndCompatibilityExecutionIsSerialized() throws Exception {
        List<RunUnit> units = new ArrayList<>();
        for (int i = 0; i < 6; i++) units.add(new RunUnit());
        var pool = Executors.newFixedThreadPool(6);
        CountDownLatch start = new CountDownLatch(1);
        java.util.concurrent.atomic.AtomicInteger active = new java.util.concurrent.atomic.AtomicInteger();
        java.util.concurrent.atomic.AtomicInteger maximum = new java.util.concurrent.atomic.AtomicInteger();
        try {
            List<java.util.concurrent.Future<Integer>> futures = new ArrayList<>();
            for (int i = 0; i < units.size(); i++) {
                RunUnit unit = units.get(i);
                int id = i;
                futures.add(pool.submit(() -> {
                    start.await();
                    Counter counter;
                    try (RunUnit.Scope scope = unit.enter()) {
                        counter = new Counter();
                        ProgramRegistry.register("COUNT", counter);
                        CobFile.DD.put("INPUT", Integer.toString(id));
                    }
                    for (int n = 0; n < 20; n++)
                        try (RunUnit.Scope scope = unit.enter()) {
                            maximum.accumulateAndGet(active.incrementAndGet(), Math::max);
                            try {
                                assertSame(counter, ProgramRegistry.resolve("COUNT"));
                                assertEquals(Integer.toString(id), CobFile.DD.get("INPUT"));
                                counter.call(new CobField[0], false);
                            } finally { active.decrementAndGet(); }
                        }
                    return counter.count.intValue();
                }));
            }
            start.countDown();
            for (var future : futures) assertEquals(20, future.get(20, TimeUnit.SECONDS));
            assertEquals(1, maximum.get());
        } finally {
            pool.shutdownNow();
            for (RunUnit unit : units) unit.close();
        }
    }

    @Test
    void scopesMustCloseInOrderAndProgramInstancesCannotCrossUnits() {
        try (RunUnit a = new RunUnit(); RunUnit b = new RunUnit()) {
            Counter counter;
            try (RunUnit.Scope scope = a.enter()) {
                counter = new Counter();
                counter.call(new CobField[0], false);
                RunUnit.Scope outer = a.enter();
                RunUnit.Scope inner = a.enter();
                assertThrows(IllegalStateException.class, outer::close);
                inner.close();
                outer.close();
            }
            try (RunUnit.Scope scope = b.enter()) {
                assertThrows(CobRt.RuntimeCheck.class, () -> counter.call(new CobField[0], false));
            }
            assertNull(RunUnit.current());
        }
    }

    @Test
    void recursiveCallsRestoreLinkageLocalStorageCalledFlagAndControlState() {
        try (RunUnit unit = new RunUnit(); RunUnit.Scope scope = unit.enter()) {
            Reentrant program = new Reentrant();
            byte[] outer = "AAAA".getBytes(java.nio.charset.StandardCharsets.ISO_8859_1);
            program.call(new CobField[]{CobField.temp(outer)}, false);
            assertEquals('Z', outer[0]);
            assertEquals('Y', program.child[0]);
            assertSame(outer, program.arg.buf());
        }
    }

    @Test
    void cancelResetsWorkingStorageAndFactoryInstancesButKeepsExternalStorage() {
        try (RunUnit unit = new RunUnit(); RunUnit.Scope scope = unit.enter()) {
            Counter program = new Counter();
            ProgramRegistry.register("COUNT", program);
            Area external = CobRt.externalArea("SHARED", 1);
            external.b[0] = 77;
            CobRt.call("COUNT", new CobField[0]);
            CobRt.call("COUNT", new CobField[0]);
            assertEquals(2, program.count.intValue());
            CobRt.cancel("COUNT");
            CobRt.call("COUNT", new CobField[0]);
            assertEquals(1, program.count.intValue());
            assertEquals(77, external.b[0]);
            ProgramRegistry.registerModernFactory("MOD", () -> new ModernProgramViews() {
                private int calls;
                @Override public void callViews(CallArgument[] arguments) {
                    arguments[0].storage()[arguments[0].offset()] = (byte) ++calls;
                }
            });
            byte[] data = new byte[1];
            CobRt.call("MOD", new CobField[]{CobField.temp(data)});
            CobRt.call("MOD", new CobField[]{CobField.temp(data)});
            assertEquals(2, data[0]);
            CobRt.cancel("MOD");
            CobRt.call("MOD", new CobField[]{CobField.temp(data)});
            assertEquals(1, data[0]);
            ProgramRegistry.registerModern("NOFACTORY", args -> {});
            assertThrows(CobRt.RuntimeCheck.class, () -> CobRt.cancel("NOFACTORY"));
        }
    }

    @Test
    void closeFailuresAreReportedAllResourcesAreAttemptedAndOuterStateIsRestored() {
        DisplaySink before = DisplaySink.current();
        RunUnit unit = new RunUnit();
        java.util.concurrent.atomic.AtomicInteger closed = new java.util.concurrent.atomic.AtomicInteger();
        try (RunUnit.Scope scope = unit.enter()) {
            OpenDatasets.opened(() -> { closed.incrementAndGet(); throw new java.io.IOException("disk failure"); });
            OpenDatasets.opened(closed::incrementAndGet);
        }
        IllegalStateException failure = assertThrows(IllegalStateException.class, unit::close);
        assertEquals("disk failure", failure.getCause().getMessage());
        assertEquals(2, closed.get());
        assertSame(before, DisplaySink.current());
        assertNull(RunUnit.current());
        assertThrows(IllegalStateException.class, unit::enter);
    }

    @Test
    void isolatedProgramFactoriesReadOnlyTheirBoundSysIn(@org.junit.jupiter.api.io.TempDir java.nio.file.Path directory) throws Exception {
        java.io.BufferedReader previous = SysIn.current();
        java.nio.file.Path first = directory.resolve("first.txt"), second = directory.resolve("second.txt");
        java.nio.file.Files.writeString(first, "ONE\n");
        java.nio.file.Files.writeString(second, "TWO\n");
        java.util.function.Supplier<CobolBase> factory = () -> new Counter() {
            @Override protected void _dispatch(int pc) { CobRt.display(false, SysIn.readLine()); }
        };
        assertEquals(List.of("ONE"), JobEngine.runProgram(factory, Map.of("SYSIN", first), null).display());
        assertEquals(List.of("TWO"), JobEngine.runProgram(factory, Map.of("SYSIN", second), null).display());
        assertSame(previous, SysIn.current());
    }

    private static class Counter extends CobolBase {
        final Area area = new Area(4);
        final CobField count = CobField.num("COUNT", CobField.Use.BINARY, area, 0, 4, 9, 0, false, false, false, null, null);
        @Override protected void _init() { count.setDec(BigDecimal.ZERO, false); }
        @Override protected void _dispatch(int pc) { count.setDec(count.dec().add(BigDecimal.ONE), false); }
        @Override protected int _count() { return 1; }
        @Override protected Area[] _linkage() { return new Area[0]; }
        @Override protected Map<String, CobField> _fields() { return Map.of("COUNT", count); }
        @Override public String programId() { return "COUNT"; }
    }

    private static final class Reentrant extends Counter {
        private final Area linkage = Area.linkage(4);
        private final Area local = new Area(1);
        final CobField arg = CobField.group("ARG", linkage, 0, 4, null, null);
        final byte[] child = "BBBB".getBytes(java.nio.charset.StandardCharsets.ISO_8859_1);
        @Override protected Area[] _linkage() { return new Area[]{linkage}; }
        @Override protected Area[] _localAreas() { return new Area[]{local}; }
        @Override protected void _initLocal() { local.b[0] = 3; }
        @Override protected void _dispatch(int pc) {
            if (arg.buf()[0] == 'A') {
                byte[] parent = arg.buf();
                local.b[0] = 9;
                _goto = 0;
                call(new CobField[]{CobField.temp(child)}, true);
                assertSame(parent, arg.buf());
                assertEquals(9, local.b[0]);
                assertEquals(0, _goto);
                assertFalse(_called);
                arg.buf()[0] = 'Z';
            } else if (arg.buf()[0] == 'B') {
                assertEquals(3, local.b[0]);
                local.b[0] = 7;
                arg.buf()[0] = 'Y';
                _goto = 0;
            }
        }
    }
}
