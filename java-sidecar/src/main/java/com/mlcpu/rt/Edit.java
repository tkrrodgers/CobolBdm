package com.mlcpu.rt;

import java.math.BigDecimal;
import java.math.RoundingMode;

/**
 * Numeric editing per the COBOL standard: fixed insertion (+ - $ CR DB), floating insertion (+ - $ strings),
 * zero suppression (Z, *), simple insertion (B 0 / ,) and the special insertion point. Floating strings
 * are rendered as floating (fixes defect D5); an all-Z/* picture with a zero value is blank (or asterisks).
 */
public final class Edit {
    private Edit() {}

    /** Expand repeat factors: Z(3)9 → ZZZ9. */
    public static String expand(String pic) {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < pic.length(); i++) {
            char c = pic.charAt(i);
            if (c == '(' ) {
                int close = pic.indexOf(')', i);
                int n = Integer.parseInt(pic.substring(i + 1, close));
                char prev = sb.charAt(sb.length() - 1);
                for (int k = 1; k < n; k++) sb.append(prev);
                i = close;
            } else sb.append(Character.toUpperCase(c));
        }
        return sb.toString();
    }

    public static String format(BigDecimal value, String pic, int scale, boolean blankWhenZero) {
        String p = pic;
        int n = p.length();
        // Locate the floating insertion string (two or more of + - $ at the start of the numeric part).
        char floatCh = 0;
        int floatFirst = -1, floatLast = -1;
        for (int i = 0; i < n; i++) {
            char c = p.charAt(i);
            if (c == '+' || c == '-' || c == '$') {
                int count = 0, j = i, last = i;
                while (j < n && (p.charAt(j) == c || ((p.charAt(j) == ',' || p.charAt(j) == 'B' || p.charAt(j) == '0' || p.charAt(j) == '/') && j + 1 < n && p.charAt(j + 1) == c))) {
                    if (p.charAt(j) == c) { count++; last = j; }
                    j++;
                }
                if (count >= 2) { floatCh = c; floatFirst = i; floatLast = last; }
                break;
            }
            if (c == '9' || c == 'Z' || c == '*' || c == '.' || c == 'V') break;
        }
        // Count integer and fraction digit positions.
        int point = -1;
        for (int i = 0; i < n; i++) if (p.charAt(i) == '.' || p.charAt(i) == 'V') { point = i; break; }
        int intPos = 0, fracPos = 0;
        boolean anyNine = false;
        for (int i = 0; i < n; i++) {
            char c = p.charAt(i);
            boolean digitPos = c == '9' || c == 'Z' || c == '*' || (floatCh != 0 && c == floatCh && i > floatFirst && i <= floatLast);
            if (c == '9') anyNine = true;
            if (!digitPos) continue;
            if (point >= 0 && i > point) fracPos++; else intPos++;
        }
        boolean neg = value.signum() < 0;
        BigDecimal abs = value.abs().setScale(fracPos, RoundingMode.DOWN);
        String digits = abs.unscaledValue().toString();
        int total = intPos + fracPos;
        if (digits.length() > total) digits = digits.substring(digits.length() - total);
        while (digits.length() < total) digits = "0" + digits;
        boolean isZero = digits.chars().allMatch(ch -> ch == '0');

        if (blankWhenZero && isZero) return " ".repeat(lengthOf(p));

        char suppress = p.indexOf('*') >= 0 ? '*' : ' ';
        boolean allSuppress = !anyNine && (p.indexOf('Z') >= 0 || p.indexOf('*') >= 0 || floatCh != 0);
        if (allSuppress && isZero) {
            if (suppress == '*') {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < n; i++) {
                    char c = p.charAt(i);
                    if (c == 'V') continue;
                    sb.append(c == '.' ? '.' : '*');
                }
                return sb.toString();
            }
            return " ".repeat(lengthOf(p));
        }

        StringBuilder out = new StringBuilder();
        int di = 0;
        boolean started = false;
        for (int i = 0; i < n; i++) {
            char c = p.charAt(i);
            if (c == 'V') continue;
            if (floatCh != 0 && i >= floatFirst && i <= floatLast) {
                if (i == floatFirst) { out.append(' '); continue; }
                if (c != floatCh) { out.append(started ? c == 'B' ? ' ' : c : ' '); continue; }
                char d = digits.charAt(di++);
                if (!started && d == '0') { out.append(' '); continue; }
                if (!started) { out.setCharAt(out.length() - 1, floatSymbol(floatCh, neg)); started = true; }
                out.append(d);
                continue;
            }
            switch (c) {
                case '9' -> {
                    if (!started && floatCh != 0 && i > floatLast) { out.setCharAt(out.length() - 1, floatSymbol(floatCh, neg)); }
                    started = true;
                    out.append(digits.charAt(di++));
                }
                case 'Z', '*' -> {
                    char d = digits.charAt(di++);
                    if (!started && d == '0' && (point < 0 || i < point)) out.append(c == '*' ? '*' : ' ');
                    else { started = true; out.append(d); }
                }
                case '.' -> {
                    if (!started && floatCh != 0 && i > floatLast) { out.setCharAt(out.length() - 1, floatSymbol(floatCh, neg)); }
                    started = true;
                    out.append('.');
                }
                case ',' -> out.append(started ? ',' : suppress);
                case 'B' -> out.append(started ? ' ' : suppress);
                case '0' -> out.append(started ? '0' : suppress);
                case '/' -> out.append(started ? '/' : suppress);
                case '+' -> out.append(neg ? '-' : '+');
                case '-' -> out.append(neg ? '-' : ' ');
                case '$' -> out.append('$');
                case 'C' -> { out.append(neg ? "CR" : "  "); i++; }
                case 'D' -> { out.append(neg ? "DB" : "  "); i++; }
                default -> out.append(c);
            }
        }
        return out.toString();
    }

    private static char floatSymbol(char f, boolean neg) {
        return switch (f) {
            case '+' -> neg ? '-' : '+';
            case '-' -> neg ? '-' : ' ';
            default -> f;
        };
    }

    private static int lengthOf(String p) {
        int n = 0;
        for (int i = 0; i < p.length(); i++) if (p.charAt(i) != 'V') n++;
        return n;
    }

    /**
     * De-edit: digits, the decimal point and the sign (leading or trailing '-', CR, DB) form the value.
     * A trailing sign makes the value negative (fixes defect D8).
     */
    public static BigDecimal deEdit(byte[] b, int scale) {
        StringBuilder digits = new StringBuilder();
        boolean neg = false;
        int frac = -1;
        String s = new String(b, java.nio.charset.StandardCharsets.ISO_8859_1);
        String upper = s.toUpperCase();
        if (upper.trim().endsWith("CR") || upper.trim().endsWith("DB")) neg = true;
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            if (c >= '0' && c <= '9') { digits.append(c); if (frac >= 0) frac++; }
            else if (c == '.') frac = 0;
            else if (c == '-') neg = true;
        }
        if (digits.length() == 0) return BigDecimal.ZERO;
        BigDecimal v = new BigDecimal(new java.math.BigInteger(digits.toString()), Math.max(frac, 0));
        if (frac < 0 && scale > 0) v = v.setScale(0);
        return neg ? v.negate() : v;
    }
}
