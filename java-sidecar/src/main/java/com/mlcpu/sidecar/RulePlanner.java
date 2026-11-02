package com.mlcpu.sidecar;

import java.math.BigDecimal;
import java.util.ArrayList;
import java.util.List;
import java.util.Set;

/**
 * Plans typed rule slices from the resolved semantic tree (never from generated Java). A condition or arithmetic
 * expression becomes a typed model only when its result is fully determined by immutable JDK values read at the
 * evaluation point and the plain-Java evaluation provably agrees with the runtime ({@code CobRt.compare}, {@code is88},
 * {@code isNumeric}, {@code isAlphabetic}, BigDecimal add/subtract/multiply). Everything else is a {@link Skip}: the
 * faithful lowering stays in place and the reason is published.
 *
 * <p>Supported: numeric relations (BigDecimal compare, as the runtime's numeric path), alphanumeric EQUAL / NOT EQUAL
 * (space padded, byte exact), level-88 views of their parent (numeric values and ranges; alphanumeric values), class and
 * sign tests, AND / OR / NOT, and + - * / negation of numeric items and literals. Fail-closed: alphanumeric ordering
 * (collating sequence), mixed numeric/alphanumeric comparisons, subscripts, reference modification, OCCURS DEPENDING ON,
 * intrinsic functions, division and exponentiation, ALL literals and compound conditions over LINKAGE items.
 */
final class RulePlanner {

    static final class Skip extends RuntimeException {
        Skip(String reason) { super(reason, null, false, false); }
    }

    interface Facts {
        Ir.Field field(int id);

        /** True when the item's position or length follows OCCURS DEPENDING ON, or the emitter abstains on it for ODO. */
        boolean odo(int id);
    }

    // ---- typed model -----------------------------------------------------------------------------------------------

    sealed interface Num permits NIn, NLit, NBin, NNeg {}
    record NIn(int input) implements Num {}
    /** A numeric literal in the runtime's NumLit text form (BigDecimal-parsable, no leading '+'). */
    record NLit(String text) implements Num {}
    record NBin(char op, Num l, Num r) implements Num {}
    record NNeg(Num x) implements Num {}

    sealed interface Str permits SIn, SLit, SFill {}
    record SIn(int input) implements Str {}
    record SLit(String text) implements Str {}
    /** A figurative constant: as many {@code c} as the other operand is long. */
    record SFill(char c, String figurative) implements Str {}

    sealed interface Cond permits CNum, CStrEq, C88Num, C88Str, CClass, CSign, CAnd, COr, CNot {}
    record CNum(String op, Num l, Num r) implements Cond {}
    record CStrEq(boolean equal, Str l, Str r) implements Cond {}
    /** Level-88 over a numeric parent: each entry is {value} or {low, high}. */
    record C88Num(String name, int parent, List<Num[]> values) implements Cond {}
    record C88Str(String name, int parent, List<Str> values) implements Cond {}
    record CClass(String kind, int input, boolean negated) implements Cond {}
    record CSign(String kind, Num x, boolean negated) implements Cond {}
    record CAnd(Cond l, Cond r) implements Cond {}
    record COr(Cond l, Cond r) implements Cond {}
    record CNot(Cond x) implements Cond {}

    static final Set<String> ALNUM_CATEGORIES = Set.of("Alphanumeric", "Alphabetic", "AlphanumericEdited", "NumericEdited", "Group");
    private static final Set<String> CLASS_CATEGORIES = Set.of("Alphanumeric", "Alphabetic", "AlphanumericEdited", "Group");

    private final Facts facts;
    /** Inputs in first-read order; a data item is read once per evaluation. */
    final List<Ir.Field> inputs = new ArrayList<>();
    private final List<Boolean> numericInput = new ArrayList<>();

    RulePlanner(Facts facts) {
        this.facts = facts;
    }

    boolean isNumericInput(int i) {
        return numericInput.get(i);
    }

