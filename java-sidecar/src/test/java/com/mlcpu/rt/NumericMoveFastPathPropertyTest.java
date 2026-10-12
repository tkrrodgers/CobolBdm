package com.mlcpu.rt;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;

import java.math.BigDecimal;
import java.math.BigInteger;
import java.util.Arrays;
import java.util.Random;
import org.junit.jupiter.api.Test;

/**
 * Phase 5 runtime speed: a numeric MOVE between items whose values fit a long goes through {@link CobField#unscaledLong}
 * and {@link CobField#storeUnscaled} instead of BigDecimal. On 300,000 random MOVEs (every usage, sign form, digit count,
 * scale, any sender bytes) the receiver's bytes must be those of {@code setDec(sender.dec(), false)}, and every decode
 * through the long path must equal {@link CobField#dec()}.
 */
class NumericMoveFastPathPropertyTest {

    static CobField random(Random r, Area a, int off) {
        int kind = r.nextInt(6);
        boolean signed = r.nextInt(4) != 0;
        int digits, len, scale = r.nextInt(9) - 2;
        CobField.Use use;
        switch (kind) {
            case 0, 1 -> { use = CobField.Use.BINARY; len = new int[]{2, 4, 8, 1, 3, 5, 6, 7}[r.nextInt(8)]; digits = Math.max(1, Math.min(18, len * 2 + r.nextInt(3) - 1)); }
            case 2 -> { use = CobField.Use.COMP5; len = new int[]{2, 4, 8}[r.nextInt(3)]; digits = len == 2 ? 4 : len == 4 ? 9 : 18; }
            case 3 -> { use = CobField.Use.PACKED; digits = 1 + r.nextInt(31); len = digits / 2 + 1; }
            default -> { use = CobField.Use.DISPLAY; digits = 1 + r.nextInt(31); len = digits; }
        }
        if (scale > digits) scale = digits;
        boolean sep = use == CobField.Use.DISPLAY && signed && r.nextInt(3) == 0, lead = r.nextBoolean();
        if (sep) len++;
        return CobField.num("F", use, a, off, len, digits, scale, signed, sep, sep ? lead : r.nextInt(5) == 0, null, null);
    }

    @Test
    void numericMovesMatchTheBigDecimalPath() {
        boolean truncate = CobConfig.BINARY_TRUNCATE, le = CobConfig.COMP5_LITTLE_ENDIAN;
        Random r = new Random(1012);
        try {
            for (int n = 0; n < 300_000; n++) {
                CobConfig.BINARY_TRUNCATE = r.nextBoolean();
                CobConfig.COMP5_LITTLE_ENDIAN = r.nextBoolean();
                Area sa = new Area(40), da = new Area(40), ra = new Area(40);
                CobField s = random(r, sa, 3);
                CobField d = random(r, da, 1);
                CobField ref = CobField.num("F", d.use, ra, 1, d.len, d.digits, d.scale, d.signed, d.signSep, d.signLead, null, null);
                if (r.nextBoolean()) {
                    byte[] any = new byte[s.len];
                    if (r.nextBoolean()) r.nextBytes(any);
                    else for (int i = 0; i < any.length; i++) any[i] = (byte) ('0' + r.nextInt(10));
                    System.arraycopy(any, 0, sa.b, 3, s.len);
                } else {
                    BigInteger u = new BigInteger(1 + r.nextInt(64), r);
                    s.setDec(new BigDecimal(r.nextBoolean() ? u.negate() : u, r.nextInt(8)), false);
                }
                byte[] init = new byte[40];
                r.nextBytes(init);
                System.arraycopy(init, 0, da.b, 0, 40);
                System.arraycopy(init, 0, ra.b, 0, 40);
                long u = s.unscaledLong();
                if (u != CobField.NO_LONG) assertEquals(s.dec(), BigDecimal.valueOf(u, s.scale), () -> "decode " + describe(s));
                CobRt.move(s, d);
                ref.setDec(s.dec(), false);
                assertArrayEquals(ra.b, da.b, () -> "MOVE " + describe(s) + " -> " + describe(d) + " value " + s.dec());
            }
        } finally {
            CobConfig.BINARY_TRUNCATE = truncate;
            CobConfig.COMP5_LITTLE_ENDIAN = le;
        }
    }

    @Test
    void comparisonsSubscriptsAndLiteralMovesMatchTheBigDecimalPath() {
        boolean truncate = CobConfig.BINARY_TRUNCATE, le = CobConfig.COMP5_LITTLE_ENDIAN;
        Random r = new Random(20261012L);
        try {
            for (int n = 0; n < 300_000; n++) {
                CobConfig.BINARY_TRUNCATE = r.nextBoolean();
                CobConfig.COMP5_LITTLE_ENDIAN = r.nextBoolean();
                Area aa = new Area(40), ba = new Area(40), da = new Area(40), ra = new Area(40);
                CobField a = random(r, aa, 2), b = random(r, ba, 5), d = random(r, da, 1);
                CobField ref = CobField.num("F", d.use, ra, 1, d.len, d.digits, d.scale, d.signed, d.signSep, d.signLead, null, null);
                for (CobField f : new CobField[]{a, b}) {
                    if (r.nextBoolean()) {
                        byte[] any = new byte[f.len];
                        if (r.nextBoolean()) r.nextBytes(any);
                        else for (int i = 0; i < any.length; i++) any[i] = (byte) ('0' + r.nextInt(10));
                        System.arraycopy(any, 0, f.area.b, f.pos(), f.len);
                    } else f.setDec(new BigDecimal(new BigInteger(1 + r.nextInt(64), r).multiply(BigInteger.valueOf(r.nextBoolean() ? 1 : -1)), r.nextInt(8)), false);
                }
                String text = (r.nextBoolean() ? "-" : "") + new BigInteger(1 + r.nextInt(66), r) + (r.nextBoolean() ? "" : "." + r.nextInt(100000));
                NumLit lit = new NumLit(text);
                assertEquals(Integer.signum(a.dec().compareTo(b.dec())), Integer.signum(CobRt.compare(a, b)), () -> "compare " + describe(a) + " " + describe(b));
                assertEquals(Integer.signum(a.dec().compareTo(lit.v)), Integer.signum(CobRt.compare(a, lit)), () -> "compare " + describe(a) + " " + text);
                assertEquals(Integer.signum(lit.v.compareTo(b.dec())), Integer.signum(CobRt.compare(lit, b)), () -> "compare " + text + " " + describe(b));
                assertEquals(a.dec().intValue(), a.intValue(), () -> "intValue " + describe(a));
                byte[] init = new byte[40];
                r.nextBytes(init);
                System.arraycopy(init, 0, da.b, 0, 40);
                System.arraycopy(init, 0, ra.b, 0, 40);
                CobRt.move(lit, d);
                ref.setDec(lit.v, false);
                assertArrayEquals(ra.b, da.b, () -> "MOVE " + text + " -> " + describe(d));
            }
        } finally {
            CobConfig.BINARY_TRUNCATE = truncate;
            CobConfig.COMP5_LITTLE_ENDIAN = le;
        }
    }

    static String describe(CobField f) {
        return f.use + "/" + f.len + "/" + f.digits + "v" + f.scale + (f.signed ? "s" : "u") + (f.signSep ? "sep" : "") + (f.signLead ? "lead" : "")
                + " " + Arrays.toString(Arrays.copyOfRange(f.area.b, f.pos(), f.pos() + f.len));
    }
}
