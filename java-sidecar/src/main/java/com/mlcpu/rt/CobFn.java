package com.mlcpu.rt;

import java.math.BigDecimal;
import java.math.RoundingMode;
import java.time.LocalDate;
import java.time.temporal.ChronoUnit;

/** Intrinsic functions. Numeric functions return BigDecimal; alphanumeric functions return String. */
public final class CobFn {
    private CobFn() {}

    private static final LocalDate BASE = LocalDate.of(1600, 12, 31);

    public static String currentDate() { return CobClock.currentDate(); }

    public static BigDecimal mod(BigDecimal a, BigDecimal b) {
        BigDecimal q = a.divide(b, 0, RoundingMode.FLOOR);
        return a.subtract(b.multiply(q));
    }

    public static BigDecimal rem(BigDecimal a, BigDecimal b) {
        BigDecimal q = a.divide(b, 0, RoundingMode.DOWN);
        return a.subtract(b.multiply(q));
    }

    public static BigDecimal integer(BigDecimal a) { return a.setScale(0, RoundingMode.FLOOR); }

    public static BigDecimal integerPart(BigDecimal a) { return a.setScale(0, RoundingMode.DOWN); }

    public static BigDecimal abs(BigDecimal a) { return a.abs(); }

    public static BigDecimal max(BigDecimal... a) {
        BigDecimal m = a[0];
        for (BigDecimal x : a) if (x.compareTo(m) > 0) m = x;
        return m;
    }

    public static BigDecimal min(BigDecimal... a) {
        BigDecimal m = a[0];
        for (BigDecimal x : a) if (x.compareTo(m) < 0) m = x;
        return m;
    }

    /**
     * NUMVAL / NUMVAL-C as GnuCOBOL 3.3 evaluates them, valid or not: digits accumulate left to right; the first '+'
     * or '-' decides the sign (a later one is ignored); "CR" or "DB" (any case) makes the value negative and ends the
     * scan; the first decimal point starts the fraction (later ones are ignored); every other character (spaces,
     * currency, commas, letters) is skipped. So "12A3" is 123 and "1.2.3" is 1.23, as the oracle returns.
     */
    public static BigDecimal numval(Object o) {
        byte[] b = CobRt.alnumBytes(o, 0);
        java.math.BigInteger acc = java.math.BigInteger.ZERO;
        int frac = 0;
        boolean point = false, signSeen = false, neg = false;
        for (int i = 0; i < b.length; i++) {
            int c = b[i] & 0xFF;
            if (i + 1 < b.length) {
                int d = Character.toUpperCase(b[i + 1] & 0xFF), u = Character.toUpperCase(c);
                if ((u == 'C' && d == 'R') || (u == 'D' && d == 'B')) { neg = true; break; }
            }
            if (c == '+' || c == '-') {
                if (!signSeen) { signSeen = true; neg = c == '-'; }
                continue;
            }
            if (c == '.') { point = true; continue; }
            if (c >= '0' && c <= '9') {
                acc = acc.multiply(java.math.BigInteger.TEN).add(java.math.BigInteger.valueOf(c - '0'));
                if (point) frac++;
            }
        }
        BigDecimal v = new BigDecimal(acc, frac);
        return neg ? v.negate() : v;
    }

    public static BigDecimal numvalC(Object o) { return numval(o); }

    public static BigDecimal numvalC(Object o, Object currency) { return numval(o); }

    /**
     * TEST-NUMVAL / TEST-NUMVAL-C: 0 when the argument is a valid NUMVAL (NUMVAL-C) argument, else the 1-based position
     * of the first character that cannot be accepted, or its length + 1 when it ends too early. Valid:
     * [sp][+|-][sp][cur][sp]number[sp] or [sp][cur][sp]number[sp][+|-|CR|DB][sp], number = digits[.[digits]] | .digits;
     * NUMVAL-C also takes the currency sign and commas between integer digits. CR and DB must be upper case.
     */
    public static BigDecimal testNumval(Object o, boolean c) {
        byte[] b = CobRt.alnumBytes(o, 0);
        int n = b.length, i = 0;
        boolean lead = false;
        while (i < n && b[i] == ' ') i++;
        if (i < n && (b[i] == '+' || b[i] == '-')) { lead = true; i++; while (i < n && b[i] == ' ') i++; }
        if (c && i < n && b[i] == '$') {
            i++;
            while (i < n && b[i] == ' ') i++;
            // NUMVAL-C also takes the sign after the currency sign
            if (!lead && i < n && (b[i] == '+' || b[i] == '-')) { lead = true; i++; while (i < n && b[i] == ' ') i++; }
        }
        int digits = 0;
        boolean point = false;
        while (i < n) {
            int ch = b[i] & 0xFF;
            if (ch >= '0' && ch <= '9') { digits++; i++; continue; }
            if (ch == '.' && !point) { point = true; i++; continue; }
            // NUMVAL-C: a comma groups integer digits, right after a digit
            if (c && ch == ',' && !point && i > 0 && b[i - 1] >= '0' && b[i - 1] <= '9') { i++; continue; }
            break;
        }
        if (digits == 0) return BigDecimal.valueOf(i < n ? i + 1 : n + 1);
        while (i < n && b[i] == ' ') i++;
        if (i < n && !lead) {
            if (b[i] == '+' || b[i] == '-') i++;
            else if (b[i] == 'C' || b[i] == 'D') {
                // CR / DB: a C or D not followed by its R or B is reported at the following position
                if (i + 1 < n && b[i + 1] == (b[i] == 'C' ? 'R' : 'B')) i += 2;
                else return BigDecimal.valueOf(i + 2);
            }
            while (i < n && b[i] == ' ') i++;
        }
        return BigDecimal.valueOf(i < n ? i + 1 : 0);
    }

