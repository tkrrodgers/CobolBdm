package com.mlcpu.rt;

/**
 * A storage area: the bytes of one 01/77 record (or one FD record area). Linkage areas are rebound to the
 * caller's storage on each CALL, so every field view follows the binding.
 */
public final class Area {
    public byte[] b;
    public int base;
    public final int size;

    public Area(int size) {
        this.size = size;
        this.b = new byte[size];
    }

    /** Bind this (linkage) area to a caller's field storage: BY REFERENCE semantics. */
    public void bind(byte[] storage, int offset) {
        this.b = storage;
        this.base = offset;
    }

    public void fill(byte v) {
        java.util.Arrays.fill(b, base, base + size, v);
    }
}
