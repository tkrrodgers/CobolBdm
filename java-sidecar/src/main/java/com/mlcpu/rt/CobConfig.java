package com.mlcpu.rt;

/**
 * Runtime dialect switches. Defaults reproduce IBM Enterprise COBOL; each can be set with a system property or
 * environment variable so a parity run can mirror the oracle's configuration.
 */
public final class CobConfig {
    private CobConfig() {}

    public static String setting(String prop, String env, String def) {
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

    /**
     * INITIALIZE as GnuCOBOL does it ("gnucobol") or as IBM does ("zos"): GnuCOBOL leaves a signed zoned item's sign
     * unpunched ("00"). Defaults to the GnuCOBOL dialect whenever DISPLAY_NUMERIC is "gnucobol" (the parity setting).
     */
    public static boolean GNU_INITIALIZE = "gnucobol".equalsIgnoreCase(setting("mlcpu.initialize", "MLCPU_INITIALIZE", DISPLAY_NUMERIC));

    /**
     * COMP-5 byte order: "native" stores it little-endian as GnuCOBOL does on x86-64, "big" as z/OS does. Defaults to
     * native whenever DISPLAY_NUMERIC is "gnucobol" (the parity setting).
     */
    public static boolean COMP5_LITTLE_ENDIAN = "native".equalsIgnoreCase(setting("mlcpu.comp5", "MLCPU_COMP5",
            "gnucobol".equalsIgnoreCase(DISPLAY_NUMERIC) ? "native" : "big"));

    /**
     * GnuCOBOL's arithmetic-osvs (-std=ibm): intermediate results of an arithmetic statement are truncated to the scale cobc
     * chooses at compile time ({@link CobRt#align}). "gnucobol" applies it, "zos" keeps exact intermediates. Defaults to the
     * GnuCOBOL dialect whenever DISPLAY_NUMERIC is "gnucobol" (the parity setting).
     */
    public static boolean ARITHMETIC_OSVS = "gnucobol".equalsIgnoreCase(setting("mlcpu.arithmetic", "MLCPU_ARITHMETIC", DISPLAY_NUMERIC));

    /** Initial byte for storage without VALUE (IBM STORAGE option / GnuCOBOL defaultbyte). */
    public static byte DEFAULT_BYTE = (byte) Integer.parseInt(setting("mlcpu.defaultByte", "MLCPU_DEFAULT_BYTE", "0"));

    /** Maximum loop iterations before a loop is cut (I10). */
    public static long LOOP_LIMIT = Long.parseLong(setting("mlcpu.loopLimit", "MLCPU_LOOP_LIMIT", "2000000000"));

    /**
     * The id of an explicit {@link RawMoveProfile} that may apply a copy order to shifted overlapping raw MOVEs. Empty (the
     * default): every such MOVE stops at a frontier. An unknown id is never guessed: the MOVE stops at a frontier naming it.
     */
    public static String RAW_MOVE_PROFILE = setting("mlcpu.rawMoveProfile", "MLCPU_RAW_MOVE_PROFILE", "");

    public static void reload() {
        BINARY_TRUNCATE = !"no".equalsIgnoreCase(setting("mlcpu.binaryTruncate", "MLCPU_BINARY_TRUNCATE", "yes"));
        ASCII_SIGN = "ascii".equalsIgnoreCase(setting("mlcpu.sign", "MLCPU_SIGN", "ebcdic"));
        DISPLAY_NUMERIC = setting("mlcpu.displayNumeric", "MLCPU_DISPLAY_NUMERIC", "zos");
        GNU_INITIALIZE = "gnucobol".equalsIgnoreCase(setting("mlcpu.initialize", "MLCPU_INITIALIZE", DISPLAY_NUMERIC));
        COMP5_LITTLE_ENDIAN = "native".equalsIgnoreCase(setting("mlcpu.comp5", "MLCPU_COMP5",
                "gnucobol".equalsIgnoreCase(DISPLAY_NUMERIC) ? "native" : "big"));
        ARITHMETIC_OSVS = "gnucobol".equalsIgnoreCase(setting("mlcpu.arithmetic", "MLCPU_ARITHMETIC", DISPLAY_NUMERIC));
        DEFAULT_BYTE = (byte) Integer.parseInt(setting("mlcpu.defaultByte", "MLCPU_DEFAULT_BYTE", "0"));
        LOOP_LIMIT = Long.parseLong(setting("mlcpu.loopLimit", "MLCPU_LOOP_LIMIT", "2000000000"));
        RAW_MOVE_PROFILE = setting("mlcpu.rawMoveProfile", "MLCPU_RAW_MOVE_PROFILE", "");
    }

    record State(boolean binaryTruncate, boolean asciiSign, String displayNumeric, boolean gnuInitialize,
                 boolean comp5LittleEndian, boolean arithmeticOsvs, byte defaultByte, long loopLimit, String rawMoveProfile) {
        void restore() {
            BINARY_TRUNCATE = binaryTruncate;
            ASCII_SIGN = asciiSign;
            DISPLAY_NUMERIC = displayNumeric;
            GNU_INITIALIZE = gnuInitialize;
            COMP5_LITTLE_ENDIAN = comp5LittleEndian;
            ARITHMETIC_OSVS = arithmeticOsvs;
            DEFAULT_BYTE = defaultByte;
            LOOP_LIMIT = loopLimit;
            RAW_MOVE_PROFILE = rawMoveProfile;
        }
    }

    static State capture() {
        return new State(BINARY_TRUNCATE, ASCII_SIGN, DISPLAY_NUMERIC, GNU_INITIALIZE, COMP5_LITTLE_ENDIAN,
                ARITHMETIC_OSVS, DEFAULT_BYTE, LOOP_LIMIT, RAW_MOVE_PROFILE);
    }
}
