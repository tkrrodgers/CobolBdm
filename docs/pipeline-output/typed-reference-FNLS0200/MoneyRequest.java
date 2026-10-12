package com.mlcpu.capabilities.fnls0200;

import java.math.BigDecimal;
import com.mlcpu.capabilities.fnls0200.records.LkMoneyRequest;

/**
 * A WORKING-STORAGE area FNLS0200 passes to a CALL.
 * Typed view of LK-MONEY-REQUEST (114 bytes); the bytes underneath are the record kit's LkMoneyRequest, which
 * stores with the COBOL MOVE rules. Text getters drop trailing spaces; setters pad. Generated (typed-interface projection).
 */
public interface MoneyRequest {
    /** A view over a CALL argument's bytes (no copy). */
    static MoneyRequest over(byte[] bytes) {
        return new Bytes(LkMoneyRequest.wrap(bytes));
    }

    /** A new area as WORKING-STORAGE starts: x'00' bytes, then the VALUE clauses. */
    static MoneyRequest create() {
        return new Bytes(new LkMoneyRequest((byte) 0));
    }

    /** The area itself (what a CALL passes BY REFERENCE). */
    byte[] bytes();

    /** INITIALIZE: text items to spaces, numeric items to zero. */
    void initialize();

    /** MY-FUNCTION PIC X(2); its 88-level names as Function */
    Function function();

    void function(Function v);

    /** The value itself (any value, also one without an 88-level name). */
    String functionCode();

    void functionCode(String v);

    /** MY-SITE PIC X(24) */
    String site();

    void site(String v);

    /** MY-MONEY-A PIC S9(13)V9(4) COMP-3 */
    BigDecimal moneyA();

    void moneyA(BigDecimal v);

    void moneyA(long v);

    /** MY-MONEY-R PIC S9(13)V9(4) COMP-3 */
    BigDecimal moneyR();

    void moneyR(BigDecimal v);

    void moneyR(long v);

    /** MY-RATE-A PIC S9(3)V9(12) COMP-3 */
    BigDecimal rateA();

    void rateA(BigDecimal v);

    void rateA(long v);

    /** MY-RATE-R PIC S9(3)V9(12) COMP-3 */
    BigDecimal rateR();

    void rateR(BigDecimal v);

    void rateR(long v);

    /** MY-DF-A (group) */
    String dfA();

    void dfA(String v);

    /** MY-DF-A-COEF PIC S9(31) COMP-3 */
    BigDecimal dfACoef();

    void dfACoef(BigDecimal v);

    void dfACoef(long v);

    /** MY-DF-A-EXP PIC S9(4) COMP */
    long dfAExp();

    void dfAExp(long v);

    void dfAExp(BigDecimal v);

    /** MY-DF-B (group) */
    String dfB();

    void dfB(String v);

    /** MY-DF-B-COEF PIC S9(31) COMP-3 */
    BigDecimal dfBCoef();

    void dfBCoef(BigDecimal v);

    void dfBCoef(long v);

    /** MY-DF-B-EXP PIC S9(4) COMP */
    long dfBExp();

    void dfBExp(long v);

    void dfBExp(BigDecimal v);

    /** MY-DF-R (group) */
    String dfR();

    void dfR(String v);

    /** MY-DF-R-COEF PIC S9(31) COMP-3 */
    BigDecimal dfRCoef();

    void dfRCoef(BigDecimal v);

    void dfRCoef(long v);

    /** MY-DF-R-EXP PIC S9(4) COMP */
    long dfRExp();

    void dfRExp(long v);

    void dfRExp(BigDecimal v);

    /** The 88-level names of MY-FUNCTION. */
    enum Function {
        /** 88 MY-OF-DF */
        OF_DF("OD"),
        /** 88 MY-TO-DF */
        TO_DF("TD"),
        /** 88 MY-MULTIPLY */
        MULTIPLY("MU"),
        /** 88 MY-DIVIDE */
        DIVIDE("DV"),
        /** 88 MY-PERCENTAGE-OF */
        PERCENTAGE_OF("PC"),
        /** 88 MY-RATE-SET-SCALE */
        RATE_SET_SCALE("RS"),
        /** 88 MY-RATE-TO-DF */
        RATE_TO_DF("RD");

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

