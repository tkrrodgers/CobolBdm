package com.mlcpu.rt;

import java.math.BigDecimal;
import java.math.BigInteger;
import java.math.MathContext;
import java.math.RoundingMode;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;

/** COBOL statement semantics over byte-backed fields. Generated programs call only this API and {@link CobField}. */
public final class CobRt {
    private CobRt() {}

    static final java.nio.charset.Charset L1 = StandardCharsets.ISO_8859_1;

    // ------------------------------------------------------------------ control-flow signals

    /** GOBACK / EXIT PROGRAM: unwinds every PERFORM and loop of the current program. */
    public static final class ProgramExit extends RuntimeException {
        ProgramExit() { super(null, null, false, false); }
    }

    public static final ProgramExit PROGRAM_EXIT = new ProgramExit();

    /** STOP RUN: unwinds to the outermost program of the run unit. */
    public static final class StopRun extends RuntimeException {
        public StopRun() { super("STOP RUN", null, false, false); }
    }

    /** A program-requested abend (CALL 'CEE3ABD'). */
    public static final class Abend extends RuntimeException {
        public final int code;
        public Abend(int code, String msg) { super(msg); this.code = code; }
    }

    /** Execution reached a statement the transpiler abstained on: never continue with a guessed behaviour (I1). */
    public static final class FrontierException extends RuntimeException {
        public FrontierException(String msg) { super(msg); }
    }

    /** A runtime check failed (reference modification out of range, unsupported usage). */
    public static final class RuntimeCheck extends RuntimeException {
        public RuntimeCheck(String msg) { super(msg); }
    }

    public static final class LoopLimit extends RuntimeException {
        public LoopLimit(String msg) { super(msg); }
    }

    public static final class CallNotFound extends RuntimeException {
        public CallNotFound(String name) { super("program not found: " + name); }
    }

    /** Division by zero inside an arithmetic statement: the size error condition. */
    public static final class SizeError extends RuntimeException {
        public SizeError() { super("size error", null, false, false); }
    }

    public static boolean alwaysTrue() { return true; }

    public static void frontier(String stepId, String reason) {
        FrontierHits.record(stepId, reason);
        throw new FrontierException("frontier reached at " + stepId + ": " + reason);
    }

    // ------------------------------------------------------------------ run-unit registers

    /** RETURN-CODE: one register per run unit, shared by every program (IBM semantics). */
    public static final CobField RETURN_CODE = CobField.num("RETURN-CODE", CobField.Use.BINARY, new Area(4), 0, 4, 9, 0, true, false, false, null, null);
    public static final CobField TALLY = CobField.num("TALLY", CobField.Use.BINARY, new Area(4), 0, 4, 5, 0, false, false, false, null, null);

    private static long loopCount;

    public static void loopTick(String stepId) {
        if (++loopCount > CobConfig.LOOP_LIMIT) {
            FrontierHits.loopCut(stepId);
            throw new LoopLimit("loop cut at " + stepId + " after " + CobConfig.LOOP_LIMIT + " iterations");
        }
    }

    public static void resetRunUnit() {
        CobFile.forgetOpen();
        loopCount = 0;
        RETURN_CODE.setDec(BigDecimal.ZERO, false);
        FrontierHits.clear();
    }

    // ------------------------------------------------------------------ operand helpers

    public static byte[] bytes(String s) { return s.getBytes(L1); }

    public static boolean isNumericOperand(Object o) {
        if (o instanceof CobField f) return f.cat == CobField.Cat.NUMERIC || f.cat == CobField.Cat.INDEX;
        return o instanceof NumLit || o instanceof BigDecimal;
    }

    /** Numeric value of an operand. Alphanumeric operands are read as unsigned integers (NUMVAL is not implied). */
    public static BigDecimal dec(Object o) {
        if (o instanceof CobField f) return f.dec();
        if (o instanceof NumLit n) return n.v;
        if (o instanceof BigDecimal d) return d;
        if (o instanceof Fig f) return f == Fig.ZEROS ? BigDecimal.ZERO : BigDecimal.ZERO;
        if (o instanceof String s) return Edit.deEdit(bytes(s), 0);
        throw new RuntimeCheck("not numeric: " + o);
    }

