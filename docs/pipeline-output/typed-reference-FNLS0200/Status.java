package com.mlcpu.capabilities.fnls0200;

import java.math.BigDecimal;
import com.mlcpu.capabilities.fnls0200.records.FnStatus;

/**
 * A CALL argument of FNLS0200 (LINKAGE).
 * Typed view of FN-STATUS (94 bytes); the bytes underneath are the record kit's FnStatus, which
 * stores with the COBOL MOVE rules. Text getters drop trailing spaces; setters pad. Generated (typed-interface projection).
 */
public interface Status {
    /** A view over a CALL argument's bytes (no copy). */
    static Status over(byte[] bytes) {
        return new Bytes(FnStatus.wrap(bytes));
    }

    /** A new area as WORKING-STORAGE starts: x'00' bytes, then the VALUE clauses. */
    static Status create() {
        return new Bytes(new FnStatus((byte) 0));
    }

    /** The area itself (what a CALL passes BY REFERENCE). */
    byte[] bytes();

    /** INITIALIZE: text items to spaces, numeric items to zero. */
    void initialize();

    /** ST-RC PIC S9(4) COMP; its 88-level names as Rc */
    Rc rc();

    void rc(Rc v);

    /** The value itself (any value, also one without an 88-level name). */
    long rcCode();

    void rcCode(long v);

    void rcCode(BigDecimal v);

    /** ST-REJECT-CODE PIC X(8) */
    String rejectCode();

    void rejectCode(String v);

    /** ST-REJECT-SRC PIC X(24) */
    String rejectSrc();

    void rejectSrc(String v);

    /** ST-REJECT-TEXT PIC X(60) */
    String rejectText();

    void rejectText(String v);

    /** The 88-level names of ST-RC. */
    enum Rc {
        /** 88 ST-OK */
        OK(0L),
        /** 88 ST-REJECTED */
        REJECTED(4L),
        /** 88 ST-ABEND */
        ABEND(16L);

        public final long code;

        Rc(long code) {
            this.code = code;
        }

        /** The constant for a value, or null when no 88-level name has it. */
        public static Rc of(long code) {
            for (Rc x : values()) if (x.code == code) return x;
            return null;
        }
    }

    /** The record kit's FnStatus behind the view. */
    final class Bytes implements Status {
        private final FnStatus r;

        Bytes(FnStatus r) {
            this.r = r;
        }

        public byte[] bytes() {
            return r.bytes();
        }

        public void initialize() {
            r.initialize();
        }

        public Rc rc() {
            return Rc.of(r.stRc());
        }

        public void rc(Rc v) {
            r.stRc(v.code);
        }

        public long rcCode() {
            return r.stRc();
        }

        public void rcCode(long v) {
            r.stRc(v);
        }

        public void rcCode(BigDecimal v) {
            r.stRc(v);
        }

        public String rejectCode() {
            return strip(r.stRejectCode());
        }

        public void rejectCode(String v) {
            r.stRejectCode(v);
        }

        public String rejectSrc() {
            return strip(r.stRejectSrc());
        }

        public void rejectSrc(String v) {
            r.stRejectSrc(v);
        }

        public String rejectText() {
            return strip(r.stRejectText());
        }

        public void rejectText(String v) {
            r.stRejectText(v);
        }
    }

    private static String strip(String s) {
        int n = s.length();
        while (n > 0 && s.charAt(n - 1) == ' ') n--;
        return s.substring(0, n);
    }
}
