package com.mlcpu.rt;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;

import java.math.BigDecimal;
import java.math.BigInteger;
import java.math.RoundingMode;
import java.util.Random;
import org.junit.jupiter.api.Test;

/**
 * Phase 5 runtime speed: binary items (COMP, COMP-5, INDEX) are decoded and stored through a long when they fit. Each
 * fast path must give the bytes, the value and the overflow flag of the BigInteger algorithm it replaces (reproduced
 * here as the reference), on 400,000 random stores and decodes over every length, sign, scale, byte order and both
 * truncation settings.
 */
class BinaryFastPathPropertyTest {

    record Shape(CobField.Use use, int len, int digits, int scale, boolean signed) {}

    static Shape shape(Random r) {
        CobField.Use use = switch (r.nextInt(4)) { case 0 -> CobField.Use.COMP5; case 1 -> CobField.Use.INDEX; default -> CobField.Use.BINARY; };
        int len = use == CobField.Use.INDEX ? 4 : new int[]{1, 2, 4, 8, 3, 5, 6, 7}[r.nextInt(8)];
        int maxDigits = switch (len) { case 1 -> 2; case 2 -> 4; case 3 -> 6; case 4 -> 9; case 5 -> 11; case 6 -> 14; case 7 -> 16; default -> 18; };
        int digits = 1 + r.nextInt(maxDigits);
        int scale = use == CobField.Use.INDEX ? 0 : r.nextInt(Math.min(digits, 5) + 1);
        return new Shape(use, len, digits, scale, use == CobField.Use.INDEX || r.nextBoolean());
    }

    static CobField field(Shape s) {
        return CobField.num("B", s.use, new Area(s.len + 3), 2, s.len, s.digits, s.scale, s.signed, false, false, null, null);
    }

    /** The BigInteger store this replaces (CobField before phase 5). */
    static byte[] referenceStore(Shape s, BigDecimal v, boolean[] overflowOut) {
        BigInteger u = v.setScale(s.scale, RoundingMode.DOWN).unscaledValue();
        if (!s.signed && s.use != CobField.Use.INDEX) u = u.abs();
        boolean sgn = s.signed || s.use == CobField.Use.INDEX;
        BigInteger cap = s.use == CobField.Use.BINARY && CobConfig.BINARY_TRUNCATE ? BigInteger.TEN.pow(s.digits)
                : BigInteger.ONE.shiftLeft(s.len * 8 - (sgn ? 1 : 0));
        boolean overflow = u.abs().compareTo(cap) >= 0;
        if (overflow) {
            if (s.use == CobField.Use.BINARY && CobConfig.BINARY_TRUNCATE) u = u.signum() < 0 ? u.negate().mod(cap).negate() : u.mod(cap);
            else {
                BigInteger mod = BigInteger.ONE.shiftLeft(s.len * 8);
                u = u.mod(mod);
                if (sgn && u.testBit(s.len * 8 - 1)) u = u.subtract(mod);
            }
        }
        overflowOut[0] = overflow;
        byte[] raw = u.toByteArray();
        byte[] out = new byte[s.len];
        byte fill = (byte) (u.signum() < 0 ? 0xFF : 0x00);
        boolean le = s.use == CobField.Use.COMP5 && CobConfig.COMP5_LITTLE_ENDIAN;
        for (int i = 0; i < s.len; i++) {
            int src = raw.length - s.len + i;
            out[le ? s.len - 1 - i : i] = src >= 0 ? raw[src] : fill;
        }
        return out;
    }

    /** The BigInteger decode this replaces. */
    static BigDecimal referenceDecode(Shape s, byte[] bytes) {
        byte[] raw = bytes.clone();
        if (s.use == CobField.Use.COMP5 && CobConfig.COMP5_LITTLE_ENDIAN)
            for (int i = 0, j = raw.length - 1; i < j; i++, j--) { byte t = raw[i]; raw[i] = raw[j]; raw[j] = t; }
        BigInteger v = s.signed || s.use == CobField.Use.INDEX ? new BigInteger(raw) : new BigInteger(1, raw);
        return new BigDecimal(v, s.scale);
    }

    @Test
    void binaryStoresAndDecodesMatchTheBigIntegerAlgorithm() {
        boolean truncate = CobConfig.BINARY_TRUNCATE, le = CobConfig.COMP5_LITTLE_ENDIAN;
        Random r = new Random(20261012);
        try {
            for (int n = 0; n < 200_000; n++) {
                CobConfig.BINARY_TRUNCATE = r.nextBoolean();
                CobConfig.COMP5_LITTLE_ENDIAN = r.nextBoolean();
                Shape s = shape(r);
                CobField f = field(s);
                int bits = 1 + r.nextInt(70);
                BigInteger unscaled = new BigInteger(bits, r);
                if (r.nextBoolean()) unscaled = unscaled.negate();
                BigDecimal v = new BigDecimal(unscaled, r.nextInt(7));
                boolean[] refOverflow = {false};
                byte[] expected = referenceStore(s, v, refOverflow);
                boolean overflow = f.setDec(v, false);
                byte[] actual = java.util.Arrays.copyOfRange(f.area.b, 2, 2 + s.len);
                assertArrayEquals(expected, actual, () -> s + " store " + v);
                assertEquals(refOverflow[0], overflow, () -> s + " overflow " + v);
                assertEquals(referenceDecode(s, actual), f.dec(), () -> s + " decode after " + v);
                // any bytes at all, as storage may hold them (REDEFINES, group moves)
                byte[] any = new byte[s.len];
                r.nextBytes(any);
                System.arraycopy(any, 0, f.area.b, 2, s.len);
                BigDecimal ref = referenceDecode(s, any);
                assertEquals(ref, f.dec(), () -> s + " decode of random bytes");
                assertEquals(ref.scale(), f.dec().scale());
            }
        } finally {
            CobConfig.BINARY_TRUNCATE = truncate;
            CobConfig.COMP5_LITTLE_ENDIAN = le;
        }
    }
}
