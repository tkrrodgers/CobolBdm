package com.mlcpu.sidecar;

import java.math.BigDecimal;
import java.math.BigInteger;
import java.util.IdentityHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.function.IntFunction;

/**
 * GnuCOBOL's {@code arithmetic-osvs} (on in {@code -std=ibm}, ibm-strict.conf): the intermediate results of an arithmetic
 * expression are aligned to a scale chosen at compile time ({@code cob_decimal_align}). This class replays cobc 3.x's
 * decision exactly (typeck.c: {@code build_decimal_assign}, {@code decimal_expand}, {@code decimal_compute},
 * {@code decimal_align}, {@code push_expr_dec}, {@code cb_walk_cond}; tree.c: the constant folding of
 * {@code cb_build_binary_op}) over the statement's expression and returns, per binary operation of the IR, the scale its
 * result is truncated to.
 *
 * <p>An alignment to a scale above the intermediate's own is a GnuCOBOL defect: {@code cob_decimal_align} then divides
 * by the power of ten instead of multiplying (3.3: {@code COMPUTE R = 1 + K * 66} into V999 gives 4001, {@code ADD A B TO C}
 * with COMP A, B and a V99 C loses the integer's last two digits). The planner marks it ({k, s}); the runtime
 * ({@code CobRt.alignUp}) lets it pass only where it changes nothing and stops the run otherwise, as for any frontier.
 */
final class OsvsPlanner {
    /** cobc's COB_MAX_DIGITS: a quotient keeps this many more decimals than dividend minus divisor. */
    static final int MAX_DIGITS = 38;

    /** An expression the planner cannot decide (an aligned intermediate of unknown scale): the emitter abstains. */
    static final class Defect extends RuntimeException {
        Defect(String m) { super(m); }
    }

    /** Field facts the planner needs: the PICTURE scale and whether cobc loads it as a 64-bit integer (set_llint). */
    record FieldFacts(int scale, boolean llint, int length) {}

    private sealed interface Sim permits Leaf, Lit, Zero, Fn, Bin {}
    private record Leaf(int scale, boolean llint) implements Sim {}
    private record Lit(BigInteger unscaled, int scale, int size) implements Sim {}
    private record Zero() implements Sim {}
    private record Fn(Integer scale) implements Sim {}
    private record Bin(char op, Sim x, Sim y, Ir.Node ir) implements Sim {}

    private static final class Reg {
        Ir.Node node;
        Integer scale;
    }

    private static final Set<String> INTEGER_FUNCTIONS = Set.of("INTEGER-OF-DATE", "DATE-OF-INTEGER", "INTEGER", "INTEGER-PART", "MOD", "LENGTH",
            "ORD", "ORD-MAX", "ORD-MIN", "INTEGER-OF-DAY", "DAY-OF-INTEGER", "FACTORIAL", "YEAR-TO-YYYY", "DATE-TO-YYYYMMDD", "TEST-NUMVAL",
            "TEST-NUMVAL-C", "TEST-DATE-YYYYMMDD");

    private final IntFunction<FieldFacts> fields;
    /**
     * Per binary operation whose result cobc aligns: {k} truncates it to k decimals; {k, s} is the defect (its static scale
     * s is below k): cob_decimal_align then divides by 10^(k - s), leaving 2s - k decimals.
     */
    private final Map<Ir.Node, int[]> marks = new IdentityHashMap<>();
    private int dmax = -1;
    private int nest;
    private final int[] decp = new int[64];
    private Reg pendingReg;
    private int pendingDec = -1;

    private OsvsPlanner(IntFunction<FieldFacts> fields) {
        this.fields = fields;
    }

    /**
     * The truncations of one arithmetic statement: {@code op} 0 for COMPUTE and the GIVING forms, '+' / '-' for ADD ... TO
     * and SUBTRACT ... FROM with several operands ({@code value} is then their sum, applied to each receiver).
     */
    static Map<Ir.Node, int[]> plan(IntFunction<FieldFacts> fields, List<Ir.Node> receivers, Ir.Node value, char op) {
        OsvsPlanner p = new OsvsPlanner(fields);
        Sim v = p.build(value);
        for (Ir.Node r : receivers) {
            FieldFacts f = r.id == null ? null : fields.apply(r.id);
            if (f != null) p.dmax = Math.max(p.dmax, f.scale());
        }
        p.walk(v);
        p.nest = 0;
        Reg d = new Reg();
        p.expand(d, v);
        if (op != 0)
            for (Ir.Node r : receivers) {
                Reg t = new Reg();
                FieldFacts f = r.id == null ? null : fields.apply(r.id);
                p.expand(t, f == null ? new Fn(null) : new Leaf(f.scale(), f.llint()));
                p.compute(op, t, d.scale, null);
            }
        return p.marks;
    }

