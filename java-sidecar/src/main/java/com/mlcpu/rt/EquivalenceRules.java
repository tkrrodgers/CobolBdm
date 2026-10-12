package com.mlcpu.rt;

import java.math.BigDecimal;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

/**
 * What the library-equivalence probe ({@link EquivalenceProbe}) learns besides the JDK operation: how the other output
 * fields of a CALL behave (unchanged, set to a constant, copied from another argument field, either of these under a
 * condition such as {@code DF-B-EXP > DF-A-EXP}), and where a verified statement stops holding (a conjunction of
 * ranges over input features that covers every failing CALL).
 */
final class EquivalenceRules {
    private EquivalenceRules() {}

    // ------------------------------------------------------------------ predicates over a CALL's inputs

    abstract static class Pred {
        abstract boolean test(byte[][] before);

        /** With the CALL's numeric fields decoded once (by field id; null when invalid). */
        boolean test(byte[][] before, BigDecimal[] nums) { return test(before); }

        abstract String describe();
    }

    static final class NumCmp extends Pred {
        final EquivalenceProbe.PField x, y;
        final int op; // -1 x < y, 0 x = y, 1 x > y

        NumCmp(EquivalenceProbe.PField x, int op, EquivalenceProbe.PField y) {
            this.x = x;
            this.op = op;
            this.y = y;
        }

        boolean test(byte[][] b) {
            BigDecimal a = x.num(b), c = y.num(b);
            if (a == null || c == null) return false;
            return Integer.signum(a.compareTo(c)) == op;
        }

        boolean test(byte[][] b, BigDecimal[] nums) {
            BigDecimal a = nums[x.id], c = nums[y.id];
            if (a == null || c == null) return false;
            return Integer.signum(a.compareTo(c)) == op;
        }

        String describe() { return x.name + (op < 0 ? " < " : op > 0 ? " > " : " = ") + y.name; }
    }

    static final class NumConst extends Pred {
        final EquivalenceProbe.PField x;
        final int op;
        final BigDecimal k;

        NumConst(EquivalenceProbe.PField x, int op, BigDecimal k) {
            this.x = x;
            this.op = op;
            this.k = k;
        }

        boolean test(byte[][] b) {
            BigDecimal a = x.num(b);
            return a != null && Integer.signum(a.compareTo(k)) == op;
        }

        boolean test(byte[][] b, BigDecimal[] nums) {
            BigDecimal a = nums[x.id];
            return a != null && Integer.signum(a.compareTo(k)) == op;
        }

        String describe() { return x.name + (op < 0 ? " < " : op > 0 ? " > " : " = ") + k.toPlainString(); }
    }

    static final class TextEq extends Pred {
        final EquivalenceProbe.PField x;
        final String code;

        TextEq(EquivalenceProbe.PField x, String code) {
            this.x = x;
            this.code = code;
        }

        boolean test(byte[][] b) { return x.code(b).equals(code); }

        String describe() { return x.name + " = " + EquivalenceProbe.renderCode(x, code); }
    }

    // ------------------------------------------------------------------ rules for one output field

    abstract static class Rule {
        abstract void apply(EquivalenceProbe.PField f, byte[][] before, byte[][] img);

        /** Null when the field keeps its value (nothing to say). */
        abstract String describe(EquivalenceProbe.PField f);
    }

    static final class Keep extends Rule {
        void apply(EquivalenceProbe.PField f, byte[][] before, byte[][] img) {
            System.arraycopy(before[f.arg], f.off, img[f.arg], f.off, f.len);
        }

        String describe(EquivalenceProbe.PField f) { return null; }
    }

    static final Keep KEEP = new Keep();

    static final class Const extends Rule {
        final byte[] v;

        Const(byte[] v) { this.v = v; }

        void apply(EquivalenceProbe.PField f, byte[][] before, byte[][] img) {
            System.arraycopy(v, 0, img[f.arg], f.off, f.len);
        }

        String describe(EquivalenceProbe.PField f) { return f.name + " = " + EquivalenceProbe.renderBytes(f, v); }
    }

    static final class Copy extends Rule {
        final EquivalenceProbe.PField from;

        Copy(EquivalenceProbe.PField from) { this.from = from; }

        void apply(EquivalenceProbe.PField f, byte[][] before, byte[][] img) {
            System.arraycopy(before[from.arg], from.off, img[f.arg], f.off, f.len);
        }

        String describe(EquivalenceProbe.PField f) { return f.name + " = " + from.name + " (the value the caller passed)"; }
    }

    static final class Cond extends Rule {
        final Pred p;
        final Rule then, other;

        Cond(Pred p, Rule then, Rule other) {
            this.p = p;
            this.then = then;
            this.other = other;
        }

