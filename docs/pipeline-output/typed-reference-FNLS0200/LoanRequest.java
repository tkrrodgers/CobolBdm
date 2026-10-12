package com.mlcpu.capabilities.fnls0200;

import java.math.BigDecimal;
import com.mlcpu.capabilities.fnls0200.records.FnLoanRequest;

/**
 * A CALL argument of FNLS0200 (LINKAGE).
 * Typed view of FN-LOAN-REQUEST (200 bytes); the bytes underneath are the record kit's FnLoanRequest, which
 * stores with the COBOL MOVE rules. Text getters drop trailing spaces; setters pad. Generated (typed-interface projection).
 */
public interface LoanRequest {
    /** A view over a CALL argument's bytes (no copy). */
    static LoanRequest over(byte[] bytes) {
        return new Bytes(FnLoanRequest.wrap(bytes));
    }

    /** A new area as WORKING-STORAGE starts: x'00' bytes, then the VALUE clauses. */
    static LoanRequest create() {
        return new Bytes(new FnLoanRequest((byte) 0));
    }

    /** The area itself (what a CALL passes BY REFERENCE). */
    byte[] bytes();

    /** INITIALIZE: text items to spaces, numeric items to zero. */
    void initialize();

    /** RQ-REC-TYPE PIC X(2) */
    String recType();

    void recType(String v);

    /** RQ-LOAN-ID PIC 9(16) */
    long loanId();

    void loanId(long v);

    void loanId(BigDecimal v);

    /** RQ-SCHED-START-DATE PIC 9(8) */
    long schedStartDate();

    void schedStartDate(long v);

    void schedStartDate(BigDecimal v);

    /** RQ-CURRENCY-CODE PIC X(3) */
    String currencyCode();

    void currencyCode(String v);

    /** RQ-DISB-AMOUNT PIC S9(13)V9(4) */
    BigDecimal disbAmount();

    void disbAmount(BigDecimal v);

    void disbAmount(long v);

    /** RQ-DISB-DATE PIC 9(8) */
    long disbDate();

    void disbDate(long v);

    void disbDate(BigDecimal v);

    /** RQ-NUM-REPAYMENTS PIC 9(4) */
    long numRepayments();

    void numRepayments(long v);

    void numRepayments(BigDecimal v);

    /** RQ-REPAY-EVERY PIC 9(4) */
    long repayEvery();

    void repayEvery(long v);

    void repayEvery(BigDecimal v);

    /** RQ-FREQ-TYPE PIC X(10) */
    String freqType();

    void freqType(String v);

    /** RQ-ANNUAL-RATE PIC S9(3)V9(9) */
    BigDecimal annualRate();

    void annualRate(BigDecimal v);

    void annualRate(long v);

    /** RQ-DOWNPAY-ENABLED PIC X */
    String downpayEnabled();

    void downpayEnabled(String v);

    /** RQ-DAYS-IN-MONTH PIC 9(2) */
    long daysInMonth();

    void daysInMonth(long v);

    void daysInMonth(BigDecimal v);

    /** RQ-DAYS-IN-YEAR PIC 9(3) */
    long daysInYear();

    void daysInYear(long v);

    void daysInYear(BigDecimal v);

    /** RQ-DOWNPAY-PCT-IND PIC X */
    String downpayPctInd();

    void downpayPctInd(String v);

    /** RQ-DOWNPAY-PCT PIC S9(3)V9(9) */
    BigDecimal downpayPct();

    void downpayPct(BigDecimal v);

    void downpayPct(long v);

    /** RQ-MULTIPLES-IND PIC X */
    String multiplesInd();

    void multiplesInd(String v);

    /** RQ-MULTIPLES PIC 9(9) */
    long multiples();

    void multiples(long v);

    void multiples(BigDecimal v);

    /** RQ-FIXED-LENGTH-IND PIC X */
    String fixedLengthInd();

    void fixedLengthInd(String v);

    /** RQ-FIXED-LENGTH PIC 9(5) */
    long fixedLength();

    void fixedLength(long v);

    void fixedLength(BigDecimal v);

    /** RQ-INT-RECOG-DISB PIC X */
    String intRecogDisb();

    void intRecogDisb(String v);

    /** RQ-DIY-STRATEGY PIC X(20) */
    String diyStrategy();

    void diyStrategy(String v);

    /** RQ-INTEREST-METHOD PIC X(18) */
    String interestMethod();

    void interestMethod(String v);

    /** RQ-ALLOW-PARTIAL PIC X */
    String allowPartial();

    void allowPartial(String v);

    /** RQ-FULL-TERM-TRANCHE PIC X */
    String fullTermTranche();

    void fullTermTranche(String v);

    /** 88 RQ-IS-LOAN-REQUEST */
    boolean isIsLoanRequest();

    void setIsLoanRequest();

    /** The record kit's FnLoanRequest behind the view. */
    final class Bytes implements LoanRequest {
        private final FnLoanRequest r;

        Bytes(FnLoanRequest r) {
            this.r = r;
        }

        public byte[] bytes() {
            return r.bytes();
        }

        public void initialize() {
            r.initialize();
        }

        public String recType() {
            return strip(r.rqRecType());
        }

