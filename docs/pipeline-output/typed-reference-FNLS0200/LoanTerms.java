package com.mlcpu.capabilities.fnls0200;

import java.math.BigDecimal;
import com.mlcpu.capabilities.fnls0200.records.FnLoanTerms;

/**
 * A CALL argument of FNLS0200 (LINKAGE).
 * Typed view of FN-LOAN-TERMS (81 bytes); the bytes underneath are the record kit's FnLoanTerms, which
 * stores with the COBOL MOVE rules. Text getters drop trailing spaces; setters pad. Generated (typed-interface projection).
 */
public interface LoanTerms {
    /** A view over a CALL argument's bytes (no copy). */
    static LoanTerms over(byte[] bytes) {
        return new Bytes(FnLoanTerms.wrap(bytes));
    }

    /** A new area as WORKING-STORAGE starts: x'00' bytes, then the VALUE clauses. */
    static LoanTerms create() {
        return new Bytes(new FnLoanTerms((byte) 0));
    }

    /** The area itself (what a CALL passes BY REFERENCE). */
    byte[] bytes();

    /** INITIALIZE: text items to spaces, numeric items to zero. */
    void initialize();

    /** LT-CURRENCY-CODE PIC X(3) */
    String currencyCode();

    void currencyCode(String v);

    /** LT-DECIMAL-PLACES PIC S9(4) COMP */
    long decimalPlaces();

    void decimalPlaces(long v);

    void decimalPlaces(BigDecimal v);

    /** LT-MULTIPLES-IND PIC X */
    String multiplesInd();

    void multiplesInd(String v);

    /** LT-MULTIPLES PIC S9(9) COMP */
    long multiples();

    void multiples(long v);

    void multiples(BigDecimal v);

    /** LT-PRINCIPAL PIC S9(13)V9(4) COMP-3 */
    BigDecimal principal();

    void principal(BigDecimal v);

    void principal(long v);

    /** LT-SUBMITTED-DAY PIC S9(9) COMP */
    long submittedDay();

    void submittedDay(long v);

    void submittedDay(BigDecimal v);

    /** LT-EXP-DISB-DAY PIC S9(9) COMP */
    long expDisbDay();

    void expDisbDay(long v);

    void expDisbDay(BigDecimal v);

    /** LT-SEED-DAY PIC S9(9) COMP */
    long seedDay();

    void seedDay(long v);

    void seedDay(BigDecimal v);

    /** LT-NUM-REPAYMENTS PIC S9(9) COMP */
    long numRepayments();

    void numRepayments(long v);

    void numRepayments(BigDecimal v);

    /** LT-REPAY-EVERY PIC S9(9) COMP */
    long repayEvery();

    void repayEvery(long v);

    void repayEvery(BigDecimal v);

    /** LT-FREQ-TYPE PIC S9(4) COMP; its 88-level names as FreqType */
    FreqType freqType();

    void freqType(FreqType v);

    /** The value itself (any value, also one without an 88-level name). */
    long freqTypeCode();

    void freqTypeCode(long v);

    void freqTypeCode(BigDecimal v);

    /** LT-ANNUAL-RATE PIC S9(3)V9(9) COMP-3 */
    BigDecimal annualRate();

    void annualRate(BigDecimal v);

    void annualRate(long v);

    /** LT-DAYS-IN-MONTH PIC S9(4) COMP; its 88-level names as DaysInMonth */
    DaysInMonth daysInMonth();

    void daysInMonth(DaysInMonth v);

    /** The value itself (any value, also one without an 88-level name). */
    long daysInMonthCode();

    void daysInMonthCode(long v);

    void daysInMonthCode(BigDecimal v);

    /** LT-DAYS-IN-YEAR PIC S9(4) COMP; its 88-level names as DaysInYear */
    DaysInYear daysInYear();

    void daysInYear(DaysInYear v);

    /** The value itself (any value, also one without an 88-level name). */
    long daysInYearCode();

    void daysInYearCode(long v);

    void daysInYearCode(BigDecimal v);

    /** LT-INTEREST-METHOD PIC S9(4) COMP; its 88-level names as InterestMethod */
    InterestMethod interestMethod();

    void interestMethod(InterestMethod v);

    /** The value itself (any value, also one without an 88-level name). */
    long interestMethodCode();

    void interestMethodCode(long v);

    void interestMethodCode(BigDecimal v);

