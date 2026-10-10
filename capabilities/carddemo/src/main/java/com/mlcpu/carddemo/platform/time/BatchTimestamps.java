package com.mlcpu.carddemo.platform.time;

import java.time.OffsetDateTime;

/** Timestamps written by the batch programs. */
public final class BatchTimestamps {
    private BatchTimestamps() {}

    /** DB2 timestamp text {@code YYYY-MM-DD-HH.MM.SS.hh0000}: to the hundredth of a second, then four zeros. */
    public static String db2(OffsetDateTime t) {
        return String.format("%04d-%02d-%02d-%02d.%02d.%02d.%02d0000",
                t.getYear(), t.getMonthValue(), t.getDayOfMonth(), t.getHour(), t.getMinute(), t.getSecond(), t.getNano() / 10_000_000);
    }
}