        void apply(EquivalenceProbe.PField f, byte[][] before, byte[][] img) {
            (p.test(before) ? then : other).apply(f, before, img);
        }

        String describe(EquivalenceProbe.PField f) {
            String a = then.describe(f), b = other.describe(f);
            return "when " + p.describe() + ": " + (a == null ? f.name + " unchanged" : a) + "; otherwise " + (b == null ? f.name + " unchanged" : b);
        }
    }

    /** One CALL as the learner sees it: inputs before, the image predicted so far (before plus the result), the faithful after. */
    record Obs(byte[][] before, byte[][] img, byte[][] after) {}

    static boolean sameField(EquivalenceProbe.PField f, byte[][] a, byte[][] b) {
        return Arrays.equals(a[f.arg], f.off, f.off + f.len, b[f.arg], f.off, f.off + f.len);
    }

    static boolean sameAs(EquivalenceProbe.PField f, byte[][] after, EquivalenceProbe.PField g, byte[][] before) {
        return Arrays.equals(after[f.arg], f.off, f.off + f.len, before[g.arg], g.off, g.off + g.len);
    }

    /** A learned rule and the observations it agrees with. */
    record Learned(Rule rule, int agree) {}

    /** Each predicate's value on each observation, with the numeric fields decoded once per observation. */
    static boolean[][] predicateValues(List<Pred> preds, List<Obs> obs, List<EquivalenceProbe.PField> fields) {
        boolean[][] v = new boolean[preds.size()][obs.size()];
        int maxId = 0;
        for (EquivalenceProbe.PField f : fields) maxId = Math.max(maxId, f.id);
        for (int j = 0; j < obs.size(); j++) {
            byte[][] b = obs.get(j).before();
            BigDecimal[] nums = new BigDecimal[maxId + 1];
            for (EquivalenceProbe.PField f : fields) if (f.numeric() && f.len <= 18) nums[f.id] = f.num(b);
            for (int i = 0; i < preds.size(); i++) v[i][j] = preds.get(i).test(b, nums);
        }
        return v;
    }

    /**
     * The rule that explains a field over the observations: one of the simple rules on each side of a condition (the
     * predicates' values precomputed by {@link #predicateValues}). When no condition makes both sides exact, the simple rule
     * that agrees with most observations.
     */
    static Learned learn(EquivalenceProbe.PField f, List<Obs> obs, List<EquivalenceProbe.PField> copyFrom, List<Pred> preds, boolean[][] vals,
                         boolean preferConst) {
        Learned simple = simple(f, obs, copyFrom, preferConst);
        if (simple.agree() == obs.size() || preds == null || obs.size() < 4) return simple;
        for (int pi = 0; pi < preds.size(); pi++) {
            Pred p = preds.get(pi);
            List<Obs> yes = new ArrayList<>(), no = new ArrayList<>();
            for (int j = 0; j < obs.size(); j++) (vals[pi][j] ? yes : no).add(obs.get(j));
            if (yes.isEmpty() || no.isEmpty()) continue;
            Learned a = simple(f, yes, copyFrom, preferConst);
            if (a.agree() != yes.size()) continue;
            Learned b = simple(f, no, copyFrom, preferConst);
            if (b.agree() != no.size()) continue;
            if (a.rule() instanceof Keep && b.rule() instanceof Keep) continue;
            return new Learned(new Cond(p, a.rule(), b.rule()), obs.size());
        }
        return simple;
    }

    static Learned simple(EquivalenceProbe.PField f, List<Obs> obs, List<EquivalenceProbe.PField> copyFrom, boolean preferConst) {
        int n = obs.size();
        if (n == 0) return new Learned(KEEP, 0);
        int keep = 0;
        Map<String, Integer> consts = new HashMap<>();
        Map<String, byte[]> constBytes = new HashMap<>();
        for (Obs o : obs) {
            if (sameField(f, o.after(), o.before())) keep++;
            byte[] v = Arrays.copyOfRange(o.after()[f.arg], f.off, f.off + f.len);
            String k = new String(v, java.nio.charset.StandardCharsets.ISO_8859_1);
            consts.merge(k, 1, Integer::sum);
            constBytes.putIfAbsent(k, v);
        }
        String bestK = null;
        int best = -1;
        for (Map.Entry<String, Integer> e : consts.entrySet())
            if (e.getValue() > best) {
                best = e.getValue();
                bestK = e.getKey();
            }
        if (preferConst && best == n) return new Learned(new Const(constBytes.get(bestK)), n);
        if (keep == n) return new Learned(KEEP, n);
        if (best == n) return new Learned(new Const(constBytes.get(bestK)), n);
        Learned top = keep >= best ? new Learned(KEEP, keep) : new Learned(new Const(constBytes.get(bestK)), best);
        for (EquivalenceProbe.PField g : copyFrom) {
            if (g == f || g.len != f.len) continue;
            int a = 0;
            for (Obs o : obs) if (sameAs(f, o.after(), g, o.before())) a++;
            if (a == n) return new Learned(new Copy(g), n);
            if (a > top.agree()) top = new Learned(new Copy(g), a);
        }
        return top;
    }

