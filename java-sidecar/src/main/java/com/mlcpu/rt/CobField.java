package com.mlcpu.rt;

import java.math.BigDecimal;
import java.math.BigInteger;
import java.math.RoundingMode;

/**
 * A view of one data item over an {@link Area}. The record bytes are authoritative (I9): every read decodes the
 * bytes under the item's usage and every write encodes them, so REDEFINES, group moves and file images are exact.
 */
public final class CobField {
    public enum Cat { GROUP, ALNUM, ALPHA, NUMERIC, NUM_EDITED, ALNUM_EDITED, INDEX, POINTER }
    public enum Use { DISPLAY, PACKED, BINARY, COMP5, FLOAT, DOUBLE, INDEX, POINTER }

    public final String name;
    public final Cat cat;
    public final Use use;
    public final Area area;
    public final int off;
    public final int len;
    public final int digits;
    public final int scale;
    public final boolean signed;
    public final boolean signSep;
    public final boolean signLead;
    public final boolean just;
    public final boolean bwz;
    /** Expanded PICTURE for edited items; null otherwise. */
    public final String pic;
    final int[] counts;
    final int[] strides;

    public CobField(String name, Cat cat, Use use, Area area, int off, int len, int digits, int scale, boolean signed,
                    boolean signSep, boolean signLead, boolean just, boolean bwz, String pic, int[] counts, int[] strides) {
        this.name = name;
        this.cat = cat;
        this.use = use;
        this.area = area;
        this.off = off;
        this.len = len;
        this.digits = digits;
        this.scale = scale;
        this.signed = signed;
        this.signSep = signSep;
        this.signLead = signLead;
        this.just = just;
        this.bwz = bwz;
        this.pic = pic;
        this.counts = counts;
        this.strides = strides;
    }

    public static CobField group(String name, Area a, int off, int len, int[] counts, int[] strides) {
        return new CobField(name, Cat.GROUP, Use.DISPLAY, a, off, len, 0, 0, false, false, false, false, false, null, counts, strides);
    }

    public static CobField alnum(String name, Area a, int off, int len, boolean just, int[] counts, int[] strides) {
        return new CobField(name, Cat.ALNUM, Use.DISPLAY, a, off, len, 0, 0, false, false, false, just, false, null, counts, strides);
    }

    public static CobField num(String name, Use use, Area a, int off, int len, int digits, int scale, boolean signed,
                               boolean signSep, boolean signLead, int[] counts, int[] strides) {
        return new CobField(name, Cat.NUMERIC, use, a, off, len, digits, scale, signed, signSep, signLead, false, false, null, counts, strides);
    }

    public static CobField edited(String name, Cat cat, Area a, int off, int len, int digits, int scale, boolean bwz, String pic,
                                  int[] counts, int[] strides) {
        return new CobField(name, cat, Use.DISPLAY, a, off, len, digits, scale, false, false, false, false, bwz, pic, counts, strides);
    }

    /** An anonymous alphanumeric view (reference modification, function results). */
    public static CobField temp(byte[] data) {
        Area a = new Area(0);
        a.b = data;
        return new CobField("", Cat.ALNUM, Use.DISPLAY, a, 0, data.length, 0, 0, false, false, false, false, false, null, null, null);
    }

    public byte[] buf() { return area.b; }
    public int pos() { return area.base + off; }

    /** This field's storage with another field's attributes (BY CONTENT copies keep the sender's description). */
    public CobField withShape(CobField f) {
        return new CobField(f.name, f.cat, f.use, area, off, f.len, f.digits, f.scale, f.signed, f.signSep, f.signLead, f.just, f.bwz, f.pic, null, null);
    }

    public boolean isNumeric() { return cat == Cat.NUMERIC || cat == Cat.INDEX; }
    public boolean isGroup() { return cat == Cat.GROUP; }

    // ------------------------------------------------------------------ subscripts and reference modification