    // ------------------------------------------------------------------ the expression as cobc builds it (with constant folding)

    private Sim build(Ir.Node n) {
        if (n == null) throw new IllegalArgumentException("missing operand");
        return switch (n.k) {
            case "ref" -> {
                FieldFacts f = n.id == null ? null : fields.apply(n.id);
                yield f == null ? new Fn(null) : new Leaf(f.scale(), f.llint());
            }
            case "num" -> literal(n.v);
            case "len" -> {
                // LENGTH OF a fixed-size item is a numeric literal in cobc (cb_build_length)
                FieldFacts f = n.l == null || n.l.id == null ? null : fields.apply(n.l.id);
                yield f == null ? new Fn(0) : literal(Integer.toString(f.length()));
            }
            case "fig" -> new Zero();
            case "neg" -> binary('-', new Zero(), build(n.l), n);
            case "fn" -> new Fn(n.v != null && INTEGER_FUNCTIONS.contains(n.v.toUpperCase()) ? 0 : null);
            case "bin" -> binary(switch (n.op) {
                case "+" -> '+';
                case "-" -> '-';
                case "*" -> '*';
                case "/" -> '/';
                case "**" -> '^';
                default -> throw new IllegalArgumentException("operator " + n.op);
            }, build(n.l), build(n.r), n);
            default -> new Fn(null);
        };
    }

    private static Lit literal(String text) {
        String t = text.startsWith("+") ? text.substring(1) : text;
        BigDecimal v = new BigDecimal(t.startsWith(".") ? "0" + t : t.startsWith("-.") ? "-0" + t.substring(1) : t);
        int point = t.indexOf('.');
        int scale = point < 0 ? 0 : t.length() - point - 1;
        int size = (int) t.chars().filter(Character::isDigit).count();
        return new Lit(v.setScale(scale).unscaledValue(), scale, size);
    }

    /** cb_build_binary_op with constant-folding: yes (tree.c). */
    private Sim binary(char op, Sim x, Sim y, Ir.Node ir) {
        if (x instanceof Lit xl && y instanceof Lit yl && xl.size() >= xl.scale() && yl.size() >= yl.scale()) {
            long xv = xl.unscaled().longValue(), yv = yl.unscaled().longValue();
            int xs = xl.scale(), ys = yl.scale();
            dmax = Math.max(dmax, Math.max(xs, ys)); // cb_set_dmax of both literals
            long r = 0;
            int rs = 0;
            switch (op) {
                case '+', '-' -> {
                    while (xs < ys) { xv *= 10; xs++; }
                    while (xs > ys) { yv *= 10; ys++; }
                    rs = xs;
                    r = op == '+' ? xv + yv : xv - yv;
                }
                case '*' -> { rs = xs + ys; r = xv * yv; }
                case '/' -> {
                    if (yv != 0) {
                        while (ys > 0) { xv *= 10; ys--; }
                        rs = xs;
                        if (xv % yv == 0) r = xv / yv;
                    }
                }
                default -> { }
            }
            while (rs > 0 && r != 0 && r % 10 == 0) { r /= 10; rs--; }
            switch (op) {
                case '+', '-', '*' -> { return lit(r, rs); }
                case '/' -> {
                    if (yv == 0) break;
                    if (r != 0) return lit(r, rs);
                    if (xl.scale() != 0 || yl.scale() != 0) break;
                    if (xv % yv == 0) return lit(xv / yv, rs);
                }
                case '^' -> {
                    if (xl.scale() != 0 || yl.scale() != 0 || yv < 0) break;
                    if (yv == 0 || xv == 1) return lit(1, 0);
                    long p = xv;
                    while (--yv > 0) p *= xv;
                    return lit(p, 0);
                }
                default -> { }
            }
        } else if (y instanceof Lit yl && yl.scale() == 0) {
            long yv = yl.unscaled().longValue();
            if ((op == '+' || op == '-') && yv == 0) return x;              // + or - ZERO does nothing
            if ((op == '*' || op == '/') && yv == 1) return x;              // * or / by ONE (not -1) does nothing
            if (op == '*' && yv == 0) return lit(0, 0);                     // * ZERO is ZERO
        }
        return new Bin(op, x, y, ir);
    }