    /**
     * The rule for a field from the CALLs that decide it ({@code obs}, the traced ones), with {@code alt} (generated CALLs,
     * which also pass other values in) choosing among the rules that fit {@code obs} equally: a field the traces always
     * pass as 0 and get back as 0 is "unchanged" or "set to 0", and only a CALL that passes something else tells which.
     */
    static Learned choose(EquivalenceProbe.PField f, List<Obs> obs, List<Obs> alt, List<EquivalenceProbe.PField> copyFrom, boolean preferConst) {
        Learned simple = simple(f, obs, copyFrom, preferConst);
        if (simple.agree() < obs.size() || alt.isEmpty() || obs.isEmpty()) return simple;
        List<Rule> perfect = new ArrayList<>();
        perfect.add(simple.rule());
        boolean keep = true, constant = true;
        byte[] first = Arrays.copyOfRange(obs.get(0).after()[f.arg], f.off, f.off + f.len);
        for (Obs o : obs) {
            if (!sameField(f, o.after(), o.before())) keep = false;
            if (!Arrays.equals(o.after()[f.arg], f.off, f.off + f.len, first, 0, f.len)) constant = false;
        }
        if (keep) perfect.add(KEEP);
        if (constant) perfect.add(new Const(first));
        for (EquivalenceProbe.PField g : copyFrom) {
            if (g == f || g.len != f.len) continue;
            boolean all = true;
            for (Obs o : obs) if (!sameAs(f, o.after(), g, o.before())) { all = false; break; }
            if (all) perfect.add(new Copy(g));
        }
        Rule best = simple.rule();
        int bestA = agreement(best, f, alt);
        for (Rule r : perfect) {
            int a = agreement(r, f, alt);
            if (a > bestA) {
                best = r;
                bestA = a;
            }
        }
        return new Learned(best, obs.size());
    }

    static int agreement(Rule r, EquivalenceProbe.PField f, List<Obs> obs) {
        int n = 0;
        for (Obs o : obs) {
            byte[][] img = new byte[o.before().length][];
            img[f.arg] = o.before()[f.arg].clone();
            r.apply(f, o.before(), img);
            if (sameField(f, img, o.after())) n++;
        }
        return n;
    }

    /** Conditions for conditional rules: comparisons of numeric fields, signs, and text codes. */
    static List<Pred> predicates(List<EquivalenceProbe.PField> fields, List<byte[][]> sample) {
        List<Pred> out = new ArrayList<>();
        List<EquivalenceProbe.PField> nums = new ArrayList<>();
        for (EquivalenceProbe.PField f : fields) if (f.numeric() && f.len <= 18) nums.add(f);
        // simplest first: a field against zero, then two fields against each other
        for (EquivalenceProbe.PField f : nums)
            for (int op = -1; op <= 1; op++) out.add(new NumConst(f, op, BigDecimal.ZERO));
        for (int i = 0; i < nums.size(); i++)
            for (int j = i + 1; j < nums.size(); j++)
                if (nums.get(i).scale == nums.get(j).scale && Math.abs(nums.get(i).digits - nums.get(j).digits) <= 5)
                    for (int op = -1; op <= 1; op++) out.add(new NumCmp(nums.get(i), op, nums.get(j)));
        for (EquivalenceProbe.PField f : fields)
            if (!f.numeric() && f.len <= 4) {
                java.util.Set<String> codes = new java.util.LinkedHashSet<>();
                for (byte[][] b : sample) {
                    codes.add(f.code(b));
                    if (codes.size() > 8) break;
                }
                if (codes.size() > 1 && codes.size() <= 8) for (String c : codes) out.add(new TextEq(f, c));
            }
        return out;
    }

    // ------------------------------------------------------------------ where a statement stops holding

    /** One term of a failure region: lo <= feature <= hi (an open side is Long.MIN_VALUE / Long.MAX_VALUE). */
    record Term(int feature, long lo, long hi) {
        boolean holds(long x) { return x != NA && x >= lo && x <= hi; }
    }

    /** A region of the input space that holds every failing CALL; {@code okInside} counts the successes it also holds. */
    record Region(List<Term> terms, int failures, int okInside, int okTotal) {}

    static final long NA = Long.MIN_VALUE;