        public void recType(String v) {
            r.rqRecType(v);
        }

        public long loanId() {
            return r.rqLoanId();
        }

        public void loanId(long v) {
            r.rqLoanId(v);
        }

        public void loanId(BigDecimal v) {
            r.rqLoanId(v);
        }

        public long schedStartDate() {
            return r.rqSchedStartDate();
        }

        public void schedStartDate(long v) {
            r.rqSchedStartDate(v);
        }

        public void schedStartDate(BigDecimal v) {
            r.rqSchedStartDate(v);
        }

        public String currencyCode() {
            return strip(r.rqCurrencyCode());
        }

        public void currencyCode(String v) {
            r.rqCurrencyCode(v);
        }

        public BigDecimal disbAmount() {
            return r.rqDisbAmount();
        }

        public void disbAmount(BigDecimal v) {
            r.rqDisbAmount(v);
        }

        public void disbAmount(long v) {
            r.rqDisbAmount(v);
        }

        public long disbDate() {
            return r.rqDisbDate();
        }

        public void disbDate(long v) {
            r.rqDisbDate(v);
        }

        public void disbDate(BigDecimal v) {
            r.rqDisbDate(v);
        }

        public long numRepayments() {
            return r.rqNumRepayments();
        }

        public void numRepayments(long v) {
            r.rqNumRepayments(v);
        }

        public void numRepayments(BigDecimal v) {
            r.rqNumRepayments(v);
        }

        public long repayEvery() {
            return r.rqRepayEvery();
        }

        public void repayEvery(long v) {
            r.rqRepayEvery(v);
        }

        public void repayEvery(BigDecimal v) {
            r.rqRepayEvery(v);
        }

        public String freqType() {
            return strip(r.rqFreqType());
        }

        public void freqType(String v) {
            r.rqFreqType(v);
        }

        public BigDecimal annualRate() {
            return r.rqAnnualRate();
        }

        public void annualRate(BigDecimal v) {
            r.rqAnnualRate(v);
        }

        public void annualRate(long v) {
            r.rqAnnualRate(v);
        }

        public String downpayEnabled() {
            return strip(r.rqDownpayEnabled());
        }

        public void downpayEnabled(String v) {
            r.rqDownpayEnabled(v);
        }

        public long daysInMonth() {
            return r.rqDaysInMonth();
        }

        public void daysInMonth(long v) {
            r.rqDaysInMonth(v);
        }

        public void daysInMonth(BigDecimal v) {
            r.rqDaysInMonth(v);
        }

        public long daysInYear() {
            return r.rqDaysInYear();
        }

        public void daysInYear(long v) {
            r.rqDaysInYear(v);
        }

        public void daysInYear(BigDecimal v) {
            r.rqDaysInYear(v);
        }

        public String downpayPctInd() {
            return strip(r.rqDownpayPctInd());
        }

        public void downpayPctInd(String v) {
            r.rqDownpayPctInd(v);
        }

        public BigDecimal downpayPct() {
            return r.rqDownpayPct();
        }

        public void downpayPct(BigDecimal v) {
            r.rqDownpayPct(v);
        }

        public void downpayPct(long v) {
            r.rqDownpayPct(v);
        }

        public String multiplesInd() {
            return strip(r.rqMultiplesInd());
        }

        public void multiplesInd(String v) {
            r.rqMultiplesInd(v);
        }

        public long multiples() {
            return r.rqMultiples();
        }

        public void multiples(long v) {
            r.rqMultiples(v);
        }

        public void multiples(BigDecimal v) {
            r.rqMultiples(v);
        }

        public String fixedLengthInd() {
            return strip(r.rqFixedLengthInd());
        }

        public void fixedLengthInd(String v) {
            r.rqFixedLengthInd(v);
        }

        public long fixedLength() {
            return r.rqFixedLength();
        }

        public void fixedLength(long v) {
            r.rqFixedLength(v);
        }

        public void fixedLength(BigDecimal v) {
            r.rqFixedLength(v);
        }

        public String intRecogDisb() {
            return strip(r.rqIntRecogDisb());
        }

        public void intRecogDisb(String v) {
            r.rqIntRecogDisb(v);
        }

        public String diyStrategy() {
            return strip(r.rqDiyStrategy());
        }

        public void diyStrategy(String v) {
            r.rqDiyStrategy(v);
        }

        public String interestMethod() {
            return strip(r.rqInterestMethod());
        }

        public void interestMethod(String v) {
            r.rqInterestMethod(v);
        }

        public String allowPartial() {
            return strip(r.rqAllowPartial());
        }

        public void allowPartial(String v) {
            r.rqAllowPartial(v);
        }

        public String fullTermTranche() {
            return strip(r.rqFullTermTranche());
        }

        public void fullTermTranche(String v) {
            r.rqFullTermTranche(v);
        }

        public boolean isIsLoanRequest() {
            return r.rqRecType().equals("LR");
        }

        public void setIsLoanRequest() {
            r.rqRecType("LR");
        }
    }

    private static String strip(String s) {
        int n = s.length();
        while (n > 0 && s.charAt(n - 1) == ' ') n--;
        return s.substring(0, n);
    }
}
