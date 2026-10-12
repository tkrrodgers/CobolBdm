package com.mlcpu.capabilities.fnls0200;

import java.math.BigDecimal;
import com.mlcpu.capabilities.fnls0200.records.DtParms;

/**
 * A WORKING-STORAGE area FNLS0200 passes to a CALL.
 * Typed view of DT-PARMS (29 bytes); the bytes underneath are the record kit's DtParms, which
 * stores with the COBOL MOVE rules. Text getters drop trailing spaces; setters pad. Generated (typed-interface projection).
 */
public interface DtParmsView {
    /** A view over a CALL argument's bytes (no copy). */
    static DtParmsView over(byte[] bytes) {
        return new Bytes(DtParms.wrap(bytes));
    }

    /** A new area as WORKING-STORAGE starts: x'00' bytes, then the VALUE clauses. */
    static DtParmsView create() {
        return new Bytes(new DtParms((byte) 0));
    }

    /** The area itself (what a CALL passes BY REFERENCE). */
    byte[] bytes();

    /** INITIALIZE: text items to spaces, numeric items to zero. */
    void initialize();

    /** DT-FUNCTION PIC X(2); its 88-level names as Function */
    Function function();

    void function(Function v);

    /** The value itself (any value, also one without an 88-level name). */
    String functionCode();

    void functionCode(String v);

    /** DT-UNIT PIC X; its 88-level names as Unit */
    Unit unit();

    void unit(Unit v);

    /** The value itself (any value, also one without an 88-level name). */
    String unitCode();

    void unitCode(String v);

    /** DT-DAY PIC S9(9) COMP */
    long day();

    void day(long v);

    void day(BigDecimal v);

    /** DT-DAY2 PIC S9(9) COMP */
    long day2();

    void day2(long v);

    void day2(BigDecimal v);

    /** DT-N PIC S9(9) COMP */
    long n();

    void n(long v);

    void n(BigDecimal v);

    /** DT-RESULT PIC S9(9) COMP */
    long result();

    void result(long v);

    void result(BigDecimal v);

    /** DT-YYYYMMDD PIC 9(8) */
    long yyyymmdd();

    void yyyymmdd(long v);

    void yyyymmdd(BigDecimal v);

    /** DT-RC PIC S9(4) COMP */
    long rc();

    void rc(long v);

    void rc(BigDecimal v);

    /** The 88-level names of DT-FUNCTION. */
    enum Function {
        /** 88 DT-FN-DAY-OF-DATE */
        DAY_OF_DATE("DN"),
        /** 88 DT-FN-DATE-OF-DAY */
        DATE_OF_DAY("YM"),
        /** 88 DT-FN-PLUS */
        PLUS("PL"),
        /** 88 DT-FN-BETWEEN */
        BETWEEN("BT"),
        /** 88 DT-FN-LENGTH-OF-MONTH */
        LENGTH_OF_MONTH("LM"),
        /** 88 DT-FN-LENGTH-OF-YEAR */
        LENGTH_OF_YEAR("LY"),
        /** 88 DT-FN-YEAR */
        YEAR("YR"),
        /** 88 DT-FN-DAY-OF-MONTH */
        DAY_OF_MONTH("DM"),
        /** 88 DT-FN-WITH-DAY-OF-MONTH */
        WITH_DAY_OF_MONTH("WD"),
        /** 88 DT-FN-YEAR-END */
        YEAR_END("YE");

        public final String code;

        Function(String code) {
            this.code = code;
        }

        /** The constant for a value, or null when no 88-level name has it. */
        public static Function of(String code) {
            for (Function x : values()) if (x.code.equals(code)) return x;
            return null;
        }
    }

    /** The 88-level names of DT-UNIT. */
    enum Unit {
        /** 88 DT-UNIT-DAYS */
        DAYS("D"),
        /** 88 DT-UNIT-WEEKS */
        WEEKS("W"),
        /** 88 DT-UNIT-MONTHS */
        MONTHS("M"),
        /** 88 DT-UNIT-YEARS */
        YEARS("Y");

        public final String code;

        Unit(String code) {
            this.code = code;
        }

        /** The constant for a value, or null when no 88-level name has it. */
        public static Unit of(String code) {
            for (Unit x : values()) if (x.code.equals(code)) return x;
            return null;
        }
    }

    /** The record kit's DtParms behind the view. */
    final class Bytes implements DtParmsView {
        private final DtParms r;

        Bytes(DtParms r) {
            this.r = r;
        }

        public byte[] bytes() {
            return r.bytes();
        }

        public void initialize() {
            r.initialize();
        }

        public Function function() {
            return Function.of(strip(r.dtFunction()));
        }

        public void function(Function v) {
            r.dtFunction(v.code);
        }

        public String functionCode() {
            return strip(r.dtFunction());
        }

        public void functionCode(String v) {
            r.dtFunction(v);
        }

        public Unit unit() {
            return Unit.of(strip(r.dtUnit()));
        }

        public void unit(Unit v) {
            r.dtUnit(v.code);
        }

        public String unitCode() {
            return strip(r.dtUnit());
        }

        public void unitCode(String v) {
            r.dtUnit(v);
        }

        public long day() {
            return r.dtDay();
        }

        public void day(long v) {
            r.dtDay(v);
        }

        public void day(BigDecimal v) {
            r.dtDay(v);
        }

        public long day2() {
            return r.dtDay2();
        }

        public void day2(long v) {
            r.dtDay2(v);
        }

        public void day2(BigDecimal v) {
            r.dtDay2(v);
        }

        public long n() {
            return r.dtN();
        }

        public void n(long v) {
            r.dtN(v);
        }

        public void n(BigDecimal v) {
            r.dtN(v);
        }

        public long result() {
            return r.dtResult();
        }

        public void result(long v) {
            r.dtResult(v);
        }

        public void result(BigDecimal v) {
            r.dtResult(v);
        }

        public long yyyymmdd() {
            return r.dtYyyymmdd();
        }

        public void yyyymmdd(long v) {
            r.dtYyyymmdd(v);
        }

        public void yyyymmdd(BigDecimal v) {
            r.dtYyyymmdd(v);
        }

        public long rc() {
            return r.dtRc();
        }

        public void rc(long v) {
            r.dtRc(v);
        }

        public void rc(BigDecimal v) {
            r.dtRc(v);
        }
    }

    private static String strip(String s) {
        int n = s.length();
        while (n > 0 && s.charAt(n - 1) == ' ') n--;
        return s.substring(0, n);
    }
}