    /** The occurrence at 1-based subscripts (outermost first). Out-of-range subscripts are not checked (IBM SSRANGE off). */
    public CobField at(int... subs) {
        if (counts == null || subs.length == 0) return this;
        int o = off;
        int first = counts.length - subs.length;
        if (first < 0) throw new IllegalArgumentException(name + ": too many subscripts");
        for (int i = 0; i < subs.length; i++) o += (subs[i] - 1) * strides[first + i];
        return new CobField(name, cat, use, area, o, len, digits, scale, signed, signSep, signLead, just, bwz, pic, null, null);
    }

    /** Reference modification (1-based start, optional length) — always an alphanumeric view. */
    public CobField ref(int start, int length) {
        if (start < 1 || start > len) throw new CobRt.RuntimeCheck(name + ": reference modification start " + start + " out of range 1.." + len);
        int l = length < 0 ? len - start + 1 : length;
        if (l < 1 || start - 1 + l > len) throw new CobRt.RuntimeCheck(name + ": reference modification length " + l + " out of range");
        return new CobField(name, Cat.ALNUM, Use.DISPLAY, area, off + start - 1, l, 0, 0, false, false, false, false, false, null, null, null);
    }

    // ------------------------------------------------------------------ raw bytes

    public byte[] bytes() {
        byte[] r = new byte[len];
        System.arraycopy(area.b, pos(), r, 0, len);
        return r;
    }

    public void setBytes(byte[] src) {
        int n = Math.min(src.length, len);
        System.arraycopy(src, 0, area.b, pos(), n);
        if (n < len) java.util.Arrays.fill(area.b, pos() + n, pos() + len, (byte) ' ');
    }

    public void fill(byte v) {
        java.util.Arrays.fill(area.b, pos(), pos() + len, v);
    }

    public String str() {
        return new String(area.b, pos(), len, java.nio.charset.StandardCharsets.ISO_8859_1);
    }

    // ------------------------------------------------------------------ numeric decode

    public BigDecimal dec() {
        return switch (use) {
            case PACKED -> decPacked();
            case BINARY, COMP5, INDEX, POINTER -> decBinary();
            case FLOAT, DOUBLE -> throw new CobRt.RuntimeCheck(name + ": COMP-1/COMP-2 are not supported");
            default -> cat == Cat.NUMERIC || cat == Cat.INDEX ? decDisplay() : Edit.deEdit(bytes(), scale);
        };
    }

    public int intValue() {
        return dec().intValue();
    }

    private BigDecimal decDisplay() {
        byte[] b = area.b;
        int p = pos();
        int n = len;
        boolean neg = false;
        int dStart = p, dEnd = p + n;
        if (signed && signSep) {
            byte s = signLead ? b[p] : b[p + n - 1];
            neg = s == '-';
            if (signLead) dStart++; else dEnd--;
        }
        long acc = 0;
        BigInteger big = null;
        int count = 0;
        for (int i = dStart; i < dEnd; i++) {
            int c = b[i] & 0xFF;
            int d;
            boolean signPos = signed && !signSep && (signLead ? i == dStart : i == dEnd - 1);
            if (signPos) {
                int[] ds = Overpunch.decode(c);
                d = ds[0];
                if (ds[1] < 0) neg = true;
            } else {
                d = c & 0x0F;
                if (d > 9) d = 0;
            }
            if (count < 18) acc = acc * 10 + d;
            else {
                if (big == null) big = BigInteger.valueOf(acc);
                big = big.multiply(BigInteger.TEN).add(BigInteger.valueOf(d));
            }
            count++;
        }
        BigInteger unscaled = big != null ? big : BigInteger.valueOf(acc);
        if (neg) unscaled = unscaled.negate();
        return new BigDecimal(unscaled, scale);
    }

