package com.mlcpu.capabilities.fnls0200;

import java.math.BigDecimal;
import com.mlcpu.capabilities.fnls0200.records.FnCurrencyRec;

/**
 * A CALL argument of FNLS0200 (LINKAGE).
 * Typed view of FN-CURRENCY-REC (40 bytes); the bytes underneath are the record kit's FnCurrencyRec, which
 * stores with the COBOL MOVE rules. Text getters drop trailing spaces; setters pad. Generated (typed-interface projection).
 */
public interface CurrencyRec {
    /** A view over a CALL argument's bytes (no copy). */
    static CurrencyRec over(byte[] bytes) {
        return new Bytes(FnCurrencyRec.wrap(bytes));
    }

    /** A new area as WORKING-STORAGE starts: x'00' bytes, then the VALUE clauses. */
    static CurrencyRec create() {
        return new Bytes(new FnCurrencyRec((byte) 0));
    }

    /** The area itself (what a CALL passes BY REFERENCE). */
    byte[] bytes();

    /** INITIALIZE: text items to spaces, numeric items to zero. */
    void initialize();

    /** CU-CODE PIC X(3) */
    String code();

    void code(String v);

    /** CU-DECIMAL-PLACES PIC 9(2) */
    long decimalPlaces();

    void decimalPlaces(long v);

    void decimalPlaces(BigDecimal v);

    /** CU-MULTIPLES-IND PIC X */
    String multiplesInd();

    void multiplesInd(String v);

    /** CU-MULTIPLES PIC 9(5) */
    long multiples();

    void multiples(long v);

    void multiples(BigDecimal v);

    /** CU-NAME PIC X(20) */
    String name();

    void name(String v);

    /** The record kit's FnCurrencyRec behind the view. */
    final class Bytes implements CurrencyRec {
        private final FnCurrencyRec r;

        Bytes(FnCurrencyRec r) {
            this.r = r;
        }

        public byte[] bytes() {
            return r.bytes();
        }

        public void initialize() {
            r.initialize();
        }

        public String code() {
            return strip(r.cuCode());
        }

        public void code(String v) {
            r.cuCode(v);
        }

        public long decimalPlaces() {
            return r.cuDecimalPlaces();
        }

        public void decimalPlaces(long v) {
            r.cuDecimalPlaces(v);
        }

        public void decimalPlaces(BigDecimal v) {
            r.cuDecimalPlaces(v);
        }

        public String multiplesInd() {
            return strip(r.cuMultiplesInd());
        }

        public void multiplesInd(String v) {
            r.cuMultiplesInd(v);
        }

        public long multiples() {
            return r.cuMultiples();
        }

        public void multiples(long v) {
            r.cuMultiples(v);
        }

        public void multiples(BigDecimal v) {
            r.cuMultiples(v);
        }

        public String name() {
            return strip(r.cuName());
        }

        public void name(String v) {
            r.cuName(v);
        }
    }

    private static String strip(String s) {
        int n = s.length();
        while (n > 0 && s.charAt(n - 1) == ' ') n--;
        return s.substring(0, n);
    }
}
