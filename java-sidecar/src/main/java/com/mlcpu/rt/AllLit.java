package com.mlcpu.rt;

/** ALL literal: the pattern repeats to the length of the other operand. */
public final class AllLit {
    public final byte[] pattern;

    public AllLit(String s) {
        this.pattern = s.getBytes(java.nio.charset.StandardCharsets.ISO_8859_1);
    }

    public AllLit(Fig f) {
        this.pattern = new byte[]{f.b};
    }

    public byte[] repeat(int n) {
        byte[] r = new byte[n];
        for (int i = 0; i < n; i++) r[i] = pattern[i % pattern.length];
        return r;
    }
}