    private BigDecimal decPacked() {
        byte[] b = area.b;
        int p = pos();
        long acc = 0;
        BigInteger big = null;
        int count = 0;
        for (int i = 0; i < len; i++) {
            int v = b[p + i] & 0xFF;
            int hi = v >> 4, lo = v & 0x0F;
            for (int k = 0; k < (i == len - 1 ? 1 : 2); k++) {
                int d = k == 0 ? hi : lo;
                if (d > 9) d = 0;
                if (count < 18) acc = acc * 10 + d;
                else {
                    if (big == null) big = BigInteger.valueOf(acc);
                    big = big.multiply(BigInteger.TEN).add(BigInteger.valueOf(d));
                }
                count++;
            }
        }
        int sign = b[p + len - 1] & 0x0F;
        BigInteger unscaled = big != null ? big : BigInteger.valueOf(acc);
        if (sign == 0x0D || sign == 0x0B) unscaled = unscaled.negate();
        return new BigDecimal(unscaled, scale);
    }

    private BigDecimal decBinary() {
        byte[] raw = java.util.Arrays.copyOfRange(area.b, pos(), pos() + len);
        BigInteger v = signed || use == Use.INDEX ? new BigInteger(raw) : new BigInteger(1, raw);
        return new BigDecimal(v, scale);
    }

    // ------------------------------------------------------------------ numeric encode

    /**
     * Store a value: scale is truncated (or rounded half-up), high-order digits beyond the PICTURE are truncated.
     * Returns true when significant high-order digits were lost (the ON SIZE ERROR condition).
     */
    public boolean setDec(BigDecimal v, boolean rounded) {
        if (cat == Cat.NUM_EDITED) {
            BigDecimal s = v.setScale(Math.max(scale, 0), rounded ? RoundingMode.HALF_UP : RoundingMode.DOWN);
            boolean overflow = s.unscaledValue().abs().compareTo(BigInteger.TEN.pow(digits)) >= 0;
            setBytes(Edit.format(s, pic, scale, bwz).getBytes(java.nio.charset.StandardCharsets.ISO_8859_1));
            return overflow;
        }
        if (!isNumeric()) {
            CobRt.move(new NumLit(v), this);
            return false;
        }
        BigDecimal s = scale >= 0 ? v.setScale(scale, rounded ? RoundingMode.HALF_UP : RoundingMode.DOWN)
                : v.setScale(scale, rounded ? RoundingMode.HALF_UP : RoundingMode.DOWN);
        BigInteger u = s.unscaledValue();
        if (!signed && use != Use.INDEX) u = u.abs();
        boolean overflow;
        switch (use) {
            case BINARY, COMP5, INDEX, POINTER -> {
                BigInteger cap = binaryCap();
                overflow = u.abs().compareTo(cap) >= 0;
                if (overflow) u = truncateBinary(u, cap);
                encodeBinary(u);
            }
            case PACKED -> {
                BigInteger cap = BigInteger.TEN.pow(digits);
                overflow = u.abs().compareTo(cap) >= 0;
                if (overflow) u = u.signum() < 0 ? u.negate().mod(cap).negate() : u.mod(cap);
                encodePacked(u);
            }
            default -> {
                BigInteger cap = BigInteger.TEN.pow(digits);
                overflow = u.abs().compareTo(cap) >= 0;
                if (overflow) u = u.signum() < 0 ? u.negate().mod(cap).negate() : u.mod(cap);
                encodeDisplay(u);
            }
        }
        return overflow;
    }

    private BigInteger binaryCap() {
        // TRUNC(STD) truncates to the PICTURE; TRUNC(BIN)/COMP-5/INDEX use the full binary capacity.
        if (use == Use.BINARY && CobConfig.BINARY_TRUNCATE) return BigInteger.TEN.pow(digits);
        int bits = len * 8 - (signed || use == Use.INDEX ? 1 : 0);
        return BigInteger.ONE.shiftLeft(bits);
    }

