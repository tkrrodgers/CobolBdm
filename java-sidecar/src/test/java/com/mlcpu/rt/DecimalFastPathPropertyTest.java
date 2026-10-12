package com.mlcpu.rt;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;

import java.math.BigDecimal;
import java.math.BigInteger;
import java.math.RoundingMode;
import java.util.Random;
import org.junit.jupiter.api.Test;

/**
 * The runtime-speed fast paths (frontier burn-down P4) against the slow paths they replace:
 * <ul>
 *   <li>{@link CobRt#divTo}: a quotient computed at the receiver's scale + 1 stores (truncated or rounded half up) as
 *       {@link CobRt#div}'s 38-extra-digit quotient does;</li>
 *   <li>{@code CobField.split18} / {@code join18}: the two-long form of magnitudes below 10^36;</li>
 *   <li>the table-driven packed decoder: any bytes (invalid nibbles included) decode as the digit-by-digit
 *       reference does, for every packed length from 1 to 20 bytes.</li>
 * </ul>
 */
class DecimalFastPathPropertyTest {

    @Test
    void divToStoresAsTheFullQuotient() {
        Random r = new Random(4242);
        for (int i = 0; i < 100_000; i++) {
            BigDecimal a = new BigDecimal(new BigInteger(1 + r.nextInt(100), r).multiply(BigInteger.valueOf(r.nextBoolean() ? 1 : -1)), r.nextInt(20));
            BigDecimal b = new BigDecimal(new BigInteger(1 + r.nextInt(60), r).add(BigInteger.ONE).multiply(BigInteger.valueOf(r.nextBoolean() ? 1 : -1)),
                    r.nextInt(20));
            int scale = r.nextInt(20);
            RoundingMode mode = r.nextBoolean() ? RoundingMode.DOWN : RoundingMode.HALF_UP;
            BigDecimal full = CobRt.div(a, b).setScale(scale, mode);
            BigDecimal fast = CobRt.divTo(a, b, scale + 1).setScale(scale, mode);
            assertEquals(full, fast, () -> a + " / " + b + " at " + scale + " " + mode);
        }
    }

    @Test
    void splitAndJoinRoundTrip() {
        Random r = new Random(1801);
        BigInteger e18 = BigInteger.TEN.pow(18), e36 = BigInteger.TEN.pow(36);
        for (int i = 0; i < 100_000; i++) {
            BigInteger a = new BigInteger(1 + r.nextInt(120), r).mod(e36);
            long[] hl = CobField.split18(a);
            BigInteger[] qr = a.divideAndRemainder(e18);
            assertEquals(qr[0].longValue(), hl[0], () -> "hi of " + a);
            assertEquals(qr[1].longValue(), hl[1], () -> "lo of " + a);
            boolean neg = r.nextBoolean();
            assertEquals(neg ? a.negate() : a, CobField.join18(hl[0], hl[1], neg), () -> "join of " + a);
        }
    }

    @Test
    void packedDecodeMatchesTheDigitByDigitReference() {
        Random r = new Random(77);
        for (int i = 0; i < 100_000; i++) {
            int len = 1 + r.nextInt(20);
            byte[] bytes = new byte[len];
            r.nextBytes(bytes);
            if (r.nextInt(3) > 0) for (int k = 0; k < len; k++) bytes[k] = (byte) ((r.nextInt(10) << 4) | (k == len - 1 ? 0x0C + r.nextInt(2) : r.nextInt(10)));
            int scale = r.nextInt(2 * len);
            Area a = new Area(len);
            System.arraycopy(bytes, 0, a.b, 0, len);
            CobField f = CobField.num("P", CobField.Use.PACKED, a, 0, len, 2 * len - 1, scale, true, false, false, null, null);
            assertEquals(reference(bytes, scale), f.dec(), () -> java.util.HexFormat.of().formatHex(bytes) + " scale " + scale);
            assertArrayEquals(bytes, f.bytes());
        }
    }

    private static BigDecimal reference(byte[] b, int scale) {
        BigInteger u = BigInteger.ZERO;
        for (int i = 0; i < b.length; i++) {
            int v = b[i] & 0xFF;
            int[] ds = i == b.length - 1 ? new int[]{v >> 4} : new int[]{v >> 4, v & 0x0F};
            for (int d : ds) u = u.multiply(BigInteger.TEN).add(BigInteger.valueOf(d > 9 ? 0 : d));
        }
        int sign = b[b.length - 1] & 0x0F;
        return new BigDecimal(sign == 0x0D || sign == 0x0B ? u.negate() : u, scale);
    }
}