    private static Lit lit(long v, int scale) {
        return new Lit(BigInteger.valueOf(v), scale, Math.max(1, Long.toString(Math.abs(v)).length()));
    }

    /** cb_walk_cond: the largest scale of the expression's literals and items, divisors excepted. */
    private void walk(Sim s) {
        switch (s) {
            case Lit l -> dmax = Math.max(dmax, l.scale());
            case Leaf f -> dmax = Math.max(dmax, f.scale());
            case Bin b -> {
                walk(b.x());
                if (b.op() != '/') walk(b.y());
            }
            default -> { }
        }
    }

    // ------------------------------------------------------------------ decimal_expand / decimal_compute / decimal_align

    private void expand(Reg d, Sim s) {
        switch (s) {
            case Zero z -> { d.node = null; d.scale = 0; }
            case Lit l -> {
                align();
                d.node = null;
                if (l.size() < 19 && l.scale() == 0) d.scale = 0;
                else { d.scale = l.scale(); push(l.scale()); }
            }
            case Leaf f -> {
                align();
                d.node = null;
                if (f.llint()) d.scale = 0;
                else { d.scale = f.scale(); push(f.scale()); }
            }
            case Fn f -> {
                align();
                d.node = null;
                d.scale = f.scale();
                push(0);
            }
            case Bin b -> {
                expand(d, b.x());
                if (b.y() instanceof Lit l) compute(b.op(), d, l.scale(), b.ir());
                else {
                    Reg t = new Reg();
                    expand(t, b.y());
                    compute(b.op(), d, t.scale, b.ir(), t);
                }
            }
        }
    }

    private void compute(char op, Reg x, Integer yScale, Ir.Node ir) {
        compute(op, x, yScale, ir, null);
    }

    private void compute(char op, Reg x, Integer yScale, Ir.Node ir, Reg y) {
        if (pendingReg != null && pendingDec >= 0 && pendingReg != x) align();
        int dp = dmax;
        if (nest > 1) {
            nest--;
            switch (op) {
                case '+', '-' -> { if (decp[nest] > decp[nest - 1]) decp[nest - 1] = decp[nest]; }
                case '*' -> decp[nest - 1] += decp[nest];
                case '/' -> {
                    int d = decp[nest - 1] - decp[nest];
                    decp[nest - 1] = d > dmax ? d : dmax;
                }
                case '^' -> { if (decp[nest - 1] - decp[nest] < decp[nest - 1]) decp[nest - 1] = decp[nest - 1] - decp[nest]; }
                default -> { }
            }
            dp = decp[nest - 1];
        }
        Integer xs = x.scale;
        x.scale = switch (op) {
            case '+', '-' -> xs == null || yScale == null ? null : Math.max(xs, yScale);
            case '*' -> xs == null || yScale == null ? null : xs + yScale;
            case '/' -> xs == null || yScale == null ? null : (xs - yScale < 0 ? MAX_DIGITS : xs - yScale + MAX_DIGITS);
            default -> null;
        };
        x.node = ir;
        pendingReg = x;
        pendingDec = dp;
    }

    private void push(int dec) {
        if (nest < decp.length) decp[nest++] = dec;
    }

    /** decimal_align: the pending alignment of the last result, if any. */
    private void align() {
        if (pendingDec >= 0 && pendingReg != null) {
            Reg r = pendingReg;
            int k = pendingDec;
            if (r.node != null) {
                if (r.scale == null)
                    throw new Defect("arithmetic-osvs aligns an intermediate result whose scale is not known at compile time");
                if (r.scale > k) marks.put(r.node, new int[]{k});
                else if (r.scale < k) {
                    marks.put(r.node, new int[]{k, r.scale});
                    r.scale = 2 * r.scale - k; // what the defect leaves
                    pendingDec = -1;
                    pendingReg = null;
                    return;
                }
            }
            r.scale = k;
        }
        pendingDec = -1;
        pendingReg = null;
    }
}
