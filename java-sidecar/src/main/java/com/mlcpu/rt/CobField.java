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
    private boolean contentArgument;

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
    CobField contentArgument() { contentArgument = true; return this; }
    boolean isContentArgument() { return contentArgument; }

    /** This field's storage with another field's attributes (BY CONTENT copies keep the sender's description). */
    public CobField withShape(CobField f) {
        return new CobField(f.name, f.cat, f.use, area, off, f.len, f.digits, f.scale, f.signed, f.signSep, f.signLead, f.just, f.bwz, f.pic, null, null);
    }

    /** The same item over another area at offset {@code offset} (a sort key over one record's bytes). */
    public CobField over(Area a, int offset) {
        return new CobField(name, cat, use, a, offset, len, digits, scale, signed, signSep, signLead, just, bwz, pic, null, null);
    }

    /** The same item with another length (an OCCURS DEPENDING ON group at its current length). */
    public CobField withLength(int length) {
        return new CobField(name, cat, use, area, off, length, digits, scale, signed, signSep, signLead, just, bwz, pic, counts, strides);
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
        // subscripts and lengths: the long path when the value fits (BigDecimal.intValue keeps the low 32 bits of the
        // integer part, as the cast of the truncated long does)
        if (scale >= 0 && scale <= 18) {
            long u = unscaledLong();
            if (u != NO_LONG) return (int) (scale == 0 ? u : u / Pow10.LONG[scale]);
        }
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
        // Digits accumulate in two longs (18 + 18), joined once: a 31-digit field costs one BigInteger, not one per digit.
        long acc = 0, low = 0;
        int count = 0, lowCount = 0;
        BigInteger big = null;
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
            else if (big == null && lowCount < 18) { low = low * 10 + d; lowCount++; }
            else {
                if (big == null) big = BigInteger.valueOf(acc).multiply(Pow10.big(lowCount)).add(BigInteger.valueOf(low));
                big = big.multiply(BigInteger.TEN).add(BigInteger.valueOf(d));
            }
            count++;
        }
        return signedDecimal(acc, low, lowCount, big, neg);
    }

    private BigDecimal decPacked() {
        if (len <= 18) {
            // 2 * len - 1 <= 35 digits: the last 18 in lo, the others in hi, two digits per byte through PAIR
            byte[] b = area.b;
            int p = pos(), last = p + len - 1;
            long hi = 0, lo = 0;
            int split = last - 9;  // its high nibble is the 19th digit from the right, its low nibble the 18th
            for (int i = p; i < split; i++) hi = hi * 100 + PAIR[b[i] & 0xFF];
            if (split >= p) {
                int v = b[split] & 0xFF;
                hi = hi * 10 + HIGH[v];
                lo = LOW[v];
            }
            for (int i = Math.max(split + 1, p); i < last; i++) lo = lo * 100 + PAIR[b[i] & 0xFF];
            int v = b[last] & 0xFF;
            lo = lo * 10 + HIGH[v];
            int sign = v & 0x0F;
            boolean neg = sign == 0x0D || sign == 0x0B;
            if (hi == 0) return BigDecimal.valueOf(neg ? -lo : lo, scale);
            return new BigDecimal(join18(hi, lo, neg), scale);
        }
        byte[] b = area.b;
        int p = pos();
        long acc = 0, low = 0;
        int count = 0, lowCount = 0;
        BigInteger big = null;
        for (int i = 0; i < len; i++) {
            int v = b[p + i] & 0xFF;
            int hi = v >> 4, lo = v & 0x0F;
            for (int k = 0; k < (i == len - 1 ? 1 : 2); k++) {
                int d = k == 0 ? hi : lo;
                if (d > 9) d = 0;
                if (count < 18) acc = acc * 10 + d;
                else if (big == null && lowCount < 18) { low = low * 10 + d; lowCount++; }
                else {
                    if (big == null) big = BigInteger.valueOf(acc).multiply(Pow10.big(lowCount)).add(BigInteger.valueOf(low));
                    big = big.multiply(BigInteger.TEN).add(BigInteger.valueOf(d));
                }
                count++;
            }
        }
        int sign = b[p + len - 1] & 0x0F;
        return signedDecimal(acc, low, lowCount, big, sign == 0x0D || sign == 0x0B);
    }

    // ------------------------------------------------------------------ long paths (phase 5: exact, BigDecimal-free MOVE)

    /** {@link #unscaledLong()} when the value takes the BigDecimal path. */
    static final long NO_LONG = Long.MIN_VALUE;

    /**
     * The unscaled value as {@link #dec()} decodes it, as a long: binary up to 8 bytes (an unsigned 8-byte value past
     * 2^63 excepted), packed up to 9 bytes (17 digits), zoned up to 18 digits; otherwise {@link #NO_LONG}.
     */
    long unscaledLong() {
        byte[] b = area.b;
        int p = pos();
        switch (use) {
            case BINARY, COMP5, INDEX, POINTER -> {
                if (len > 8) return NO_LONG;
                long v = 0;
                if (littleEndian()) for (int i = len - 1; i >= 0; i--) v = (v << 8) | (b[p + i] & 0xFF);
                else for (int i = 0; i < len; i++) v = (v << 8) | (b[p + i] & 0xFF);
                if (signed || use == Use.INDEX) {
                    int shift = 64 - 8 * len;
                    return (v << shift) >> shift;
                }
                return v >= 0 ? v : NO_LONG;
            }
            case PACKED -> {
                if (len > 9) return NO_LONG;
                int last = p + len - 1;
                long lo = 0;
                for (int i = p; i < last; i++) lo = lo * 100 + PAIR[b[i] & 0xFF];
                int v = b[last] & 0xFF;
                lo = lo * 10 + HIGH[v];
                int sign = v & 0x0F;
                return sign == 0x0D || sign == 0x0B ? -lo : lo;
            }
            case DISPLAY -> {
                if (cat != Cat.NUMERIC && cat != Cat.INDEX) return NO_LONG;
                boolean neg = false;
                int dStart = p, dEnd = p + len;
                if (signed && signSep) {
                    neg = (signLead ? b[p] : b[p + len - 1]) == '-';
                    if (signLead) dStart++; else dEnd--;
                }
                if (dEnd - dStart > 18) return NO_LONG;
                long acc = 0;
                for (int i = dStart; i < dEnd; i++) {
                    int c = b[i] & 0xFF;
                    int d;
                    if (signed && !signSep && (signLead ? i == dStart : i == dEnd - 1)) {
                        int[] ds = Overpunch.decode(c);
                        d = ds[0];
                        if (ds[1] < 0) neg = true;
                    } else {
                        d = c & 0x0F;
                        if (d > 9) d = 0;
                    }
                    acc = acc * 10 + d;
                }
                return neg ? -acc : acc;
            }
            default -> {
                return NO_LONG;
            }
        }
    }

    /**
     * Stores the unscaled value {@code u} (at this item's scale, |u| < 2^62) as {@link #setDec} stores it after scaling:
     * the absolute value into an unsigned item, then the item's truncation and encoding. Returns the overflow flag, or
     * null when the store takes the BigDecimal path.
     */
    Boolean storeUnscaled(long u) {
        if (cat != Cat.NUMERIC && cat != Cat.INDEX) return null;
        if (!signed && use != Use.INDEX) u = Math.abs(u);
        switch (use) {
            case BINARY, COMP5, INDEX, POINTER -> {
                return len <= 8 ? encodeBinary(u) : null;
            }
            case PACKED -> {
                if (digits > 18) {
                    long m = Math.abs(u);
                    encodePacked(m / E18, m % E18, u < 0);
                    return false;
                }
                long cap = Pow10.LONG[digits];
                boolean overflow = Math.abs(u) >= cap;
                if (overflow) u %= cap;
                encodePacked(u);
                return overflow;
            }
            case DISPLAY -> {
                if (digits > 18) return null;
                long cap = Pow10.LONG[digits];
                boolean overflow = Math.abs(u) >= cap;
                if (overflow) u %= cap;
                encodeDisplay(u);
                return overflow;
            }
            default -> {
                return null;
            }
        }
    }

    /**
     * MOVE numeric to numeric without BigDecimal when both values fit a long: exactly {@code setDec(s.dec(), false)}
     * (truncation toward zero when the receiver has fewer decimals). Returns false when the caller must take that path.
     */
    boolean moveNumericFast(CobField s) {
        if (cat != Cat.NUMERIC && cat != Cat.INDEX) return false;
        long u = s.unscaledLong();
        if (u == NO_LONG) return false;
        int diff = scale - s.scale;
        if (diff > 0) {
            if (diff > 18) return false;
            long m = Pow10.LONG[diff];
            if (Math.abs(u) > Long.MAX_VALUE / m) return false;
            u *= m;
        } else if (diff < 0) {
            u = -diff > 18 ? 0 : u / Pow10.LONG[-diff];
        }
        if (Math.abs(u) >= 1L << 62) return false;
        return storeUnscaled(u) != null;
    }

    /**
     * MOVE of a numeric literal (unscaled {@code u} at {@code s} decimals) without BigDecimal: exactly
     * {@code setDec(literal, false)} for a numeric receiver. Returns false when the caller must take that path.
     */
    boolean storeLiteral(long u, int s) {
        if (cat != Cat.NUMERIC && cat != Cat.INDEX) return false;
        int diff = scale - s;
        if (diff > 0) {
            if (diff > 18) return false;
            long m = Pow10.LONG[diff];
            if (Math.abs(u) > Long.MAX_VALUE / m) return false;
            u *= m;
        } else if (diff < 0) {
            u = -diff > 18 ? 0 : u / Pow10.LONG[-diff];
        }
        if (Math.abs(u) >= 1L << 62) return false;
        return storeUnscaled(u) != null;
    }

    /** The value of the digits the decoders accumulated: {@code acc}, then {@code lowCount} digits in {@code low}. */
    private BigDecimal signedDecimal(long acc, long low, int lowCount, BigInteger big, boolean neg) {
        if (big == null && lowCount == 0) return BigDecimal.valueOf(neg ? -acc : acc, scale);
        BigInteger unscaled = big != null ? big : BigInteger.valueOf(acc).multiply(Pow10.big(lowCount)).add(BigInteger.valueOf(low));
        return new BigDecimal(neg ? unscaled.negate() : unscaled, scale);
    }

    /** COMP-5 in native (little-endian) order, as GnuCOBOL stores it on x86-64. */
    private boolean littleEndian() {
        return use == Use.COMP5 && CobConfig.COMP5_LITTLE_ENDIAN;
    }

    private BigDecimal decBinary() {
        if (len <= 8) {
            // Up to 8 bytes: the value in a long (no copy, no BigInteger); an unsigned 8-byte value past 2^63 goes the long way.
            byte[] b = area.b;
            int p = pos();
            long v = 0;
            if (littleEndian()) for (int i = len - 1; i >= 0; i--) v = (v << 8) | (b[p + i] & 0xFF);
            else for (int i = 0; i < len; i++) v = (v << 8) | (b[p + i] & 0xFF);
            if (signed || use == Use.INDEX) {
                int shift = 64 - 8 * len;
                return BigDecimal.valueOf((v << shift) >> shift, scale);
            }
            if (v >= 0) return BigDecimal.valueOf(v, scale);
        }
        byte[] raw = java.util.Arrays.copyOfRange(area.b, pos(), pos() + len);
        if (littleEndian()) for (int i = 0, j = len - 1; i < j; i++, j--) { byte t = raw[i]; raw[i] = raw[j]; raw[j] = t; }
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
            boolean overflow = s.unscaledValue().abs().compareTo(Pow10.big(digits)) >= 0;
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
                if (len <= 8 && u.bitLength() <= 62) return encodeBinary(u.longValue());
                BigInteger cap = binaryCap();
                overflow = u.abs().compareTo(cap) >= 0;
                if (overflow) u = truncateBinary(u, cap);
                encodeBinary(u);
            }
            case PACKED -> {
                if (digits > 18 && u.bitLength() <= 62) {
                    // a small value into a wide field (|v| < 2^62 < 10^19 <= 10^digits): no overflow, no BigInteger
                    long m = Math.abs(u.longValue());
                    encodePacked(m / E18, m % E18, u.signum() < 0);
                    return false;
                }
                if (digits <= 18 && u.bitLength() <= 62) {
                    // long fast path: the same truncation (the remainder keeps the sign, as the mod of the magnitude does)
                    long lu = u.longValue(), cap = Pow10.LONG[digits];
                    overflow = Math.abs(lu) >= cap;
                    if (overflow) lu %= cap;
                    encodePacked(lu);
                    return overflow;
                }
                BigInteger cap = Pow10.big(digits);
                overflow = u.abs().compareTo(cap) >= 0;
                if (overflow) u = u.signum() < 0 ? u.negate().mod(cap).negate() : u.mod(cap);
                encodePacked(u);
            }
            default -> {
                if (digits <= 18 && u.bitLength() <= 62) {
                    long lu = u.longValue(), cap = Pow10.LONG[digits];
                    overflow = Math.abs(lu) >= cap;
                    if (overflow) lu %= cap;
                    encodeDisplay(lu);
                    return overflow;
                }
                BigInteger cap = Pow10.big(digits);
                overflow = u.abs().compareTo(cap) >= 0;
                if (overflow) u = u.signum() < 0 ? u.negate().mod(cap).negate() : u.mod(cap);
                encodeDisplay(u);
            }
        }
        return overflow;
    }

    private BigInteger binaryCap() {
        // TRUNC(STD) truncates to the PICTURE; TRUNC(BIN)/COMP-5/INDEX use the full binary capacity.
        if (use == Use.BINARY && CobConfig.BINARY_TRUNCATE) return Pow10.big(digits);
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

    /**
     * The long path of a binary store (|u| < 2^62, at most 8 bytes): the same truncation as {@link #binaryCap} and
     * {@link #truncateBinary} (TRUNC(STD) keeps the low digits with the sign; otherwise the low bytes, two's complement),
     * the same bytes as {@link #encodeBinary(BigInteger)}. Returns whether the value overflowed.
     */
    private boolean encodeBinary(long u) {
        boolean overflow;
        if (use == Use.BINARY && CobConfig.BINARY_TRUNCATE && digits <= 18) {
            long cap = Pow10.LONG[digits];
            overflow = Math.abs(u) >= cap;
            if (overflow) u %= cap;
        } else if (len < 8) {
            int bytes = 8 * len;
            boolean sgn = signed || use == Use.INDEX;
            overflow = Math.abs(u) >= 1L << (bytes - (sgn ? 1 : 0));
            if (overflow) {
                long mod = 1L << bytes;
                u = Math.floorMod(u, mod);
                if (sgn && (u & (1L << (bytes - 1))) != 0) u -= mod;
            }
        } else if (use == Use.BINARY && CobConfig.BINARY_TRUNCATE) {
            return encodeBinarySlow(BigInteger.valueOf(u));
        } else overflow = false; // 8 bytes hold any |u| < 2^62
        byte[] b = area.b;
        int p = pos();
        if (littleEndian()) for (int i = 0; i < len; i++) b[p + i] = (byte) (u >> (8 * i));
        else for (int i = 0; i < len; i++) b[p + len - 1 - i] = (byte) (u >> (8 * i));
        return overflow;
    }

    private boolean encodeBinarySlow(BigInteger u) {
        BigInteger cap = binaryCap();
        boolean overflow = u.abs().compareTo(cap) >= 0;
        if (overflow) u = truncateBinary(u, cap);
        encodeBinary(u);
        return overflow;
    }

    private void encodeBinary(BigInteger u) {
        byte[] b = area.b;
        int p = pos();
        byte[] raw = u.toByteArray();
        byte fill = (byte) (u.signum() < 0 ? 0xFF : 0x00);
        boolean le = littleEndian();
        for (int i = 0; i < len; i++) {
            int src = raw.length - len + i;
            b[p + (le ? len - 1 - i : i)] = src >= 0 ? raw[src] : fill;
        }
    }

    /** Packed decimal of a value with |v| < 10^18: digits taken from the right, no strings. */
    private void encodePacked(long v) {
        byte[] b = area.b;
        int p = pos();
        boolean neg = v < 0;
        long m = Math.abs(v);
        int sign = signed ? (neg ? 0x0D : 0x0C) : 0x0F;
        int last = p + len - 1;
        b[last] = (byte) (((int) (m % 10) << 4) | sign);
        m /= 10;
        for (int i = last - 1; i >= p; i--) {
            int lo = (int) (m % 10);
            m /= 10;
            int hi = (int) (m % 10);
            m /= 10;
            b[i] = (byte) ((hi << 4) | lo);
        }
    }

    /** Zoned decimal of a value with |v| < 10^18: the same bytes {@link #encodeDisplay(BigInteger)} writes. */
    private void encodeDisplay(long v) {
        byte[] b = area.b;
        int p = pos();
        boolean neg = v < 0;
        long m = Math.abs(v);
        int dStart = p, n = digits;
        if (signed && signSep) {
            if (signLead) { b[p] = (byte) (neg ? '-' : '+'); dStart = p + 1; }
            else b[p + len - 1] = (byte) (neg ? '-' : '+');
        }
        for (int i = n - 1; i >= 0; i--) {
            b[dStart + i] = (byte) ('0' + (int) (m % 10));
            m /= 10;
        }
        if (signed && !signSep) {
            int sp = signLead ? dStart : dStart + n - 1;
            b[sp] = (byte) Overpunch.encode(b[sp] - '0', neg);
        }
    }

    static final long E18 = 1_000_000_000_000_000_000L;

    /** A packed byte's two digits as a number (a nibble above 9 reads as 0, as the decoders always did). */
    private static final int[] PAIR = new int[256], HIGH = new int[256], LOW = new int[256];
    /** The packed byte of a two-digit number 0..99. */
    private static final byte[] PACK = new byte[100];

    static {
        for (int v = 0; v < 256; v++) {
            int h = v >> 4, l = v & 0x0F;
            HIGH[v] = h > 9 ? 0 : h;
            LOW[v] = l > 9 ? 0 : l;
            PAIR[v] = HIGH[v] * 10 + LOW[v];
        }
        for (int n = 0; n < 100; n++) PACK[n] = (byte) (((n / 10) << 4) | (n % 10));
    }
    private static final BigInteger E9 = BigInteger.valueOf(1_000_000_000L);

    /**
     * {hi, lo} with a = hi * 10^18 + lo for 0 <= a < 10^36: a long when it fits, else two divisions by 10^9 (one-word
     * divisors, much cheaper than one Knuth division by 10^18).
     */
    static long[] split18(BigInteger a) {
        if (a.bitLength() <= 63) {
            long v = a.longValue();
            return new long[]{v / E18, v % E18};
        }
        if (a.bitLength() <= 120) {
            // 128-bit (h:l) / 10^18 without BigInteger division (about 2.5x faster): a double estimate of the quotient,
            // the exact 128-bit remainder, a second estimate from it, then unit corrections (none seen in practice).
            long l = a.longValue(), h = a.shiftRight(64).longValue();
            long q = (long) ((h * 18446744073709551616.0 + ((l >>> 1) * 2.0 + (l & 1))) / 1e18);
            for (int pass = 0; pass < 2; pass++) {
                long pl = q * E18, ph = Math.multiplyHigh(q, E18);
                long rl = l - pl, rh = h - ph - (Long.compareUnsigned(l, pl) < 0 ? 1 : 0);
                double rd = rh * 18446744073709551616.0 + ((rl >>> 1) * 2.0 + (rl & 1));
                q += (long) Math.floor(rd / 1e18);
            }
            while (true) {
                long pl = q * E18, ph = Math.multiplyHigh(q, E18);
                long rl = l - pl, rh = h - ph - (Long.compareUnsigned(l, pl) < 0 ? 1 : 0);
                if (rh < 0) { q--; continue; }
                if (rh > 0 || Long.compareUnsigned(rl, E18) >= 0) { q++; continue; }
                return new long[]{q, rl};
            }
        }
        BigInteger[] q1 = a.divideAndRemainder(E9);
        BigInteger[] q2 = q1[0].divideAndRemainder(E9);
        long lo = q2[1].longValue() * 1_000_000_000L + q1[1].longValue();
        return new long[]{q2[0].longValue(), lo};
    }

    /** hi * 10^18 + lo (hi < 10^18, 0 <= lo < 10^18) as a BigInteger, through one 128-bit product: no BigInteger arithmetic. */
    static BigInteger join18(long hi, long lo, boolean neg) {
        long pl = hi * E18, ph = Math.multiplyHigh(hi, E18);
        long sl = pl + lo;
        long sh = ph + (Long.compareUnsigned(sl, pl) < 0 ? 1 : 0);
        if (sh == 0 && sl == 0) return BigInteger.ZERO;
        byte[] m = new byte[16];
        for (int i = 0; i < 8; i++) {
            m[7 - i] = (byte) (sh >>> (8 * i));
            m[15 - i] = (byte) (sl >>> (8 * i));
        }
        return new BigInteger(neg ? -1 : 1, m);
    }

    /** 10^36: below it a magnitude splits into two longs of 18 digits each. */
    private static final BigInteger TEN36 = BigInteger.TEN.pow(36);

    /**
     * Packed decimal of a magnitude below 10^36 given as hi * 10^18 + lo: the same bytes as the string path, without
     * BigInteger.toString (31-digit COMP-3 stores are the decimal kernel's hot path).
     */
    private void encodePacked(long hi, long lo, boolean neg) {
        byte[] b = area.b;
        int p = pos();
        int sign = signed ? (neg ? 0x0D : 0x0C) : 0x0F;
        int last = p + len - 1;
        b[last] = (byte) (((int) (lo % 10) << 4) | sign);
        lo /= 10;                                   // 17 digits of the low word left
        int i = last - 1;
        for (int n = 0; n < 8 && i >= p; n++, i--) {
            b[i] = PACK[(int) (lo % 100)];
            lo /= 100;
        }
        if (i >= p) {                               // the low word's last digit and the high word's first
            b[i--] = PACK[(int) (lo % 10) + (int) (hi % 10) * 10];
            hi /= 10;
        }
        for (; i >= p; i--) {
            b[i] = PACK[(int) (hi % 100)];
            hi /= 100;
        }
    }

    private void encodePacked(BigInteger u) {
        BigInteger a = u.abs();
        if (a.compareTo(TEN36) < 0) {
            long[] hl = split18(a);
            encodePacked(hl[0], hl[1], u.signum() < 0);
            return;
        }
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

    /** Zoned decimal of a magnitude below 10^36 given as hi * 10^18 + lo (the same bytes as the string path). */
    private void encodeDisplay(long hi, long lo, boolean neg) {
        byte[] b = area.b;
        int p = pos();
        int dStart = p, n = digits;
        if (signed && signSep) {
            if (signLead) { b[p] = (byte) (neg ? '-' : '+'); dStart = p + 1; }
            else b[p + len - 1] = (byte) (neg ? '-' : '+');
        }
        long cur = lo, nextWord = hi;
        int left = 18;
        for (int i = n - 1; i >= 0; i--) {
            b[dStart + i] = (byte) ('0' + (int) (cur % 10));
            cur /= 10;
            if (--left == 0) { cur = nextWord; nextWord = 0; left = Integer.MAX_VALUE; }
        }
        if (signed && !signSep) {
            int sp = signLead ? dStart : dStart + n - 1;
            b[sp] = (byte) Overpunch.encode(b[sp] - '0', neg);
        }
    }

    private void encodeDisplay(BigInteger u) {
        BigInteger a = u.abs();
        if (a.compareTo(TEN36) < 0) {
            long[] hl = split18(a);
            encodeDisplay(hl[0], hl[1], u.signum() < 0);
            return;
        }
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