    /** ORD: the ordinal position of the first character in the native collating sequence (byte value + 1). */
    public static BigDecimal ord(Object o) {
        byte[] b = CobRt.alnumBytes(o, 1);
        return BigDecimal.valueOf((b.length == 0 ? ' ' : b[0] & 0xFF) + 1);
    }

    /** CHAR: the character at ordinal position n (1..256). */
    public static String chr(BigDecimal n) {
        int v = n.intValue();
        if (v < 1 || v > 256) throw new CobRt.RuntimeCheck("FUNCTION CHAR (" + n.toPlainString() + ") outside 1..256");
        return String.valueOf((char) (v - 1));
    }

    /** ORD-MAX / ORD-MIN: the 1-based position of the first greatest (least) argument. */
    public static BigDecimal ordMax(BigDecimal... a) {
        int k = 0;
        for (int i = 1; i < a.length; i++) if (a[i].compareTo(a[k]) > 0) k = i;
        return BigDecimal.valueOf(k + 1);
    }

    public static BigDecimal ordMin(BigDecimal... a) {
        int k = 0;
        for (int i = 1; i < a.length; i++) if (a[i].compareTo(a[k]) < 0) k = i;
        return BigDecimal.valueOf(k + 1);
    }

    public static BigDecimal sum(BigDecimal... a) {
        BigDecimal t = BigDecimal.ZERO;
        for (BigDecimal x : a) t = t.add(x);
        return t;
    }

    /** MEAN: the sum divided by the count, with the division's 38 extra digits truncated (as COMPUTE). */
    public static BigDecimal mean(BigDecimal... a) { return CobRt.div(sum(a), BigDecimal.valueOf(a.length)); }

    /** MEDIAN: the middle value of the sorted arguments, or the mean of the two middle ones. */
    public static BigDecimal median(BigDecimal... a) {
        BigDecimal[] s = a.clone();
        java.util.Arrays.sort(s);
        int m = s.length / 2;
        return s.length % 2 == 1 ? s[m] : s[m - 1].add(s[m]).divide(BigDecimal.valueOf(2));
    }

    public static BigDecimal range(BigDecimal... a) { return max(a).subtract(min(a)); }

    /** SQRT to 100 significant digits, truncated; a negative argument stops the run. */
    public static BigDecimal sqrt(BigDecimal a) {
        if (a.signum() < 0) throw new CobRt.RuntimeCheck("FUNCTION SQRT of a negative argument");
        return a.sqrt(new java.math.MathContext(100, RoundingMode.DOWN));
    }

    /** FACTORIAL of an integer from 0; any other argument stops the run. */
    public static BigDecimal factorial(BigDecimal a) {
        if (a.signum() < 0 || a.stripTrailingZeros().scale() > 0) throw new CobRt.RuntimeCheck("FUNCTION FACTORIAL (" + a.toPlainString() + ")");
        java.math.BigInteger f = java.math.BigInteger.ONE;
        for (int i = 2; i <= a.intValueExact(); i++) f = f.multiply(java.math.BigInteger.valueOf(i));
        return new BigDecimal(f);
    }

    /** INTEGER-OF-DAY (YYYYDDD): days since 1600-12-31; an invalid argument returns 0, as GnuCOBOL does. */
    public static BigDecimal integerOfDay(BigDecimal yyyyddd) {
        long v = yyyyddd.longValue();
        int y = (int) (v / 1000), d = (int) (v % 1000);
        if (v < 0 || y < 1601 || y > 9999 || d < 1 || d > java.time.Year.of(y).length()) return BigDecimal.ZERO;
        return BigDecimal.valueOf(ChronoUnit.DAYS.between(BASE, LocalDate.ofYearDay(y, d)));
    }

    /** DAY-OF-INTEGER: the YYYYDDD date of a day number from 1 to 3,067,671; any other argument returns 0. */
    public static BigDecimal dayOfInteger(BigDecimal n) {
        long days = n.longValue();
        if (days < 1 || days > LAST_DAY) return BigDecimal.ZERO;
        LocalDate d = BASE.plusDays(days);
        return BigDecimal.valueOf(d.getYear() * 1000L + d.getDayOfYear());
    }