    /**
     * Greedily intersects ranges (each satisfied by every failure) until no success is left inside or no term helps (up
     * to four terms), then simplifies the box without letting more successes in: a term on a simpler feature
     * ({@code rank}: field values before operand digits before differences) replaces a term where it can, redundant terms
     * go, and a side that does not matter is left open.
     */
    static Region region(List<long[]> fail, List<long[]> ok, int features, boolean[] categorical, int[] rank) {
        List<Term> terms = new ArrayList<>();
        boolean[] inside = new boolean[ok.size()];
        Arrays.fill(inside, true);
        int okInside = ok.size();
        for (int step = 0; step < 4 && okInside > 0; step++) {
            Term bestT = null;
            int bestLeft = okInside;
            for (int j = 0; j < features; j++) {
                Term t = range(j, fail, categorical);
                if (t == null) continue;
                int left = 0;
                for (int i = 0; i < ok.size(); i++) if (inside[i] && t.holds(ok.get(i)[j])) left++;
                if (left < bestLeft) {
                    bestLeft = left;
                    bestT = t;
                }
            }
            if (bestT == null) break;
            terms.add(bestT);
            for (int i = 0; i < ok.size(); i++) if (inside[i] && !bestT.holds(ok.get(i)[bestT.feature()])) inside[i] = false;
            okInside = bestLeft;
        }
        terms = simplify(terms, fail, ok, features, categorical, rank);
        return new Region(terms, fail.size(), inside(terms, ok), ok.size());
    }

    /** The range of feature j over the failures (null when a failure lacks it, or a code feature takes several codes). */
    static Term range(int j, List<long[]> fail, boolean[] categorical) {
        if (fail.isEmpty()) return null;
        long lo = Long.MAX_VALUE, hi = Long.MIN_VALUE + 1;
        for (long[] v : fail) {
            if (v[j] == NA) return null;
            lo = Math.min(lo, v[j]);
            hi = Math.max(hi, v[j]);
        }
        if (categorical[j] && lo != hi) return null;
        return new Term(j, lo, hi);
    }

    static int inside(List<Term> terms, List<long[]> ok) {
        int n = 0;
        outer:
        for (long[] v : ok) {
            for (Term t : terms) if (!t.holds(v[t.feature()])) continue outer;
            n++;
        }
        return n;
    }

    static List<Term> simplify(List<Term> terms, List<long[]> fail, List<long[]> ok, int features, boolean[] categorical, int[] rank) {
        List<Term> cur = new ArrayList<>(terms);
        int base = inside(cur, ok);
        Integer[] order = new Integer[features];
        for (int j = 0; j < features; j++) order[j] = j;
        Arrays.sort(order, (a, b) -> Integer.compare(rank[a], rank[b]));
        for (int i = 0; i < cur.size(); i++) {
            Term t = cur.get(i);
            for (int j : order) {
                if (rank[j] >= rank[t.feature()]) break;
                Term c = range(j, fail, categorical);
                if (c == null) continue;
                List<Term> trial = new ArrayList<>(cur);
                trial.set(i, c);
                if (inside(trial, ok) <= base) {
                    cur = trial;
                    break;
                }
            }
        }
        for (int i = cur.size() - 1; i >= 0; i--) {
            List<Term> trial = new ArrayList<>(cur);
            trial.remove(i);
            if (!trial.isEmpty() && inside(trial, ok) <= base) cur = trial;
        }
        for (int i = 0; i < cur.size(); i++) {
            Term t = cur.get(i);
            if (categorical[t.feature()] || t.lo() == t.hi()) continue;
            for (Term open : new Term[]{new Term(t.feature(), t.lo(), Long.MAX_VALUE), new Term(t.feature(), Long.MIN_VALUE + 1, t.hi())}) {
                List<Term> trial = new ArrayList<>(cur);
                trial.set(i, open);
                if (inside(trial, ok) <= base) {
                    cur = trial;
                    break;
                }
            }
        }
        return cur;
    }

    /** A term in words. */
    static String describe(Term t, String name) {
        if (t.lo() == t.hi()) return name + " = " + t.lo();
        if (t.hi() == Long.MAX_VALUE) return name + " >= " + t.lo();
        if (t.lo() <= Long.MIN_VALUE + 1) return name + " <= " + t.hi();
        return t.lo() + " <= " + name + " <= " + t.hi();
    }

    // ------------------------------------------------------------------ the effects of a CALL outside a statement

    /** A field that takes the value the verified statement gives it (computed per CALL by {@code image}). */
    static final class FromStatement extends Rule {
        final java.util.function.Function<byte[][], byte[][]> image;

        FromStatement(java.util.function.Function<byte[][], byte[][]> image) { this.image = image; }

        void apply(EquivalenceProbe.PField f, byte[][] before, byte[][] img) {
            byte[][] s = image.apply(before);
            if (s != null) System.arraycopy(s[f.arg], f.off, img[f.arg], f.off, f.len);
        }

        String describe(EquivalenceProbe.PField f) { return f.name + " as the statement gives it"; }
    }
}
