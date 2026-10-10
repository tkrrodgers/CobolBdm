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

    public static BigDecimal numval(Object o) {
        String s = new String(CobRt.alnumBytes(o, 0), CobRt.L1).trim();
        return Edit.deEdit(s.getBytes(CobRt.L1), 0);
    }

    public static BigDecimal numvalC(Object o) { return numval(o); }

    public static BigDecimal length(Object o) { return BigDecimal.valueOf(CobRt.sizeOf(o)); }

    public static BigDecimal integerOfDate(BigDecimal yyyymmdd) {
        int v = yyyymmdd.intValue();
        LocalDate d = LocalDate.of(v / 10000, (v / 100) % 100, v % 100);
        return BigDecimal.valueOf(ChronoUnit.DAYS.between(BASE, d));
    }

    public static BigDecimal dateOfInteger(BigDecimal n) {
        LocalDate d = BASE.plusDays(n.longValue());
        return BigDecimal.valueOf(d.getYear() * 10000L + d.getMonthValue() * 100L + d.getDayOfMonth());
    }

    public static String upperCase(Object o) { return new String(CobRt.alnumBytes(o, 0), CobRt.L1).toUpperCase(); }

    public static String lowerCase(Object o) { return new String(CobRt.alnumBytes(o, 0), CobRt.L1).toLowerCase(); }

    public static String trim(Object o) { return new String(CobRt.alnumBytes(o, 0), CobRt.L1).strip(); }

    public static String reverse(Object o) { return new StringBuilder(new String(CobRt.alnumBytes(o, 0), CobRt.L1)).reverse().toString(); }
}
