package com.mlcpu.platform.records;

import java.math.BigDecimal;
import java.math.BigInteger;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;

/**
 * Field codecs for fixed-length record images: text fields (ISO-8859-1, space padded) and zoned decimals (one digit per
 * byte; a signed field carries its sign in the last byte as an EBCDIC-style overpunch: { A-I positive, } J-R negative).
 */
public final class Fields {
    private Fields() {}

    /** Text with trailing spaces removed (other padding, such as binary zeros, is kept). */
    public static String text(byte[] r, int off, int len) {
        int end = off + len;
        while (end > off && r[end - 1] == ' ') end--;
        return new String(r, off, end - off, StandardCharsets.ISO_8859_1);
    }

    /** Text left-justified and space padded, truncated on the right when longer than the field. */
    public static void putText(byte[] r, int off, int len, String value) {
        byte[] v = (value == null ? "" : value).getBytes(StandardCharsets.ISO_8859_1);
        int n = Math.min(len, v.length);
        System.arraycopy(v, 0, r, off, n);
        Arrays.fill(r, off + n, off + len, (byte) ' ');
    }

    public static BigDecimal zoned(byte[] r, int off, int len, int scale) {
        StringBuilder digits = new StringBuilder(len);
        int sign = 1;
        for (int i = 0; i < len; i++) {
            int c = r[off + i] & 0xFF;
            int d;
            if (c >= '0' && c <= '9') d = c - '0';
            else if (c == '{') d = 0;
            else if (c >= 'A' && c <= 'I') d = c - 'A' + 1;
            else if (c == '}') { d = 0; sign = -1; }
            else if (c >= 'J' && c <= 'R') { d = c - 'J' + 1; sign = -1; }
            else if (c >= 'p' && c <= 'y') { d = c - 'p'; sign = -1; }
            else { d = c & 0x0F; if (d > 9) d = 0; }
            digits.append((char) ('0' + d));
        }
        BigDecimal v = new BigDecimal(new BigInteger(digits.toString()), scale);
        return sign < 0 ? v.negate() : v;
    }

    public static long unsigned(byte[] r, int off, int len) {
        return zoned(r, off, len, 0).abs().longValueExact();
    }

    /** Stores a value in a zoned field of {@code len} digits; the value must already fit (see Decimals.fit). */
    public static void putZoned(byte[] r, int off, int len, int scale, boolean signed, BigDecimal value) {
        BigInteger unscaled = value.setScale(scale, java.math.RoundingMode.DOWN).unscaledValue();
        String digits = unscaled.abs().toString();
        if (digits.length() > len) digits = digits.substring(digits.length() - len);
        digits = "0".repeat(len - digits.length()) + digits;
        byte[] b = digits.getBytes(StandardCharsets.US_ASCII);
        if (signed) {
            int last = b[len - 1] - '0';
            boolean negative = unscaled.signum() < 0;
            b[len - 1] = (byte) (negative ? (last == 0 ? '}' : 'J' + last - 1) : (last == 0 ? '{' : 'A' + last - 1));
        }
        System.arraycopy(b, 0, r, off, len);
    }

    public static void putUnsigned(byte[] r, int off, int len, long value) {
        putZoned(r, off, len, 0, false, BigDecimal.valueOf(value));
    }

    /** Zero-padded digits of an unsigned number, as stored in an unsigned zoned field of {@code len} digits. */
    public static String digits(long value, int len) {
        String s = Long.toString(Math.abs(value));
        if (s.length() > len) s = s.substring(s.length() - len);
        return "0".repeat(len - s.length()) + s;
    }

    public static String image(byte[] r) {
        return new String(r, StandardCharsets.ISO_8859_1);
    }

    /** Packed decimal (COMP-3): two digits per byte, the sign in the last nibble (C or F positive, D negative). */
    public static BigDecimal packed(byte[] r, int off, int len, int scale) {
        StringBuilder digits = new StringBuilder(len * 2);
        for (int i = 0; i < len; i++) {
            int b = r[off + i] & 0xFF;
            digits.append((char) ('0' + Math.min(9, b >> 4)));
            if (i < len - 1) digits.append((char) ('0' + Math.min(9, b & 0x0F)));
        }
        BigDecimal v = new BigDecimal(new BigInteger(digits.toString()), scale);
        return (r[off + len - 1] & 0x0F) == 0x0D ? v.negate() : v;
    }

    /** Stores a packed decimal of {@code len} bytes (2·len−1 digits); high-order digits beyond the field are dropped. */
    public static void putPacked(byte[] r, int off, int len, int scale, boolean signed, BigDecimal value) {
        BigInteger unscaled = value.setScale(scale, java.math.RoundingMode.DOWN).unscaledValue();
        int n = 2 * len - 1;
        String digits = unscaled.abs().toString();
        if (digits.length() > n) digits = digits.substring(digits.length() - n);
        digits = "0".repeat(n - digits.length()) + digits;
        for (int i = 0; i < len; i++) {
            int hi = digits.charAt(2 * i) - '0';
            int lo = i < len - 1 ? digits.charAt(2 * i + 1) - '0' : (!signed ? 0x0F : unscaled.signum() < 0 ? 0x0D : 0x0C);
            r[off + i] = (byte) ((hi << 4) | lo);
        }
    }

    /** Big-endian two's-complement binary (COMP / COMP-4 / BINARY) of {@code len} bytes (2, 4 or 8). */
    public static long binary(byte[] r, int off, int len) {
        long v = r[off];
        for (int i = 1; i < len; i++) v = (v << 8) | (r[off + i] & 0xFF);
        return v;
    }

    public static void putBinary(byte[] r, int off, int len, long value) {
        for (int i = len - 1; i >= 0; i--) {
            r[off + i] = (byte) value;
            value >>= 8;
        }
    }
}