    /** The record kit's LkMoneyRequest behind the view. */
    final class Bytes implements MoneyRequest {
        private final LkMoneyRequest r;

        Bytes(LkMoneyRequest r) {
            this.r = r;
        }

        public byte[] bytes() {
            return r.bytes();
        }

        public void initialize() {
            r.initialize();
        }

        public Function function() {
            return Function.of(strip(r.myFunction()));
        }

        public void function(Function v) {
            r.myFunction(v.code);
        }

        public String functionCode() {
            return strip(r.myFunction());
        }

        public void functionCode(String v) {
            r.myFunction(v);
        }

        public String site() {
            return strip(r.mySite());
        }

        public void site(String v) {
            r.mySite(v);
        }

        public BigDecimal moneyA() {
            return r.myMoneyA();
        }

        public void moneyA(BigDecimal v) {
            r.myMoneyA(v);
        }

        public void moneyA(long v) {
            r.myMoneyA(v);
        }

        public BigDecimal moneyR() {
            return r.myMoneyR();
        }

        public void moneyR(BigDecimal v) {
            r.myMoneyR(v);
        }

        public void moneyR(long v) {
            r.myMoneyR(v);
        }

        public BigDecimal rateA() {
            return r.myRateA();
        }

        public void rateA(BigDecimal v) {
            r.myRateA(v);
        }

        public void rateA(long v) {
            r.myRateA(v);
        }

        public BigDecimal rateR() {
            return r.myRateR();
        }

        public void rateR(BigDecimal v) {
            r.myRateR(v);
        }

        public void rateR(long v) {
            r.myRateR(v);
        }

        public String dfA() {
            return r.myDfA();
        }

        public void dfA(String v) {
            r.myDfA(v);
        }

        public BigDecimal dfACoef() {
            return r.myDfACoef();
        }

        public void dfACoef(BigDecimal v) {
            r.myDfACoef(v);
        }

        public void dfACoef(long v) {
            r.myDfACoef(v);
        }

        public long dfAExp() {
            return r.myDfAExp();
        }

        public void dfAExp(long v) {
            r.myDfAExp(v);
        }

        public void dfAExp(BigDecimal v) {
            r.myDfAExp(v);
        }

        public String dfB() {
            return r.myDfB();
        }

        public void dfB(String v) {
            r.myDfB(v);
        }

        public BigDecimal dfBCoef() {
            return r.myDfBCoef();
        }

        public void dfBCoef(BigDecimal v) {
            r.myDfBCoef(v);
        }

        public void dfBCoef(long v) {
            r.myDfBCoef(v);
        }

        public long dfBExp() {
            return r.myDfBExp();
        }

        public void dfBExp(long v) {
            r.myDfBExp(v);
        }

        public void dfBExp(BigDecimal v) {
            r.myDfBExp(v);
        }

        public String dfR() {
            return r.myDfR();
        }

        public void dfR(String v) {
            r.myDfR(v);
        }

        public BigDecimal dfRCoef() {
            return r.myDfRCoef();
        }

        public void dfRCoef(BigDecimal v) {
            r.myDfRCoef(v);
        }

        public void dfRCoef(long v) {
            r.myDfRCoef(v);
        }

        public long dfRExp() {
            return r.myDfRExp();
        }

        public void dfRExp(long v) {
            r.myDfRExp(v);
        }

        public void dfRExp(BigDecimal v) {
            r.myDfRExp(v);
        }
    }

    private static String strip(String s) {
        int n = s.length();
        while (n > 0 && s.charAt(n - 1) == ' ') n--;
        return s.substring(0, n);
    }
}