    /** LT-DOWNPAY-SW PIC X */
    String downpaySw();

    void downpaySw(String v);

    /** LT-DOWNPAY-PCT-IND PIC X */
    String downpayPctInd();

    void downpayPctInd(String v);

    /** LT-DOWNPAY-PCT PIC S9(3)V9(9) COMP-3 */
    BigDecimal downpayPct();

    void downpayPct(BigDecimal v);

    void downpayPct(long v);

    /** LT-DOWNPAY-AMOUNT PIC S9(13)V9(4) COMP-3 */
    BigDecimal downpayAmount();

    void downpayAmount(BigDecimal v);

    void downpayAmount(long v);

    /** LT-FIXED-LENGTH-IND PIC X */
    String fixedLengthInd();

    void fixedLengthInd(String v);

    /** LT-FIXED-LENGTH PIC S9(9) COMP */
    long fixedLength();

    void fixedLength(long v);

    void fixedLength(BigDecimal v);

    /** LT-FULL-TERM-SW PIC X */
    String fullTermSw();

    void fullTermSw(String v);

    /** LT-LCD-INT-RECOG-SW PIC X */
    String lcdIntRecogSw();

    void lcdIntRecogSw(String v);

    /** LT-INT-RECOG-SW PIC X */
    String intRecogSw();

    void intRecogSw(String v);

    /** LT-PARTIAL-SW PIC X */
    String partialSw();

    void partialSw(String v);

    /** 88 LT-MULTIPLES-PRESENT */
    boolean isMultiplesPresent();

    void setMultiplesPresent();

    /** 88 LT-DOWNPAY-ENABLED */
    boolean isDownpayEnabled();

    void setDownpayEnabled();

    /** 88 LT-DOWNPAY-PCT-PRESENT */
    boolean isDownpayPctPresent();

    void setDownpayPctPresent();

    /** 88 LT-FIXED-LENGTH-PRESENT */
    boolean isFixedLengthPresent();

    void setFixedLengthPresent();

    /** 88 LT-FULL-TERM-TRANCHE */
    boolean isFullTermTranche();

    void setFullTermTranche();

    /** 88 LT-LCD-INT-RECOG-ON-DISB */
    boolean isLcdIntRecogOnDisb();

    void setLcdIntRecogOnDisb();

    /** The 88-level names of LT-FREQ-TYPE. */
    enum FreqType {
        /** 88 LT-FREQ-DAYS */
        DAYS(0L),
        /** 88 LT-FREQ-WEEKS */
        WEEKS(1L),
        /** 88 LT-FREQ-MONTHS */
        MONTHS(2L),
        /** 88 LT-FREQ-YEARS */
        YEARS(3L),
        /** 88 LT-FREQ-WHOLE-TERM */
        WHOLE_TERM(4L),
        /** 88 LT-FREQ-INVALID */
        INVALID(5L);

        public final long code;

        FreqType(long code) {
            this.code = code;
        }

        /** The constant for a value, or null when no 88-level name has it. */
        public static FreqType of(long code) {
            for (FreqType x : values()) if (x.code == code) return x;
            return null;
        }
    }

    /** The 88-level names of LT-DAYS-IN-MONTH. */
    enum DaysInMonth {
        /** 88 LT-DIM-INVALID */
        INVALID(0L),
        /** 88 LT-DIM-ACTUAL */
        ACTUAL(1L),
        /** 88 LT-DIM-30 */
        V30(30L);

        public final long code;

        DaysInMonth(long code) {
            this.code = code;
        }

        /** The constant for a value, or null when no 88-level name has it. */
        public static DaysInMonth of(long code) {
            for (DaysInMonth x : values()) if (x.code == code) return x;
            return null;
        }
    }

    /** The 88-level names of LT-DAYS-IN-YEAR. */
    enum DaysInYear {
        /** 88 LT-DIY-INVALID */
        INVALID(0L),
        /** 88 LT-DIY-ACTUAL */
        ACTUAL(1L),
        /** 88 LT-DIY-360 */
        V360(360L),
        /** 88 LT-DIY-364 */
        V364(364L),
        /** 88 LT-DIY-365 */
        V365(365L);

        public final long code;

        DaysInYear(long code) {
            this.code = code;
        }

        /** The constant for a value, or null when no 88-level name has it. */
        public static DaysInYear of(long code) {
            for (DaysInYear x : values()) if (x.code == code) return x;
            return null;
        }
    }