    private BigInteger truncateBinary(BigInteger u, BigInteger cap) {
        if (use == Use.BINARY && CobConfig.BINARY_TRUNCATE)
            return u.signum() < 0 ? u.negate().mod(cap).negate() : u.mod(cap);
        BigInteger mod = BigInteger.ONE.shiftLeft(len * 8);
        BigInteger r = u.mod(mod);
        if ((signed || use == Use.INDEX) && r.testBit(len * 8 - 1)) r = r.subtract(mod);
        return r;
    }

    private void encodeBinary(BigInteger u) {
        byte[] b = area.b;
        int p = pos();
        byte[] raw = u.toByteArray();
        byte fill = (byte) (u.signum() < 0 ? 0xFF : 0x00);
        for (int i = 0; i < len; i++) {
            int src = raw.length - len + i;
            b[p + i] = src >= 0 ? raw[src] : fill;
        }
    }

    private void encodePacked(BigInteger u) {
        byte[] b = area.b;
        int p = pos();
        boolean neg = u.signum() < 0;
        String d = u.abs().toString();
        int nd = len * 2 - 1;
        StringBuilder sb = new StringBuilder();
        for (int i = d.length(); i < nd; i++) sb.append('0');
        sb.append(d, Math.max(0, d.length() - nd), d.length());
        String digs = sb.toString();
        int sign = signed ? (neg ? 0x0D : 0x0C) : 0x0F;
        for (int i = 0; i < len; i++) {
            int hi = digs.charAt(i * 2) - '0';
            int lo = i == len - 1 ? sign : digs.charAt(i * 2 + 1) - '0';
            b[p + i] = (byte) ((hi << 4) | lo);
        }
    }

    private void encodeDisplay(BigInteger u) {
        byte[] b = area.b;
        int p = pos();
        boolean neg = u.signum() < 0;
        String d = u.abs().toString();
        int dStart = p, n = digits;
        if (signed && signSep) {
            if (signLead) { b[p] = (byte) (neg ? '-' : '+'); dStart = p + 1; }
            else b[p + len - 1] = (byte) (neg ? '-' : '+');
        }
        for (int i = 0; i < n; i++) {
            int src = d.length() - n + i;
            b[dStart + i] = (byte) (src >= 0 ? d.charAt(src) : '0');
        }
        if (signed && !signSep) {
            int sp = signLead ? dStart : dStart + n - 1;
            b[sp] = (byte) Overpunch.encode(b[sp] - '0', neg);
        }
    }

    /** True when the content is a valid numeric value for the NUMERIC class test. */
    public boolean isNumericClass() {
        byte[] b = area.b;
        int p = pos();
        if (use == Use.PACKED) {
            for (int i = 0; i < len; i++) {
                int v = b[p + i] & 0xFF;
                if ((v >> 4) > 9) return false;
                if (i < len - 1 && (v & 0x0F) > 9) return false;
            }
            int s = b[p + len - 1] & 0x0F;
            return signed ? s >= 0x0A : s == 0x0F;
        }
        if (use != Use.DISPLAY || cat != Cat.NUMERIC) {
            if (cat == Cat.NUMERIC) return true;
            for (int i = 0; i < len; i++) if (b[p + i] < '0' || b[p + i] > '9') return false;
            return true;
        }
        int dStart = p, dEnd = p + len;
        if (signed && signSep) {
            byte s = signLead ? b[p] : b[p + len - 1];
            if (s != '+' && s != '-') return false;
            if (signLead) dStart++; else dEnd--;
        }
        for (int i = dStart; i < dEnd; i++) {
            int c = b[i] & 0xFF;
            boolean signPos = signed && !signSep && (signLead ? i == dStart : i == dEnd - 1);
            if (c >= '0' && c <= '9') continue;
            if (signPos && Overpunch.isSign(c)) continue;
            return false;
        }
        return true;
    }

    @Override
    public String toString() {
        return name + "@" + off + "+" + len;
    }
}