    /** TEST-DATE-YYYYMMDD: 0 for a valid date from 1601-01-01 to 9999-12-31, else 1 (year), 2 (month) or 3 (day). */
    public static BigDecimal testDateYyyymmdd(BigDecimal yyyymmdd) {
        long v = yyyymmdd.longValue();
        int y = (int) (v / 10000), m = (int) (v / 100 % 100), d = (int) (v % 100);
        if (v < 0 || y < 1601 || y > 9999) return BigDecimal.ONE;
        if (m < 1 || m > 12) return BigDecimal.valueOf(2);
        if (d < 1 || d > java.time.YearMonth.of(y, m).lengthOfMonth()) return BigDecimal.valueOf(3);
        return BigDecimal.ZERO;
    }

    /**
     * YEAR-TO-YYYY (yy [window [year]]): the year ending in yy within the 100 years that end at year + window (window
     * 50 and the current year by default). An argument out of range returns 0.
     */
    public static BigDecimal yearToYyyy(BigDecimal yy, BigDecimal window, BigDecimal year) {
        int y2 = yy.intValue();
        int w = window == null ? 50 : window.intValue();
        int cur = year == null ? CobClock.now().getYear() : year.intValue();
        if (y2 < 0 || y2 > 99 || w < -1699 || w > 8299 || cur < 1700 || cur > 9999) return BigDecimal.ZERO;
        int end = cur + w;
        if (end < 1700 || end > 9999) return BigDecimal.ZERO;
        int r = end / 100 * 100 + y2;
        if (r > end) r -= 100;
        return BigDecimal.valueOf(r);
    }

    /** DATE-TO-YYYYMMDD (yymmdd [window [year]]): the year expanded as YEAR-TO-YYYY. */
    public static BigDecimal dateToYyyymmdd(BigDecimal yymmdd, BigDecimal window, BigDecimal year) {
        long v = yymmdd.longValue();
        BigDecimal y = yearToYyyy(BigDecimal.valueOf(v / 10000), window, year);
        if (y.signum() == 0 || v < 0 || v > 999999) return BigDecimal.ZERO;
        return BigDecimal.valueOf(y.longValue() * 10000 + v % 10000);
    }

    /**
     * WHEN-COMPILED: pinned. The value of {@code mlcpu.whenCompiled} / {@code MLCPU_WHEN_COMPILED} when set (21
     * characters), else the time the program was transpiled, which the emitter writes into the class.
     */
    public static String whenCompiled(String transpiled) {
        String v = CobConfig.setting("mlcpu.whenCompiled", "MLCPU_WHEN_COMPILED", transpiled);
        return v.length() >= 21 ? v.substring(0, 21) : String.format("%-21s", v).replace(' ', '0');
    }

    public static BigDecimal length(Object o) { return BigDecimal.valueOf(CobRt.sizeOf(o)); }

    /** Day 3,067,671 is 9999-12-31, the last date the date functions accept. */
    private static final long LAST_DAY = 3_067_671L;

    /**
     * Days since 1600-12-31. An argument that is not a valid date from 1601-01-01 to 9999-12-31 returns 0, as GnuCOBOL
     * does (the standard's EC-ARGUMENT-FUNCTION, not a stop).
     */
    public static BigDecimal integerOfDate(BigDecimal yyyymmdd) {
        long v = yyyymmdd.longValue();
        int y = (int) (v / 10000), m = (int) (v / 100 % 100), d = (int) (v % 100);
        if (v < 0 || y < 1601 || y > 9999 || m < 1 || m > 12 || d < 1 || d > java.time.YearMonth.of(y, m).lengthOfMonth()) {
            return BigDecimal.ZERO;
        }
        return BigDecimal.valueOf(ChronoUnit.DAYS.between(BASE, LocalDate.of(y, m, d)));
    }

    /** The YYYYMMDD date of a day number from 1 to 3,067,671; any other argument returns 0, as GnuCOBOL does. */
    public static BigDecimal dateOfInteger(BigDecimal n) {
        long days = n.longValue();
        if (days < 1 || days > LAST_DAY) return BigDecimal.ZERO;
        LocalDate d = BASE.plusDays(days);
        return BigDecimal.valueOf(d.getYear() * 10000L + d.getMonthValue() * 100L + d.getDayOfMonth());
    }

    public static String upperCase(Object o) { return new String(CobRt.alnumBytes(o, 0), CobRt.L1).toUpperCase(); }

    public static String lowerCase(Object o) { return new String(CobRt.alnumBytes(o, 0), CobRt.L1).toLowerCase(); }

    public static String trim(Object o) { return new String(CobRt.alnumBytes(o, 0), CobRt.L1).strip(); }

    public static String reverse(Object o) { return new StringBuilder(new String(CobRt.alnumBytes(o, 0), CobRt.L1)).reverse().toString(); }
}