    /** The 88-level names of LT-INTEREST-METHOD. */
    enum InterestMethod {
        /** 88 LT-DECLINING-BALANCE */
        DECLINING_BALANCE(0L),
        /** 88 LT-FLAT */
        FLAT(1L),
        /** 88 LT-METHOD-INVALID */
        METHOD_INVALID(2L);

        public final long code;

        InterestMethod(long code) {
            this.code = code;
        }

        /** The constant for a value, or null when no 88-level name has it. */
        public static InterestMethod of(long code) {
            for (InterestMethod x : values()) if (x.code == code) return x;
            return null;
        }
    }

    /** The record kit's FnLoanTerms behind the view. */
    final class Bytes implements LoanTerms {
        private final FnLoanTerms r;

        Bytes(FnLoanTerms r) {
            this.r = r;
        }

        public byte[] bytes() {
            return r.bytes();
        }

        public void initialize() {
            r.initialize();
        }

        public String currencyCode() {
            return strip(r.ltCurrencyCode());
        }

        public void currencyCode(String v) {
            r.ltCurrencyCode(v);
        }

        public long decimalPlaces() {
            return r.ltDecimalPlaces();
        }

        public void decimalPlaces(long v) {
            r.ltDecimalPlaces(v);
        }

        public void decimalPlaces(BigDecimal v) {
            r.ltDecimalPlaces(v);
        }

        public String multiplesInd() {
            return strip(r.ltMultiplesInd());
        }

        public void multiplesInd(String v) {
            r.ltMultiplesInd(v);
        }

        public long multiples() {
            return r.ltMultiples();
        }

        public void multiples(long v) {
            r.ltMultiples(v);
        }

        public void multiples(BigDecimal v) {
            r.ltMultiples(v);
        }

        public BigDecimal principal() {
            return r.ltPrincipal();
        }

        public void principal(BigDecimal v) {
            r.ltPrincipal(v);
        }

        public void principal(long v) {
            r.ltPrincipal(v);
        }

        public long submittedDay() {
            return r.ltSubmittedDay();
        }

        public void submittedDay(long v) {
            r.ltSubmittedDay(v);
        }

        public void submittedDay(BigDecimal v) {
            r.ltSubmittedDay(v);
        }

        public long expDisbDay() {
            return r.ltExpDisbDay();
        }

        public void expDisbDay(long v) {
            r.ltExpDisbDay(v);
        }

        public void expDisbDay(BigDecimal v) {
            r.ltExpDisbDay(v);
        }

        public long seedDay() {
            return r.ltSeedDay();
        }

        public void seedDay(long v) {
            r.ltSeedDay(v);
        }

        public void seedDay(BigDecimal v) {
            r.ltSeedDay(v);
        }

        public long numRepayments() {
            return r.ltNumRepayments();
        }

        public void numRepayments(long v) {
            r.ltNumRepayments(v);
        }

        public void numRepayments(BigDecimal v) {
            r.ltNumRepayments(v);
        }

        public long repayEvery() {
            return r.ltRepayEvery();
        }

        public void repayEvery(long v) {
            r.ltRepayEvery(v);
        }

        public void repayEvery(BigDecimal v) {
            r.ltRepayEvery(v);
        }

        public FreqType freqType() {
            return FreqType.of(r.ltFreqType());
        }

        public void freqType(FreqType v) {
            r.ltFreqType(v.code);
        }

        public long freqTypeCode() {
            return r.ltFreqType();
        }

        public void freqTypeCode(long v) {
            r.ltFreqType(v);
        }

        public void freqTypeCode(BigDecimal v) {
            r.ltFreqType(v);
        }

        public BigDecimal annualRate() {
            return r.ltAnnualRate();
        }

        public void annualRate(BigDecimal v) {
            r.ltAnnualRate(v);
        }

        public void annualRate(long v) {
            r.ltAnnualRate(v);
        }

        public DaysInMonth daysInMonth() {
            return DaysInMonth.of(r.ltDaysInMonth());
        }

        public void daysInMonth(DaysInMonth v) {
            r.ltDaysInMonth(v.code);
        }

        public long daysInMonthCode() {
            return r.ltDaysInMonth();
        }

        public void daysInMonthCode(long v) {
            r.ltDaysInMonth(v);
        }

