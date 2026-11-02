package com.mlcpu.rt;

/**
 * Record character set (§6.6.3). LATIN1 is identity (default). CP037 changes only the collating sequence of
 * alphanumeric comparisons, which then follows EBCDIC byte order.
 */
public final class RecordCharset {
    private RecordCharset() {}

    private static boolean EBCDIC;
    private static final int[] TO_EBCDIC = new int[256];

    static {
        String m = CobConfig.setting("mlcpu.recordCharset", "MLCPU_RECORD_CHARSET", "LATIN1");
        EBCDIC = m.equalsIgnoreCase("CP037") || m.equalsIgnoreCase("EBCDIC");
        java.nio.charset.Charset cp = java.nio.charset.Charset.forName("IBM037");
        for (int c = 0; c < 256; c++) {
            byte[] e = String.valueOf((char) c).getBytes(cp);
            TO_EBCDIC[c] = e.length == 1 ? e[0] & 0xFF : c;
        }
        TO_EBCDIC[0xFF] = 0xFF;
    }

    public static boolean isEbcdic() { return EBCDIC; }
    public static void configure(String charset) {
        try (RunUnit.Guard guard = RunUnit.guard()) {
            if (!charset.equalsIgnoreCase("LATIN1") && !charset.equalsIgnoreCase("CP037") && !charset.equalsIgnoreCase("EBCDIC"))
                throw new IllegalArgumentException("unsupported record charset: " + charset);
            EBCDIC = !charset.equalsIgnoreCase("LATIN1");
        }
    }
    static void restore(boolean ebcdic) { EBCDIC = ebcdic; }

    public static int ebcdicByte(int latin1) { return TO_EBCDIC[latin1 & 0xFF]; }
}