    /** A predicate over the data items read by the condition. */
    Cond predicate(Ir.Node n) {
        Cond c = cond(n);
        if (inputs.isEmpty()) throw new Skip("constant condition: no data item is read");
        if (compound(c) && inputs.stream().anyMatch(f -> "Linkage".equals(f.section)))
            throw new Skip("compound condition over a LINKAGE item: COBOL may leave an unbound operand unevaluated, a snapshot reads every operand");
        return c;
    }

    /** An exact (unrounded) arithmetic intermediate; storing it stays with the adapter. */
    Num calculation(Ir.Node n) {
        Num v = num(n);
        if (inputs.isEmpty()) throw new Skip("constant expression: no data item is read");
        return v;
    }

    private static boolean compound(Cond c) {
        return switch (c) {
            case CAnd a -> true;
            case COr o -> true;
            case CNot x -> compound(x.x());
            default -> false;
        };
    }

    private Cond cond(Ir.Node n) {
        if (n == null) throw new Skip("missing condition");
        return switch (n.k) {
            case "rel" -> relation(n);
            case "and" -> new CAnd(cond(n.l), cond(n.r));
            case "or" -> new COr(cond(n.l), cond(n.r));
            case "not" -> new CNot(cond(n.l));
            case "c88" -> level88(n);
            case "class" -> {
                String kind = n.v == null ? "" : n.v;
                if (!Set.of("NUMERIC", "ALPHABETIC", "ALPHABETIC-LOWER", "ALPHABETIC-UPPER").contains(kind))
                    throw new Skip("class test " + kind + " (user-defined class or unsupported kind)");
                Ir.Field f = plainRef(n.l, "class test");
                if (!CLASS_CATEGORIES.contains(f.category))
                    throw new Skip("class test of a " + f.category + " item: the runtime tests its sign/encoding, not its text");
                yield new CClass(kind, input(f, false), "not".equals(n.op));
            }
            case "sign" -> {
                String kind = n.v == null ? "" : n.v;
                if (!Set.of("POSITIVE", "NEGATIVE", "ZERO").contains(kind)) throw new Skip("sign test " + kind);
                yield new CSign(kind, num(n.l), "not".equals(n.op));
            }
            case "bool", "any" -> throw new Skip("constant condition " + n.k);
            default -> throw new Skip("condition kind " + n.k);
        };
    }

    private Cond relation(Ir.Node n) {
        String op = n.op == null ? "" : n.op;
        if (!Set.of("eq", "ne", "lt", "le", "gt", "ge").contains(op)) throw new Skip("relational operator " + op);
        if (n.l == null || n.r == null) throw new Skip("missing relation operand");
        if ("condval".equals(n.l.k) || "condval".equals(n.r.k)) throw new Skip("comparison of condition values");
        if (arithmetic(n.l) || arithmetic(n.r)) return new CNum(op, num(n.l), num(n.r));
        boolean an = numericOperand(n.l), bn = numericOperand(n.r);
        if (an && bn) return new CNum(op, num(n.l), num(n.r));
        if (zeroFig(n.l) && bn) return new CNum(op, new NLit("0"), num(n.r));
        if (zeroFig(n.r) && an) return new CNum(op, num(n.l), new NLit("0"));
        if (an || bn) throw new Skip("mixed numeric and alphanumeric comparison (numeric digits compared as text)");
        if (!op.equals("eq") && !op.equals("ne"))
            throw new Skip("alphanumeric ordering " + op + " depends on the collating sequence");
        Str l = str(n.l), r = str(n.r);
        if (l instanceof SFill && r instanceof SFill) throw new Skip("comparison of two figurative constants");
        return new CStrEq(op.equals("eq"), l, r);
    }

