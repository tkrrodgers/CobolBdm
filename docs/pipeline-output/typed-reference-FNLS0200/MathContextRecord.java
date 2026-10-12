package com.mlcpu.capabilities.fnls0200;

import java.math.BigDecimal;
import com.mlcpu.capabilities.fnls0200.records.FnMathContext;

/**
 * A CALL argument of FNLS0200 (LINKAGE).
 * Typed view of FN-MATH-CONTEXT (3 bytes); the bytes underneath are the record kit's FnMathContext, which
 * stores with the COBOL MOVE rules. Text getters drop trailing spaces; setters pad. Generated (typed-interface projection).
 */
public interface MathContextRecord {
    /** A view over a CALL argument's bytes (no copy). */
    static MathContextRecord over(byte[] bytes) {
        return new Bytes(FnMathContext.wrap(bytes));
    }

    /** A new area as WORKING-STORAGE starts: x'00' bytes, then the VALUE clauses. */
    static MathContextRecord create() {
        return new Bytes(new FnMathContext((byte) 0));
    }

    /** The area itself (what a CALL passes BY REFERENCE). */
    byte[] bytes();

    /** INITIALIZE: text items to spaces, numeric items to zero. */
    void initialize();

    /** MC-PRECISION PIC S9(4) COMP */
    long precision();

    void precision(long v);

    void precision(BigDecimal v);

    /** MC-MODE PIC X; its 88-level names as Mode */
    Mode mode();

    void mode(Mode v);

    /** The value itself (any value, also one without an 88-level name). */
    String modeCode();

    void modeCode(String v);

    /** The 88-level names of MC-MODE. */
    enum Mode {
        /** 88 MC-UP */
        UP("U"),
        /** 88 MC-DOWN */
        DOWN("D"),
        /** 88 MC-CEILING */
        CEILING("C"),
        /** 88 MC-FLOOR */
        FLOOR("F"),
        /** 88 MC-HALF-UP */
        HALF_UP("H"),
        /** 88 MC-HALF-DOWN */
        HALF_DOWN("N"),
        /** 88 MC-HALF-EVEN */
        HALF_EVEN("E");

        public final String code;

        Mode(String code) {
            this.code = code;
        }

        /** The constant for a value, or null when no 88-level name has it. */
        public static Mode of(String code) {
            for (Mode x : values()) if (x.code.equals(code)) return x;
            return null;
        }
    }

    /** The record kit's FnMathContext behind the view. */
    final class Bytes implements MathContextRecord {
        private final FnMathContext r;

        Bytes(FnMathContext r) {
            this.r = r;
        }

        public byte[] bytes() {
            return r.bytes();
        }

        public void initialize() {
            r.initialize();
        }

        public long precision() {
            return r.mcPrecision();
        }

        public void precision(long v) {
            r.mcPrecision(v);
        }

        public void precision(BigDecimal v) {
            r.mcPrecision(v);
        }

        public Mode mode() {
            return Mode.of(strip(r.mcMode()));
        }

        public void mode(Mode v) {
            r.mcMode(v.code);
        }

        public String modeCode() {
            return strip(r.mcMode());
        }

        public void modeCode(String v) {
            r.mcMode(v);
        }
    }

    private static String strip(String s) {
        int n = s.length();
        while (n > 0 && s.charAt(n - 1) == ' ') n--;
        return s.substring(0, n);
    }
}
