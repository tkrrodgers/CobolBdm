package com.mlcpu.rt;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;

import java.math.BigDecimal;
import java.math.BigInteger;
import java.math.RoundingMode;
import java.nio.charset.StandardCharsets;
import java.util.Random;
import org.junit.jupiter.api.Test;

/**
 * The numeric store and decode paths (long fast paths and the BigInteger path) against an independent reference: a store
 * truncates the scale (or rounds half up), keeps the low-order {@code digits} digits of the magnitude with the sign, and
 * encodes packed (sign C/D, F when unsigned) or zoned (overpunch, or a separate leading/trailing sign). Random values of
 * 1 to 40 digits into receivers of 1 to 31 digits, so both overflow and every encoder path are hit.
 */
class NumericCodecPropertyTest {

    @Test
    void storesAndDecodesMatchTheReference() {
        Random r = new Random(20261011);
        for (int i = 0; i < 200_000; i++) {
            CobField.Use use = r.nextBoolean() ? CobField.Use.PACKED : CobField.Use.DISPLAY;
            int digits = 1 + r.nextInt(31);
            int scale = r.nextInt(Math.min(digits, 9) + 1);
            boolean signed = r.nextInt(4) != 0;
            boolean signSep = use == CobField.Use.DISPLAY && signed && r.nextBoolean();
            boolean signLead = signSep ? r.nextBoolean() : use == CobField.Use.DISPLAY && signed && r.nextInt(8) == 0;
            int len = use == CobField.Use.PACKED ? digits / 2 + 1 : digits + (signSep ? 1 : 0);
            CobField f = CobField.num("P", use, new Area(len), 0, len, digits, scale, signed, signSep, signLead, null, null);
            BigDecimal v = new BigDecimal(new BigInteger(1 + r.nextInt(130), r).multiply(BigInteger.valueOf(r.nextBoolean() ? 1 : -1)),
                    r.nextInt(12));
            boolean rounded = r.nextInt(5) == 0;
            boolean overflow = f.setDec(v, rounded);

            BigInteger u = v.setScale(scale, rounded ? RoundingMode.HALF_UP : RoundingMode.DOWN).unscaledValue();
            if (!signed) u = u.abs();
            BigInteger cap = BigInteger.TEN.pow(digits);
            assertEquals(u.abs().compareTo(cap) >= 0, overflow, () -> "overflow of " + v + " into " + describe(f));
            BigInteger kept = u.signum() < 0 ? u.negate().mod(cap).negate() : u.mod(cap);
            byte[] want = use == CobField.Use.PACKED ? packed(kept, len, signed) : zoned(kept, digits, signed, signSep, signLead);
            assertArrayEquals(want, java.util.Arrays.copyOf(f.area.b, len), () -> "bytes of " + v + " into " + describe(f));
            // A zero stored signed may come back as +0 either way; compare values.
            assertEquals(0, new BigDecimal(kept, scale).compareTo(f.dec()), () -> "decode of " + v + " from " + describe(f));
        }
    }

    @Test
    void divisionByAPowerOfTenIsExact() {
        Random r = new Random(7);
        for (int i = 0; i < 50_000; i++) {
            BigDecimal a = new BigDecimal(new BigInteger(1 + r.nextInt(100), r), r.nextInt(10));
            if (r.nextBoolean()) a = a.negate();
            BigDecimal b = BigDecimal.TEN.pow(r.nextInt(12)).movePointLeft(r.nextInt(6));
            if (r.nextBoolean()) b = b.negate();
            int sc = Math.max(a.scale(), 0) + 38;
            assertEquals(a.divide(b, sc, RoundingMode.DOWN), CobRt.div(a, b), "a=" + a + " b=" + b);
        }
    }

    private static String describe(CobField f) {
        return f.use + " digits " + f.digits + " scale " + f.scale + (f.signed ? " signed" : "") + (f.signSep ? " separate" : "")
                + (f.signLead ? " leading" : "");
    }

    private static byte[] packed(BigInteger u, int len, boolean signed) {
        String d = u.abs().toString();
        StringBuilder sb = new StringBuilder();
        for (int i = d.length(); i < len * 2 - 1; i++) sb.append('0');
        sb.append(d);
        byte[] out = new byte[len];
        int sign = signed ? (u.signum() < 0 ? 0x0D : 0x0C) : 0x0F;
        for (int i = 0; i < len; i++) {
            int hi = sb.charAt(i * 2) - '0';
            int lo = i == len - 1 ? sign : sb.charAt(i * 2 + 1) - '0';
            out[i] = (byte) ((hi << 4) | lo);
        }
        return out;
    }

    private static byte[] zoned(BigInteger u, int digits, boolean signed, boolean signSep, boolean signLead) {
        String d = u.abs().toString();
        StringBuilder sb = new StringBuilder();
        for (int i = d.length(); i < digits; i++) sb.append('0');
        sb.append(d);
        byte[] digitsBytes = sb.toString().getBytes(StandardCharsets.ISO_8859_1);
        boolean neg = u.signum() < 0;
        if (signed && signSep) {
            byte[] out = new byte[digits + 1];
            if (signLead) {
                out[0] = (byte) (neg ? '-' : '+');
                System.arraycopy(digitsBytes, 0, out, 1, digits);
            } else {
                System.arraycopy(digitsBytes, 0, out, 0, digits);
                out[digits] = (byte) (neg ? '-' : '+');
            }
            return out;
        }
        if (signed) {
            int sp = signLead ? 0 : digits - 1;
            digitsBytes[sp] = (byte) Overpunch.encode(digitsBytes[sp] - '0', neg);
        }
        return digitsBytes;
    }
}
