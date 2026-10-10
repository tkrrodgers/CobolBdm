package com.mlcpu.rt;

/**
 * Runtime dialect switches. Defaults reproduce IBM Enterprise COBOL; each can be set with a system property or
 * environment variable so a parity run can mirror the oracle's configuration.
 */
public final class CobConfig {
    private CobConfig() {}

    static String setting(String prop, String env, String def) {
        String v = System.getProperty(prop);
        if (v == null) v = System.getenv(env);
        return v == null || v.isBlank() ? def : v.trim();
    }

    /** TRUNC(STD): binary items truncate to their PICTURE. GnuCOBOL -std=ibm uses binary-truncate:no. */
    public static boolean BINARY_TRUNCATE = !"no".equalsIgnoreCase(setting("mlcpu.binaryTruncate", "MLCPU_BINARY_TRUNCATE", "yes"));

    /** Write ASCII-convention negative overpunch (p–y) instead of the EBCDIC convention. */
    public static boolean ASCII_SIGN = "ascii".equalsIgnoreCase(setting("mlcpu.sign", "MLCPU_SIGN", "ebcdic"));

    /**
     * DISPLAY of numeric items: "zos" shows the stored bytes (sign in the zone of the last digit, Appendix A);
     * "gnucobol" shows digits with a separate sign as GnuCOBOL does with pretty-display:no.
     */
    public static String DISPLAY_NUMERIC = setting("mlcpu.displayNumeric", "MLCPU_DISPLAY_NUMERIC", "zos");

    /** Initial byte for storage without VALUE (IBM STORAGE option / GnuCOBOL defaultbyte). */
    public static byte DEFAULT_BYTE = (byte) Integer.parseInt(setting("mlcpu.defaultByte", "MLCPU_DEFAULT_BYTE", "0"));

    /** Maximum loop iterations before a loop is cut (I10). */
    public static long LOOP_LIMIT = Long.parseLong(setting("mlcpu.loopLimit", "MLCPU_LOOP_LIMIT", "2000000000"));

    public static void reload() {
        BINARY_TRUNCATE = !"no".equalsIgnoreCase(setting("mlcpu.binaryTruncate", "MLCPU_BINARY_TRUNCATE", "yes"));
        ASCII_SIGN = "ascii".equalsIgnoreCase(setting("mlcpu.sign", "MLCPU_SIGN", "ebcdic"));
        DISPLAY_NUMERIC = setting("mlcpu.displayNumeric", "MLCPU_DISPLAY_NUMERIC", "zos");
        DEFAULT_BYTE = (byte) Integer.parseInt(setting("mlcpu.defaultByte", "MLCPU_DEFAULT_BYTE", "0"));
        LOOP_LIMIT = Long.parseLong(setting("mlcpu.loopLimit", "MLCPU_LOOP_LIMIT", "2000000000"));
    }
}