    private Cond level88(Ir.Node n) {
        Ir.Field c = facts.field(n.id);
        if (c == null) throw new Skip("unknown condition-name");
        if (n.args != null && !n.args.isEmpty()) throw new Skip("subscripted condition-name " + c.field);
        if (c.parentId == null) throw new Skip("level-88 without a parent");
        if (c.valueAll) throw new Skip("level-88 VALUE ALL " + c.field);
        if (c.values == null || c.values.isEmpty()) throw new Skip("level-88 without values " + c.field);
        Ir.Field p = facts.field(c.parentId);
        if (p == null) throw new Skip("unknown condition-name parent");
        checkPlain(p, "condition-name parent");
        if (numericCategory(p)) {
            List<Num[]> values = new ArrayList<>();
            int parent = input(p, true);
            for (Ir.Value v : c.values) {
                Num lo = numericValue(v.v, v.q, c.field);
                values.add(v.hi == null ? new Num[]{lo} : new Num[]{lo, numericValue(v.hi, Boolean.TRUE.equals(v.hq), c.field)});
            }
            return new C88Num(c.field, parent, values);
        }
        if (!ALNUM_CATEGORIES.contains(p.category)) throw new Skip("level-88 over a " + p.category + " item");
        int parent = input(p, false);
        List<Str> values = new ArrayList<>();
        for (Ir.Value v : c.values) {
            if (v.hi != null) throw new Skip("THRU range of alphanumeric condition-name " + c.field + " depends on the collating sequence");
            if (v.q) values.add(literal(v.v));
            else {
                SFill f = fill(v.v == null ? "" : v.v.toUpperCase());
                if (f == null) throw new Skip("non-alphanumeric VALUE " + v.v + " of alphanumeric condition-name " + c.field);
                values.add(f);
            }
        }
        return new C88Str(c.field, parent, values);
    }

    private Num numericValue(String text, boolean quoted, String name) {
        if (quoted || text == null) throw new Skip("alphanumeric VALUE of numeric condition-name " + name + " (compared as text)");
        String u = text.toUpperCase();
        if (Set.of("ZERO", "ZEROS", "ZEROES").contains(u)) return new NLit("0");
        if (SkeletonEmitter.figurative(u) != null) throw new Skip("figurative " + text + " of numeric condition-name " + name + " (compared as text)");
        return literalNum(text);
    }

    // ---- operands --------------------------------------------------------------------------------------------------

    private static boolean arithmetic(Ir.Node n) {
        return "bin".equals(n.k) || "neg".equals(n.k) || "fn".equals(n.k);
    }

    private boolean numericOperand(Ir.Node n) {
        if ("num".equals(n.k)) return true;
        if ("len".equals(n.k)) return true;
        if (!"ref".equals(n.k)) return false;
        Ir.Field f = facts.field(n.id);
        return f != null && ("Numeric".equals(f.category) || "Index".equals(f.category));
    }

    private static boolean zeroFig(Ir.Node n) {
        return "fig".equals(n.k) && n.v != null && Set.of("ZERO", "ZEROS", "ZEROES").contains(n.v.toUpperCase());
    }

    private Num num(Ir.Node n) {
        if (n == null) throw new Skip("missing numeric operand");
        return switch (n.k) {
            case "ref" -> {
                Ir.Field f = plainRef(n, "numeric operand");
                if (!numericCategory(f)) throw new Skip("numeric use of a " + f.category + " item " + f.field);
                yield new NIn(input(f, true));
            }
            case "num" -> literalNum(n.v);
            case "bin" -> {
                String op = n.op == null ? "" : n.op;
                if (op.equals("/") || op.equals("**"))
                    throw new Skip("operator " + op + ": the runtime's truncated quotient, power and SIZE ERROR are not reproduced in plain Java");
                if (!op.equals("+") && !op.equals("-") && !op.equals("*")) throw new Skip("operator " + op);
                yield new NBin(op.charAt(0), num(n.l), num(n.r));
            }
            case "neg" -> new NNeg(num(n.l));
            case "fig" -> {
                if (!"ZEROS".equals(n.v)) throw new Skip("figurative " + n.v + " in arithmetic");
                yield new NLit("0");
            }
            case "fn" -> throw new Skip("intrinsic FUNCTION " + n.v);
            case "len" -> throw new Skip("LENGTH OF");
            default -> throw new Skip("numeric operand kind " + n.k);
        };
    }