        public void daysInMonthCode(BigDecimal v) {
            r.ltDaysInMonth(v);
        }

        public DaysInYear daysInYear() {
            return DaysInYear.of(r.ltDaysInYear());
        }

        public void daysInYear(DaysInYear v) {
            r.ltDaysInYear(v.code);
        }

        public long daysInYearCode() {
            return r.ltDaysInYear();
        }

        public void daysInYearCode(long v) {
            r.ltDaysInYear(v);
        }

        public void daysInYearCode(BigDecimal v) {
            r.ltDaysInYear(v);
        }

        public InterestMethod interestMethod() {
            return InterestMethod.of(r.ltInterestMethod());
        }

        public void interestMethod(InterestMethod v) {
            r.ltInterestMethod(v.code);
        }

        public long interestMethodCode() {
            return r.ltInterestMethod();
        }

        public void interestMethodCode(long v) {
            r.ltInterestMethod(v);
        }

        public void interestMethodCode(BigDecimal v) {
            r.ltInterestMethod(v);
        }

        public String downpaySw() {
            return strip(r.ltDownpaySw());
        }

        public void downpaySw(String v) {
            r.ltDownpaySw(v);
        }

        public String downpayPctInd() {
            return strip(r.ltDownpayPctInd());
        }

        public void downpayPctInd(String v) {
            r.ltDownpayPctInd(v);
        }

        public BigDecimal downpayPct() {
            return r.ltDownpayPct();
        }

        public void downpayPct(BigDecimal v) {
            r.ltDownpayPct(v);
        }

        public void downpayPct(long v) {
            r.ltDownpayPct(v);
        }

        public BigDecimal downpayAmount() {
            return r.ltDownpayAmount();
        }

        public void downpayAmount(BigDecimal v) {
            r.ltDownpayAmount(v);
        }

        public void downpayAmount(long v) {
            r.ltDownpayAmount(v);
        }

        public String fixedLengthInd() {
            return strip(r.ltFixedLengthInd());
        }

        public void fixedLengthInd(String v) {
            r.ltFixedLengthInd(v);
        }

        public long fixedLength() {
            return r.ltFixedLength();
        }

        public void fixedLength(long v) {
            r.ltFixedLength(v);
        }

        public void fixedLength(BigDecimal v) {
            r.ltFixedLength(v);
        }

        public String fullTermSw() {
            return strip(r.ltFullTermSw());
        }

        public void fullTermSw(String v) {
            r.ltFullTermSw(v);
        }

        public String lcdIntRecogSw() {
            return strip(r.ltLcdIntRecogSw());
        }

        public void lcdIntRecogSw(String v) {
            r.ltLcdIntRecogSw(v);
        }

        public String intRecogSw() {
            return strip(r.ltIntRecogSw());
        }

        public void intRecogSw(String v) {
            r.ltIntRecogSw(v);
        }

        public String partialSw() {
            return strip(r.ltPartialSw());
        }

        public void partialSw(String v) {
            r.ltPartialSw(v);
        }

        public boolean isMultiplesPresent() {
            return r.ltMultiplesInd().equals("Y");
        }

        public void setMultiplesPresent() {
            r.ltMultiplesInd("Y");
        }

        public boolean isDownpayEnabled() {
            return r.ltDownpaySw().equals("Y");
        }

        public void setDownpayEnabled() {
            r.ltDownpaySw("Y");
        }

        public boolean isDownpayPctPresent() {
            return r.ltDownpayPctInd().equals("Y");
        }

        public void setDownpayPctPresent() {
            r.ltDownpayPctInd("Y");
        }

        public boolean isFixedLengthPresent() {
            return r.ltFixedLengthInd().equals("Y");
        }

        public void setFixedLengthPresent() {
            r.ltFixedLengthInd("Y");
        }

        public boolean isFullTermTranche() {
            return r.ltFullTermSw().equals("Y");
        }

        public void setFullTermTranche() {
            r.ltFullTermSw("Y");
        }

        public boolean isLcdIntRecogOnDisb() {
            return r.ltLcdIntRecogSw().equals("Y");
        }

        public void setLcdIntRecogOnDisb() {
            r.ltLcdIntRecogSw("Y");
        }
    }

    private static String strip(String s) {
        int n = s.length();
        while (n > 0 && s.charAt(n - 1) == ' ') n--;
        return s.substring(0, n);
    }
}