    public static int toInt(BigDecimal d) { return d.intValue(); }

    /** The bytes an operand contributes to an alphanumeric context, sized for a receiver of {@code len} where relevant. */
    public static byte[] alnumBytes(Object o, int len) {
        if (o instanceof CobField f) {
            if (f.cat == CobField.Cat.NUMERIC && !f.isGroup()) return numericAsAlnum(f);
            return f.bytes();
        }
        if (o instanceof String s) return bytes(s);
        if (o instanceof NumLit n) return bytes(n.digits());
        if (o instanceof BigDecimal d) return bytes(d.abs().unscaledValue().toString());
        if (o instanceof Fig f) { byte[] r = new byte[Math.max(len, 1)]; Arrays.fill(r, f.b); return r; }
        if (o instanceof AllLit a) return a.repeat(Math.max(len, 1));
        if (o instanceof byte[] b) return b;
        throw new RuntimeCheck("not alphanumeric: " + o);
    }

    /** A numeric item used as alphanumeric: its digits without sign or decimal point. */
    static byte[] numericAsAlnum(CobField f) {
        if (f.use == CobField.Use.DISPLAY && !f.signed) return f.bytes();
        BigInteger u = f.dec().abs().unscaledValue();
        if (f.scale < 0) u = u.multiply(BigInteger.TEN.pow(-f.scale));
        String d = u.toString();
        int n = Math.max(f.digits, 1);
        if (d.length() > n) d = d.substring(d.length() - n);
        return bytes("0".repeat(n - d.length()) + d);
    }

    public static int sizeOf(Object o) {
        if (o instanceof CobField f) return f.len;
        if (o instanceof String s) return s.length();
        if (o instanceof NumLit n) return n.digits().length();
        return 1;
    }

    // ------------------------------------------------------------------ MOVE

    public static void move(Object src, CobField dst) {
        if (src instanceof CobField f) { moveField(f, dst); return; }
        if (src instanceof NumLit n) { moveNumber(n.v, n.digits(), dst); return; }
        if (src instanceof BigDecimal d) { moveNumber(d, d.abs().toPlainString().replace(".", ""), dst); return; }
        if (src instanceof Fig fg) { moveFig(fg, dst); return; }
        if (src instanceof AllLit a) {
            if (dst.isNumeric()) alnumToNumeric(a.repeat(dst.len), dst); else dst.setBytes(a.repeat(dst.len));
            return;
        }
        byte[] b = alnumBytes(src, dst.len);
        moveAlnumBytes(b, dst);
    }

    public static void move(Object src, CobField... dsts) {
        for (CobField d : dsts) move(src, d);
    }

    static void moveField(CobField s, CobField d) {
        if (s.isGroup() || d.isGroup()) { alnumMove(s.bytes(), d, false); return; }
        switch (d.cat) {
            case ALNUM, ALPHA -> alnumMove(s.cat == CobField.Cat.NUMERIC ? numericAsAlnum(s) : s.bytes(), d, d.just);
            case ALNUM_EDITED -> alnumEdited(s.cat == CobField.Cat.NUMERIC ? numericAsAlnum(s) : s.bytes(), d);
            case NUMERIC, INDEX, NUM_EDITED, POINTER -> {
                switch (s.cat) {
                    case NUMERIC, INDEX -> d.setDec(s.dec(), false);
                    case NUM_EDITED -> d.setDec(Edit.deEdit(s.bytes(), s.scale), false);
                    default -> {
                        if (d.cat == CobField.Cat.NUM_EDITED) alnumMove(s.bytes(), d, false);
                        else alnumToNumeric(s.bytes(), d);
                    }
                }
            }
            default -> alnumMove(s.bytes(), d, false);
        }
    }

    static void moveNumber(BigDecimal v, String digits, CobField d) {
        switch (d.cat) {
            case NUMERIC, INDEX, NUM_EDITED, POINTER -> d.setDec(v, false);
            case ALNUM_EDITED -> alnumEdited(bytes(digits), d);
            default -> alnumMove(bytes(digits), d, d.just && !d.isGroup());
        }
    }