    private Str str(Ir.Node n) {
        return switch (n.k) {
            case "ref" -> {
                Ir.Field f = plainRef(n, "alphanumeric operand");
                if (!ALNUM_CATEGORIES.contains(f.category)) throw new Skip("alphanumeric use of a " + f.category + " item " + f.field);
                yield new SIn(input(f, false));
            }
            case "str" -> literal(n.v);
            case "fig" -> {
                SFill f = fill(n.v == null ? "" : n.v.toUpperCase());
                if (f == null) throw new Skip("figurative " + n.v);
                yield f;
            }
            case "all" -> throw new Skip("ALL literal");
            default -> throw new Skip("alphanumeric operand kind " + n.k);
        };
    }

    private static SLit literal(String s) {
        if (s == null) throw new Skip("missing literal");
        for (char ch : s.toCharArray()) if (ch > 0xFF) throw new Skip("literal character outside the record character set");
        return new SLit(s);
    }

    static SFill fill(String u) {
        String fig = SkeletonEmitter.figurative(u);
        if (fig == null) return null;
        return switch (fig) {
            case "Fig.SPACES" -> new SFill(' ', "SPACES");
            case "Fig.ZEROS" -> new SFill('0', "ZEROS");
            case "Fig.HIGH_VALUES" -> new SFill((char) 0xFF, "HIGH-VALUES");
            case "Fig.LOW_VALUES" -> new SFill((char) 0, "LOW-VALUES");
            case "Fig.QUOTES" -> new SFill('"', "QUOTES");
            case "Fig.NULLS" -> new SFill((char) 0, "NULLS");
            default -> null;
        };
    }

    /** The runtime's NumLit text for a literal (SkeletonEmitter.num normalization, then NumLit's '+' removal). */
    static NLit literalNum(String text) {
        if (text == null) throw new Skip("missing numeric literal");
        String t = text.startsWith(".") ? "0" + text : text;
        if (t.endsWith(".")) t = t.substring(0, t.length() - 1);
        if (t.startsWith("+")) t = t.substring(1);
        try {
            new BigDecimal(t);
        } catch (NumberFormatException e) {
            throw new Skip("numeric literal " + text);
        }
        return new NLit(t);
    }

    private Ir.Field plainRef(Ir.Node n, String what) {
        if (n == null || !"ref".equals(n.k)) throw new Skip(what + " is not a data item");
        Ir.Field f = facts.field(n.id);
        if (f == null) throw new Skip("unknown data item " + n.v);
        if (n.args != null && !n.args.isEmpty()) throw new Skip("subscripted " + what + " " + f.field + " (bounds and evaluation order stay with the adapter)");
        if (n.start != null || n.len != null) throw new Skip("reference-modified " + what + " " + f.field);
        checkPlain(f, what);
        return f;
    }

    private void checkPlain(Ir.Field f, String what) {
        if (f.dims != null && !f.dims.isEmpty()) throw new Skip(what + " " + f.field + " is a table element");
        if (facts.odo(f.id)) throw new Skip(what + " " + f.field + " is shaped by OCCURS DEPENDING ON");
        if (f.lvl() == 88) throw new Skip("condition-name used as data");
    }

    private static boolean numericCategory(Ir.Field f) {
        return "Numeric".equals(f.category) && !"COMP-1".equals(f.usage) && !"COMP-2".equals(f.usage);
    }

    private int input(Ir.Field f, boolean numeric) {
        for (int i = 0; i < inputs.size(); i++)
            if (inputs.get(i).id == f.id) {
                if (numericInput.get(i) != numeric) throw new Skip(f.field + " read both as number and as text");
                return i;
            }
        inputs.add(f);
        numericInput.add(numeric);
        return inputs.size() - 1;
    }
}
