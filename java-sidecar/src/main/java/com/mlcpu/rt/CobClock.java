package com.mlcpu.rt;

import java.time.OffsetDateTime;
import java.time.ZoneId;
import java.time.format.DateTimeFormatter;

/**
 * Program clock (§6.6.2): WALL (default), FIXED (every read returns the epoch, like GnuCOBOL COB_CURRENT_DATE),
 * MONOTONIC (starts at the epoch and advances one tick per read).
 */
public final class CobClock {
    private CobClock() {}

    private static String mode = CobConfig.setting("mlcpu.clock", "MLCPU_CLOCK", "WALL").toUpperCase();
    private static OffsetDateTime epoch = parseEpoch();
    private static long ticks;
    private static long tickMicros = Long.parseLong(CobConfig.setting("mlcpu.clockTickMicros", "MLCPU_CLOCK_TICK_MICROS", "1"));
    private static java.util.function.Consumer<OffsetDateTime> observer;
    private static java.util.function.Supplier<OffsetDateTime> replay;

    private static OffsetDateTime parseEpoch() {
        String e = CobConfig.setting("mlcpu.clockEpoch", "MLCPU_CLOCK_EPOCH", "");
        if (e.isEmpty()) return OffsetDateTime.now();
        return OffsetDateTime.parse(e);
    }

    public static void configure(String m, String isoEpoch) {
        try (RunUnit.Guard guard = RunUnit.guard()) {
        mode = m.toUpperCase();
        epoch = isoEpoch == null || isoEpoch.isEmpty() ? OffsetDateTime.now() : OffsetDateTime.parse(isoEpoch);
        ticks = 0;
        tickMicros = Long.parseLong(CobConfig.setting("mlcpu.clockTickMicros", "MLCPU_CLOCK_TICK_MICROS", "1"));
        }
    }

    public static OffsetDateTime now() {
        try (RunUnit.Guard guard = RunUnit.guard()) {
        OffsetDateTime value = replay != null ? replay.get() : switch (mode) {
            case "FIXED" -> epoch;
            case "MONOTONIC" -> epoch.plusNanos(1000L * tickMicros * ticks++);
            default -> OffsetDateTime.now(ZoneId.systemDefault());
        };
        if (observer != null) observer.accept(value);
        return value;
        }
    }

    /** FUNCTION CURRENT-DATE: YYYYMMDDHHMMSShh±hhmm (21 characters). */
    public static String currentDate() {
        OffsetDateTime t = now();
        int off = t.getOffset().getTotalSeconds() / 60;
        String sign = off < 0 ? "-" : "+";
        off = Math.abs(off);
        return t.format(DateTimeFormatter.ofPattern("yyyyMMddHHmmss")) + String.format("%02d", t.getNano() / 10_000_000)
                + sign + String.format("%02d%02d", off / 60, off % 60);
    }

    record State(String clockMode, OffsetDateTime clockEpoch, long clockTicks, long clockTickMicros,
                 java.util.function.Consumer<OffsetDateTime> clockObserver, java.util.function.Supplier<OffsetDateTime> clockReplay) {
        void restore() {
            mode = clockMode;
            epoch = clockEpoch;
            ticks = clockTicks;
            tickMicros = clockTickMicros;
            observer = clockObserver; replay = clockReplay;
        }
    }

    static State capture() { return new State(mode, epoch, ticks, tickMicros, observer, replay); }
    static State fresh() { return new State(mode, epoch, 0, tickMicros, null, null); }
    static void observe(java.util.function.Consumer<OffsetDateTime> recorder) { observer = recorder; }
    static void replay(java.util.function.Supplier<OffsetDateTime> values) { replay = values; }
    static void advanceFrom(State state) {
        mode = state.clockMode; epoch = state.clockEpoch; ticks = state.clockTicks; tickMicros = state.clockTickMicros;
    }
}