    static void moveFig(Fig f, CobField d) {
        if (f == Fig.ZEROS && (d.cat == CobField.Cat.NUMERIC || d.cat == CobField.Cat.NUM_EDITED || d.cat == CobField.Cat.INDEX)) {
            d.setDec(BigDecimal.ZERO, false);
            return;
        }
        d.fill(f.b);
    }

    public static void moveAlnumBytes(byte[] b, CobField d) {
        switch (d.cat) {
            case NUMERIC, INDEX, POINTER -> alnumToNumeric(b, d);
            case ALNUM_EDITED -> alnumEdited(b, d);
            default -> alnumMove(b, d, d.just && !d.isGroup());
        }
    }

    /** Alphanumeric move: left-justified (or right when JUSTIFIED), space-filled, truncated. */
    static void alnumMove(byte[] src, CobField d, boolean right) {
        byte[] buf = d.buf();
        int p = d.pos();
        int n = d.len;
        if (!right) {
            int c = Math.min(src.length, n);
            System.arraycopy(src, 0, buf, p, c);
            if (c < n) Arrays.fill(buf, p + c, p + n, (byte) ' ');
        } else {
            int c = Math.min(src.length, n);
            System.arraycopy(src, src.length - c, buf, p + n - c, c);
            if (c < n) Arrays.fill(buf, p, p + n - c, (byte) ' ');
        }
    }

    /**
     * Alphanumeric sender to numeric receiver: the sender is an unsigned integer aligned on the right of the
     * integer part; spaces become zero digits (GnuCOBOL / IBM NUMPROC behaviour observed in the oracle).
     */
    static void alnumToNumeric(byte[] src, CobField d) {
        int intDigits = Math.max(d.digits - Math.max(d.scale, 0), 0);
        byte[] digs = new byte[Math.max(d.digits, 0)];
        Arrays.fill(digs, (byte) '0');
        int c = Math.min(src.length, intDigits);
        System.arraycopy(src, src.length - c, digs, intDigits - c, c);
        for (int i = 0; i < digs.length; i++) if (digs[i] == ' ') digs[i] = '0';
        if (d.use == CobField.Use.DISPLAY && d.cat == CobField.Cat.NUMERIC) {
            byte[] buf = d.buf();
            int p = d.pos();
            int start = p + (d.signed && d.signSep && d.signLead ? 1 : 0);
            System.arraycopy(digs, 0, buf, start, digs.length);
            if (d.signed && d.signSep) buf[d.signLead ? p : p + d.len - 1] = '+';
            else if (d.signed) {
                int sp = d.signLead ? start : start + digs.length - 1;
                int ch = buf[sp] & 0xFF;
                if (ch >= '0' && ch <= '9') buf[sp] = (byte) Overpunch.encode(ch - '0', false);
            }
            return;
        }
        BigInteger u = BigInteger.ZERO;
        for (byte b : digs) u = u.multiply(BigInteger.TEN).add(BigInteger.valueOf((b & 0x0F) > 9 ? 0 : b & 0x0F));
        d.setDec(new BigDecimal(u, Math.max(d.scale, 0)), false);
    }

    static void alnumEdited(byte[] src, CobField d) {
        String pic = d.pic;
        byte[] out = new byte[d.len];
        int si = 0, oi = 0;
        for (int i = 0; i < pic.length() && oi < out.length; i++) {
            char c = pic.charAt(i);
            switch (c) {
                case 'B' -> out[oi++] = ' ';
                case '0' -> out[oi++] = '0';
                case '/' -> out[oi++] = '/';
                default -> out[oi++] = si < src.length ? src[si++] : (byte) ' ';
            }
        }
        d.setBytes(out);
    }

    // ------------------------------------------------------------------ comparison

    public static int compare(Object a, Object b) {
        boolean an = isNumericOperand(a), bn = isNumericOperand(b);
        if (an && bn) return dec(a).compareTo(dec(b));
        if (a instanceof Fig f && f == Fig.ZEROS && bn) return BigDecimal.ZERO.compareTo(dec(b));
        if (b instanceof Fig f && f == Fig.ZEROS && an) return dec(a).compareTo(BigDecimal.ZERO);
        int la = sizeOf(a), lb = sizeOf(b);
        if (a instanceof CobField fa && fa.cat == CobField.Cat.NUMERIC) la = numericAsAlnum(fa).length;
        if (b instanceof CobField fb && fb.cat == CobField.Cat.NUMERIC) lb = numericAsAlnum(fb).length;
        byte[] x = alnumBytes(a, a instanceof Fig || a instanceof AllLit ? lb : la);
        byte[] y = alnumBytes(b, b instanceof Fig || b instanceof AllLit ? la : lb);
        int n = Math.max(x.length, y.length);
        for (int i = 0; i < n; i++) {
            int cx = i < x.length ? x[i] & 0xFF : ' ';
            int cy = i < y.length ? y[i] & 0xFF : ' ';
            if (cx != cy) return Integer.compare(collate(cx), collate(cy));
        }
        return 0;
    }

