package com.mlcpu.rt;

/**
 * Zoned-decimal sign overpunch. Encoding follows the EBCDIC convention carried in ASCII data ({ A–I for +0..+9,
 * } J–R for −0..−9), which is what GnuCOBOL writes under -fsign=EBCDIC and what the CardDemo data uses. Decoding
 * also accepts plain digits (unsigned), the GnuCOBOL ASCII negative overpunch (p–y) and raw EBCDIC zones (Cx/Dx).
 */
public final class Overpunch {
    private Overpunch() {}

    /** Returns {digit, sign} where sign is -1, +1, or 0 (no sign). */
    public static int[] decode(int c) {
        if (c >= '0' && c <= '9') return new int[]{c - '0', 0};
        if (c == '{') return new int[]{0, 1};
        if (c >= 'A' && c <= 'I') return new int[]{c - 'A' + 1, 1};
        if (c == '}') return new int[]{0, -1};
        if (c >= 'J' && c <= 'R') return new int[]{c - 'J' + 1, -1};
        if (c >= 'p' && c <= 'y') return new int[]{c - 'p', -1};
        if (c >= 0xC0 && c <= 0xC9) return new int[]{c - 0xC0, 1};
        if (c >= 0xD0 && c <= 0xD9) return new int[]{c - 0xD0, -1};
        if (c >= 0xF0 && c <= 0xF9) return new int[]{c - 0xF0, 0};
        int d = c & 0x0F;
        return new int[]{d > 9 ? 0 : d, 0};
    }

    public static boolean isSign(int c) {
        return c == '{' || c == '}' || (c >= 'A' && c <= 'R') || (c >= 'p' && c <= 'y');
    }

    public static int encode(int digit, boolean negative) {
        if (CobConfig.ASCII_SIGN) return negative ? 'p' + digit : '0' + digit;
        if (negative) return digit == 0 ? '}' : 'J' + digit - 1;
        return digit == 0 ? '{' : 'A' + digit - 1;
    }
}
