package com.mlcpu.platform.text;

import com.mlcpu.platform.numeric.Decimals;
import java.math.BigDecimal;
import java.math.BigInteger;

/** Report and console formats of the legacy batch output. */
public final class Formats {
    private Formats() {}

    /**
     * An amount edited with a fixed leading sign and zero suppression, as in {@code -ZZZ,ZZZ,ZZZ.ZZ} (sign '-': minus or
     * blank) or {@code +ZZZ,ZZZ,ZZZ.ZZ} (sign '+': plus or minus). Suppression stops at the first non-zero digit or the
     * decimal point; a zero amount is all blanks.
     */
    private static final int SIGNED_AMOUNT_WIDTH = 15;

    public static String signedAmount(BigDecimal value, char signStyle) {
        BigDecimal v = Decimals.fit(value, 9, 2, true);
        if (v.signum() == 0) return " ".repeat(SIGNED_AMOUNT_WIDTH);
        String digits = zeroPad(v.unscaledValue().abs(), 11);
        StringBuilder sb = new StringBuilder(SIGNED_AMOUNT_WIDTH);
        sb.append(v.signum() < 0 ? '-' : signStyle == '+' ? '+' : ' ');
        boolean significant = false;
        int d = 0;
        for (char p : "ZZZ,ZZZ,ZZZ".toCharArray()) {
            if (p == ',') { sb.append(significant ? ',' : ' '); continue; }
            char c = digits.charAt(d++);
            if (c != '0') significant = true;
            sb.append(significant ? c : ' ');
        }
        sb.append('.').append(digits, 9, 11);
        return sb.toString();
    }

    private static String zeroPad(BigInteger n, int len) {
        String s = n.toString();
        if (s.length() > len) s = s.substring(s.length() - len);
        return "0".repeat(len - s.length()) + s;
    }

    /**
     * Console text of a signed zoned number: "gnucobol" prints the digits then '+' or '-'; "zos" prints the stored
     * bytes (the sign as an overpunch on the last digit).
     */
    public static String signedNumber(BigDecimal value, int integerDigits, int scale, String style) {
        BigDecimal v = Decimals.fit(value, integerDigits, scale, true);
        String digits = zeroPad(v.unscaledValue().abs(), integerDigits + scale);
        if ("gnucobol".equalsIgnoreCase(style)) return digits + (v.signum() < 0 ? '-' : '+');
        int last = digits.charAt(digits.length() - 1) - '0';
        char over = v.signum() < 0 ? (last == 0 ? '}' : (char) ('J' + last - 1)) : (last == 0 ? '{' : (char) ('A' + last - 1));
        return digits.substring(0, digits.length() - 1) + over;
    }

    /** Console text of an unsigned count: zero-padded digits. */
    public static String count(long value, int digits) {
        return zeroPad(BigInteger.valueOf(value).abs(), digits);
    }

    /** Pads or truncates to {@code width} characters. */
    public static String column(String s, int width) {
        String v = s == null ? "" : s;
        return v.length() >= width ? v.substring(0, width) : v + " ".repeat(width - v.length());
    }
}