    private static int collate(int c) {
        return RecordCharset.isEbcdic() ? RecordCharset.ebcdicByte(c) : c;
    }

    public static boolean eq(Object a, Object b) { return compare(a, b) == 0; }

    /** Level-88 test: each entry is {value} or {low, high}. */
    public static boolean is88(CobField parent, Object[][] values) {
        for (Object[] v : values) {
            if (v.length == 1 || v[1] == null) { if (compare(parent, v[0]) == 0) return true; }
            else if (compare(parent, v[0]) >= 0 && compare(parent, v[1]) <= 0) return true;
        }
        return false;
    }

    public static boolean isNumeric(Object o) {
        if (o instanceof CobField f) {
            if (f.cat == CobField.Cat.NUMERIC) return f.isNumericClass();
            byte[] b = f.bytes();
            for (byte x : b) if (x < '0' || x > '9') return false;
            return true;
        }
        if (o instanceof String s) return !s.isEmpty() && s.chars().allMatch(c -> c >= '0' && c <= '9');
        return o instanceof NumLit || o instanceof BigDecimal;
    }

    public static boolean isAlphabetic(Object o, String kind) {
        byte[] b = alnumBytes(o, 0);
        for (byte x : b) {
            int c = x & 0xFF;
            boolean ok = c == ' ' || switch (kind) {
                case "ALPHABETIC-LOWER" -> c >= 'a' && c <= 'z';
                case "ALPHABETIC-UPPER" -> c >= 'A' && c <= 'Z';
                default -> (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
            };
            if (!ok) return false;
        }
        return true;
    }

    // ------------------------------------------------------------------ arithmetic

    public static BigDecimal add(BigDecimal a, BigDecimal b) { return a.add(b); }
    public static BigDecimal sub(BigDecimal a, BigDecimal b) { return a.subtract(b); }
    public static BigDecimal mul(BigDecimal a, BigDecimal b) { return a.multiply(b); }

    /** Division with 38 extra fraction digits, truncated (the receiver's scale then truncates or rounds). */
    public static BigDecimal div(BigDecimal a, BigDecimal b) {
        if (b.signum() == 0) throw new SizeError();
        int sc = Math.max(a.scale(), 0) + 38;
        return a.divide(b, sc, RoundingMode.DOWN);
    }

    public static BigDecimal pow(BigDecimal a, BigDecimal b) {
        if (b.stripTrailingZeros().scale() <= 0) {
            int n = b.intValueExact();
            if (n >= 0) return a.pow(n);
            if (a.signum() == 0) throw new SizeError();
            return BigDecimal.ONE.divide(a.pow(-n), 38, RoundingMode.DOWN);
        }
        return new BigDecimal(Math.pow(a.doubleValue(), b.doubleValue()), MathContext.DECIMAL64);
    }

    /** Store an arithmetic result. With {@code sizeCheck} an overflowing result leaves the receiver unchanged and returns true. */
    public static boolean store(CobField f, BigDecimal v, boolean rounded, boolean sizeCheck) {
        if (sizeCheck && overflows(f, v, rounded)) return true;
        return f.setDec(v, rounded);
    }

    public static boolean overflows(CobField f, BigDecimal v, boolean rounded) {
        int sc = Math.max(f.scale, 0);
        BigInteger u = v.setScale(sc, rounded ? RoundingMode.HALF_UP : RoundingMode.DOWN).unscaledValue().abs();
        int digits = f.cat == CobField.Cat.NUM_EDITED ? f.digits : f.digits;
        if ((f.use == CobField.Use.BINARY && !CobConfig.BINARY_TRUNCATE) || f.use == CobField.Use.COMP5 || f.use == CobField.Use.INDEX) {
            return u.compareTo(BigInteger.ONE.shiftLeft(f.len * 8 - (f.signed ? 1 : 0))) >= 0;
        }
        return u.compareTo(BigInteger.TEN.pow(digits)) >= 0;
    }

    // ------------------------------------------------------------------ DISPLAY

    /** When set, every DISPLAY is reported here too, in order with CobFile.TRACE's I/O events (the difference probe). */
    public static volatile java.util.function.Consumer<String> DISPLAY_TRACE;

    public static void display(boolean noAdvancing, Object... items) {
        StringBuilder sb = new StringBuilder();
        for (Object o : items) sb.append(displayText(o));
        DisplaySink.current().write(sb.toString(), !noAdvancing);
        java.util.function.Consumer<String> t = DISPLAY_TRACE;
        if (t != null) t.accept(sb.toString());
    }

    public static String displayText(Object o) {
        if (o instanceof CobField f) {
            if (f.cat == CobField.Cat.NUMERIC || f.cat == CobField.Cat.INDEX) return displayNumeric(f);
            return f.str();
        }
        if (o instanceof String s) return s;
        if (o instanceof NumLit n) return n.text.replace(".", "");
        if (o instanceof BigDecimal d) return d.toPlainString();
        if (o instanceof Fig f) return new String(new byte[]{f.b}, L1);
        if (o instanceof AllLit a) return new String(a.pattern, L1);
        return String.valueOf(o);
    }

    static String displayNumeric(CobField f) {
        boolean gnu = "gnucobol".equalsIgnoreCase(CobConfig.DISPLAY_NUMERIC);
        if (f.use == CobField.Use.DISPLAY) {
            if (!gnu || !f.signed || f.signSep) return f.str();
            BigDecimal v = f.dec();
            String digits = pad(v.unscaledValue().abs().toString(), f.digits);
            char sign = v.signum() < 0 ? '-' : '+';
            return f.signLead ? sign + digits : digits + sign;
        }
        BigDecimal v = f.dec();
        int n = f.digits;
        if (f.use == CobField.Use.BINARY || f.use == CobField.Use.COMP5 || f.use == CobField.Use.INDEX) {
            if (gnu || f.use != CobField.Use.BINARY) n = switch (f.len) { case 1 -> 3; case 2 -> 5; case 4 -> 10; default -> f.signed ? 19 : 20; };
        }
        String digits = pad(v.unscaledValue().abs().toString(), n);
        if (gnu) return f.signed ? (v.signum() < 0 ? "-" : "+") + digits : digits;
        // z/OS: converted to zoned decimal; a negative value is overpunched in the last digit.
        if (f.signed && v.signum() < 0) {
            char last = digits.charAt(digits.length() - 1);
            return digits.substring(0, digits.length() - 1) + (char) Overpunch.encode(last - '0', true);
        }
        return digits;
    }

    static String pad(String d, int n) {
        if (d.length() > n) return d.substring(d.length() - n);
        return "0".repeat(n - d.length()) + d;
    }

    // ------------------------------------------------------------------ INITIALIZE

    public static void initLeaf(CobField f) {
        switch (f.cat) {
            case NUMERIC, NUM_EDITED -> f.setDec(BigDecimal.ZERO, false);
            case INDEX, POINTER -> { }
            default -> f.fill((byte) ' ');
        }
    }

    // ------------------------------------------------------------------ reference modification of values

    public static String substr(Object o, int start, int length) {
        byte[] b = alnumBytes(o, 0);
        int l = length < 0 ? b.length - start + 1 : length;
        if (start < 1 || start - 1 + l > b.length) throw new RuntimeCheck("reference modification out of range");
        return new String(b, start - 1, l, L1);
    }

    // ------------------------------------------------------------------ STRING / UNSTRING / INSPECT

    /** STRING: returns true on overflow. Delimiter {@code null} means DELIMITED BY SIZE. */
    public static boolean stringInto(CobField into, CobField pointer, Object[] sources, Object[] delims) {
        int ptr = pointer == null ? 1 : pointer.intValue();
        if (ptr < 1 || ptr > into.len) return true;
        boolean overflow = false;
        byte[] buf = into.buf();
        int base = into.pos();
        outer:
        for (int i = 0; i < sources.length; i++) {
            byte[] s = sourceBytes(sources[i]);
            int end = s.length;
            if (delims[i] != null) {
                byte[] d = alnumBytes(delims[i], 1);
                int at = indexOf(s, d, 0);
                if (at >= 0) end = at;
            }
            for (int k = 0; k < end; k++) {
                if (ptr > into.len) { overflow = true; break outer; }
                buf[base + ptr - 1] = s[k];
                ptr++;
            }
        }
        if (pointer != null) pointer.setDec(BigDecimal.valueOf(ptr), false);
        return overflow;
    }

    private static byte[] sourceBytes(Object o) {
        if (o instanceof CobField f) return f.bytes();
        if (o instanceof Fig f) return new byte[]{f.b};
        if (o instanceof AllLit a) return a.pattern;
        return alnumBytes(o, 1);
    }

    static int indexOf(byte[] s, byte[] d, int from) {
        outer:
        for (int i = from; i <= s.length - d.length; i++) {
            for (int k = 0; k < d.length; k++) if (s[i + k] != d[k]) continue outer;
            return i;
        }
        return -1;
    }

    /**
     * UNSTRING. {@code delims[i]} is a delimiter operand; {@code all[i]} marks ALL. Each target receives its
     * substring by MOVE; delimiterIn/countIn may be null. Returns true on overflow.
     */
    public static boolean unstring(CobField src, Object[] delims, boolean[] all, CobField[] targets, CobField[] delimIn,
                                   CobField[] countIn, CobField pointer, CobField tallying) {
        byte[] s = src.bytes();
        int ptr = pointer == null ? 1 : pointer.intValue();
        if (ptr < 1 || ptr > s.length) return true;
        int pos = ptr - 1;
        int filled = 0;
        byte[][] ds = new byte[delims.length][];
        for (int i = 0; i < delims.length; i++) ds[i] = sourceBytes(delims[i]);
        for (int t = 0; t < targets.length; t++) {
            if (pos >= s.length) break;
            int bestAt = -1, bestIdx = -1;
            for (int i = 0; i < ds.length; i++) {
                int at = indexOf(s, ds[i], pos);
                if (at >= 0 && (bestAt < 0 || at < bestAt)) { bestAt = at; bestIdx = i; }
            }
            int end = bestAt < 0 ? s.length : bestAt;
            byte[] part = Arrays.copyOfRange(s, pos, end);
            moveAlnumBytes(part, targets[t]);
            if (countIn != null && countIn[t] != null) countIn[t].setDec(BigDecimal.valueOf(part.length), false);
            int next = end;
            if (bestAt >= 0) {
                byte[] d = ds[bestIdx];
                if (delimIn != null && delimIn[t] != null) moveAlnumBytes(d, delimIn[t]);
                next = end + d.length;
                if (all[bestIdx]) while (next + d.length <= s.length && indexOf(s, d, next) == next) next += d.length;
            } else if (delimIn != null && delimIn[t] != null) delimIn[t].fill((byte) ' ');
            pos = next;
            filled++;
        }
        if (pointer != null) pointer.setDec(BigDecimal.valueOf(pos + 1), false);
        if (tallying != null) tallying.setDec(tallying.dec().add(BigDecimal.valueOf(filled)), false);
        return pos < s.length;
    }

    /** INSPECT region [from, to) after BEFORE/AFTER INITIAL. */
    private static int[] region(byte[] s, Object before, Object after) {
        int from = 0, to = s.length;
        if (after != null) {
            byte[] a = alnumBytes(after, 1);
            int at = indexOf(s, a, 0);
            from = at < 0 ? s.length : at + a.length;
        }
        if (before != null) {
            byte[] b = alnumBytes(before, 1);
            int at = indexOf(s, b, from);
            if (at >= 0) to = at;
        }
        return new int[]{from, Math.max(from, to)};
    }

    public static int inspectTally(CobField target, String mode, Object what, Object before, Object after) {
        byte[] s = target.bytes();
        int[] r = region(s, before, after);
        if (mode.equals("CHARACTERS")) return r[1] - r[0];
        byte[] w = alnumBytes(what, 1);
        int count = 0;
        int i = r[0];
        while (i + w.length <= r[1]) {
            if (indexOf(Arrays.copyOfRange(s, i, i + w.length), w, 0) == 0) { count++; i += w.length; }
            else { if (mode.equals("LEADING")) break; i++; }
        }
        return count;
    }

    public static void inspectReplace(CobField target, String mode, Object what, Object by, Object before, Object after) {
        byte[] s = target.bytes();
        int[] r = region(s, before, after);
        byte[] b = alnumBytes(by, mode.equals("CHARACTERS") ? 1 : sizeOf(what));
        if (mode.equals("CHARACTERS")) {
            for (int i = r[0]; i < r[1]; i++) s[i] = b[0];
        } else {
            byte[] w = alnumBytes(what, b.length);
            if (b.length != w.length) throw new RuntimeCheck("INSPECT REPLACING operands differ in size");
            int i = r[0];
            while (i + w.length <= r[1]) {
                boolean match = indexOf(Arrays.copyOfRange(s, i, i + w.length), w, 0) == 0;
                if (match) {
                    System.arraycopy(b, 0, s, i, b.length);
                    i += w.length;
                    if (mode.equals("FIRST")) break;
                } else {
                    if (mode.equals("LEADING")) break;
                    i++;
                }
            }
        }
        target.setBytes(s);
    }

    public static void inspectConvert(CobField target, Object from, Object to, Object before, Object after) {
        byte[] s = target.bytes();
        int[] r = region(s, before, after);
        byte[] f = alnumBytes(from, 1);
        byte[] t = alnumBytes(to, f.length);
        for (int i = r[0]; i < r[1]; i++)
            for (int k = 0; k < f.length; k++) if (s[i] == f[k]) { s[i] = t[Math.min(k, t.length - 1)]; break; }
        target.setBytes(s);
    }

    // ------------------------------------------------------------------ ACCEPT

    public static void accept(CobField dst, String from) {
        java.time.OffsetDateTime now = CobClock.now();
        String v = switch (from == null ? "" : from) {
            case "DATE" -> String.format("%02d%02d%02d", now.getYear() % 100, now.getMonthValue(), now.getDayOfMonth());
            case "DATE-YYYYMMDD" -> String.format("%04d%02d%02d", now.getYear(), now.getMonthValue(), now.getDayOfMonth());
            case "DAY" -> String.format("%02d%03d", now.getYear() % 100, now.getDayOfYear());
            case "DAY-YYYYDDD" -> String.format("%04d%03d", now.getYear(), now.getDayOfYear());
            case "DAY-OF-WEEK" -> String.valueOf(now.getDayOfWeek().getValue());
            case "TIME" -> String.format("%02d%02d%02d%02d", now.getHour(), now.getMinute(), now.getSecond(), now.getNano() / 10_000_000);
            case "" -> SysIn.readLine();
            default -> throw new RuntimeCheck("ACCEPT FROM " + from + " is not supported");
        };
        if (v == null) return;
        moveAlnumBytes(bytes(v), dst);
    }

    // ------------------------------------------------------------------ CALL

    public static final java.util.Set<String> ABEND_PROGRAMS = new java.util.HashSet<>(java.util.List.of("CEE3ABD", "ILBOABN0"));

    /** CALL program USING args (BY REFERENCE fields share storage; BY CONTENT arguments are copies made by the caller). */
    public static void call(String name, CobField[] args) {
        String n = name.trim().toUpperCase();
        ModernProgram normalized = ProgramRegistry.modern(n);
        if (normalized != null) {
            ProgramRegistry.callModern(normalized, args);
            return;
        }
        CobolBase target = ProgramRegistry.resolve(n);
        if (target == null) {
            if (ABEND_PROGRAMS.contains(n)) {
                int code = args.length > 0 ? args[0].dec().intValue() : 0;
                throw new Abend(code, "ABEND requested by CALL " + n + (args.length > 0 ? " code " + code : ""));
            }
            throw new CallNotFound(n);
        }
        target.call(args, true);
    }

    public static CobField byContent(Object o) {
        if (o instanceof CobField f) return CobField.temp(f.bytes()).withShape(f);
        return CobField.temp(alnumBytes(o, 0));
    }
}
