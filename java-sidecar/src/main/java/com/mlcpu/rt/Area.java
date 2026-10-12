package com.mlcpu.rt;

/**
 * A storage area: the bytes of one 01/77 record (or one FD record area). Linkage areas are rebound to the
 * caller's storage on each CALL, so every field view follows the binding.
 */
public final class Area {
    private static final byte[] NONE = new byte[0];

    public byte[] b;
    public int base;
    public final int size;
    /** A LINKAGE record named in PROCEDURE DIVISION USING: it has no storage of its own until it is bound. */
    private final boolean linkage;
    private boolean bound;

    public Area(int size) {
        this(size, false);
    }

    private Area(int size, boolean linkage) {
        this.size = size;
        this.linkage = linkage;
        this.b = linkage ? NONE : new byte[size];
    }

    /**
     * A LINKAGE record of PROCEDURE DIVISION USING. As in COBOL it owns no storage: a CALL binds it to the caller's bytes
     * (BY REFERENCE). A program used without that binding (a characterization run, a CALL with fewer arguments) gets
     * storage of its own then ({@link #materialize}). A record passed BY REFERENCE through every CALL of an estate (the
     * J3S heap: 477 MB) is thus never allocated once per program.
     */
    public static Area linkage(int size) {
        return new Area(size, true);
    }

    /** Bind this (linkage) area to a caller's field storage: BY REFERENCE semantics. */
    public void bind(byte[] storage, int offset) {
        this.b = storage;
        this.base = offset;
        this.bound = true;
    }

    /** An unbound LINKAGE record that is used anyway: storage of its own, filled with {@code fill}. */
    public void materialize(byte fill) {
        if (!linkage || bound || b.length >= size) return;
        b = new byte[size];
        base = 0;
        java.util.Arrays.fill(b, fill);
    }

    public void fill(byte v) {
        java.util.Arrays.fill(b, base, base + size, v);
    }
}
