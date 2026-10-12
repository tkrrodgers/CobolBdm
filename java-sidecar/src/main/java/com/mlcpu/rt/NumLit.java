package com.mlcpu.rt;

import java.math.BigDecimal;

/** A numeric literal: its value and its source text (DISPLAY shows the digits as written). */
public final class NumLit {
    public final BigDecimal v;
    public final String text;

    /** The value as an unscaled long at {@link #scale}, or {@link CobField#NO_LONG} when it does not fit. */
    final long unscaled;
    final int scale;

    public NumLit(String text) {
        this.text = text;
        this.v = new BigDecimal(text.startsWith("+") ? text.substring(1) : text);
        this.scale = v.scale();
        this.unscaled = longOf(v);
    }

    public NumLit(BigDecimal v) {
        this.v = v;
        this.text = v.toPlainString();
        this.scale = v.scale();
        this.unscaled = longOf(v);
    }

    private static long longOf(BigDecimal v) {
        java.math.BigInteger u = v.unscaledValue();
        return u.bitLength() <= 62 ? u.longValue() : CobField.NO_LONG;
    }

    private String digits;

    /** Digits without sign or decimal point (alphanumeric use of an integer literal); computed once (every MOVE of the literal asks). */
    public String digits() {
        String d = digits;
        return d != null ? d : (digits = text.replace("+", "").replace("-", "").replace(".", ""));
    }

    @Override
    public String toString() {
        return text;
    }
}
