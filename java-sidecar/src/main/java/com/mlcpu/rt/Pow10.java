package com.mlcpu.rt;

import java.math.BigInteger;

/** Powers of ten, computed once: every numeric store checks its value against 10^digits of the receiver. */
final class Pow10 {
    private Pow10() {}

    static final long[] LONG = new long[19];
    private static final BigInteger[] BIG = new BigInteger[64];

    static {
        LONG[0] = 1;
        for (int i = 1; i < LONG.length; i++) LONG[i] = LONG[i - 1] * 10;
        BIG[0] = BigInteger.ONE;
        for (int i = 1; i < BIG.length; i++) BIG[i] = BIG[i - 1].multiply(BigInteger.TEN);
    }

    static BigInteger big(int n) {
        return n < BIG.length ? BIG[n] : BigInteger.TEN.pow(n);
    }
}
