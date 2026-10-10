package com.mlcpu.rt;

/** Figurative constants. */
public enum Fig {
    SPACES((byte) ' '), ZEROS((byte) '0'), HIGH_VALUES((byte) 0xFF), LOW_VALUES((byte) 0x00), QUOTES((byte) '"'), NULLS((byte) 0x00);

    public final byte b;

    Fig(byte b) {
        this.b = b;
    }
}
