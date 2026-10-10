package com.mlcpu.rt;

import java.math.BigDecimal;

/** A numeric literal: its value and its source text (DISPLAY shows the digits as written). */
public final class NumLit {
    public final BigDecimal v;
    public final String text;

    public NumLit(String text) {
        this.text = text;
        this.v = new BigDecimal(text.startsWith("+") ? text.substring(1) : text);
    }

    public NumLit(BigDecimal v) {
        this.v = v;
        this.text = v.toPlainString();
    }

    /** Digits without sign or decimal point (alphanumeric use of an integer literal). */
    public String digits() {
        return text.replace("+", "").replace("-", "").replace(".", "");
    }

    @Override
    public String toString() {
        return text;
    }
}
