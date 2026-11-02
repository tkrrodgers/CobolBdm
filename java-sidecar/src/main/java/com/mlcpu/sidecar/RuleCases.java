package com.mlcpu.sidecar;

import com.mlcpu.rt.Area;
import com.mlcpu.rt.CobField;
import com.mlcpu.rt.CobRt;
import com.mlcpu.rt.Edit;
import com.mlcpu.rt.Fig;
import com.mlcpu.rt.NumLit;
import com.mlcpu.sidecar.RulePlanner.*;
import java.math.BigDecimal;
import java.math.BigInteger;
import java.math.RoundingMode;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;

/**
 * Witness cases for a rule with expected values computed by the faithful runtime: each input is encoded into a field of
 * its declared layout (so every value is one the COBOL item can hold), read back as the adapter snapshots it, and the
 * rule's semantic-tree node is evaluated with the runtime primitives the faithful lowering calls (CobRt.compare, is88,
 * isNumeric, isAlphabetic, add/sub/mul). The generated plain-Java implementation is not consulted.
 */
final class RuleCases {

    record Case(List<Object> inputs, Object expected) {}

    static final int MAX_CASES = 128;
    private static final int MAX_CANDIDATES = 12;

    private RuleCases() {}

    static List<Case> cases(RuleSet.Rule r, java.util.function.IntFunction<Ir.Field> dictionary) {
        List<String> numLits = new ArrayList<>();
        List<String> strLits = new ArrayList<>();
        Set<Character> fills = new LinkedHashSet<>();
        collect(r.model, numLits, strLits, fills);
        List<List<Object>> candidates = new ArrayList<>();
        for (int i = 0; i < r.inputs.size(); i++)
            candidates.add(r.numeric.get(i) ? numericCandidates(r.inputs.get(i), numLits) : textCandidates(r.inputs.get(i), strLits, fills));
        Map<String, List<Object>> tuples = new LinkedHashMap<>();
        long product = 1;
        for (List<Object> c : candidates) product *= c.size();
        if (product <= MAX_CASES) cross(candidates, 0, new ArrayList<>(), tuples);
        else {
            List<Object> base = new ArrayList<>();
            for (List<Object> c : candidates) base.add(c.get(0));
            add(tuples, base);
            int longest = candidates.stream().mapToInt(List::size).max().orElse(0);
            for (int j = 0; j < longest; j++) {
                List<Object> diagonal = new ArrayList<>();
                for (List<Object> c : candidates) diagonal.add(c.get(j % c.size()));
                add(tuples, diagonal);
            }
            for (int i = 0; i < candidates.size(); i++)
                for (Object v : candidates.get(i)) {
                    List<Object> t = new ArrayList<>(base);
                    t.set(i, v);
                    add(tuples, t);
                }
        }
        List<Case> out = new ArrayList<>();
        for (List<Object> t : tuples.values()) {
            if (out.size() >= MAX_CASES) break;
            Map<Integer, CobField> fields = new HashMap<>();
            List<Object> snapshot = new ArrayList<>();
            for (int i = 0; i < t.size(); i++) {
                CobField f = field(r.inputs.get(i));
                if (r.numeric.get(i)) {
                    f.setDec((BigDecimal) t.get(i), false);
                    snapshot.add(f.dec());
                } else {
                    f.setBytes(((String) t.get(i)).getBytes(StandardCharsets.ISO_8859_1));
                    snapshot.add(f.str());
                }
                fields.put(r.inputs.get(i).id, f);
            }
            Object expected = r.kind.equals("predicate") ? (Object) cond(r.source, fields, dictionary) : num(r.source, fields);
            out.add(new Case(snapshot, expected));
        }
        return out;
    }

    private static void cross(List<List<Object>> c, int i, List<Object> acc, Map<String, List<Object>> out) {
        if (i == c.size()) { add(out, new ArrayList<>(acc)); return; }
        for (Object v : c.get(i)) {
            acc.add(v);
            cross(c, i + 1, acc, out);
            acc.remove(acc.size() - 1);
        }
    }

    private static void add(Map<String, List<Object>> out, List<Object> t) {
        StringBuilder k = new StringBuilder();
        for (Object o : t) k.append(o instanceof BigDecimal d ? "n" + d.toString() : "s" + o).append('\u0001');
        out.putIfAbsent(k.toString(), t);
    }

    // ---- candidates ------------------------------------------------------------------------------------------------

    private static List<Object> numericCandidates(Ir.Field f, List<String> literals) {
        int scale = f.impliedDecimals;
        BigDecimal ulp = BigDecimal.ONE.scaleByPowerOfTen(-scale);
        BigDecimal max = f.digits <= 0 ? BigDecimal.ZERO : new BigDecimal(BigInteger.TEN.pow(f.digits).subtract(BigInteger.ONE), scale);
        List<BigDecimal> raw = new ArrayList<>(List.of(BigDecimal.ZERO, ulp, max));
        if (f.signed) raw.addAll(List.of(ulp.negate(), max.negate()));
        for (String l : literals) {
            BigDecimal v = new BigDecimal(l).setScale(Math.max(scale, 0), RoundingMode.DOWN);
            raw.addAll(List.of(v, v.add(ulp), v.subtract(ulp)));
        }
        Map<String, Object> out = new LinkedHashMap<>();
        for (BigDecimal v : raw) {
            if (v.abs().compareTo(max) > 0 || (!f.signed && v.signum() < 0)) continue;
            BigDecimal s = v.setScale(scale, RoundingMode.DOWN);
            out.putIfAbsent(s.toString(), s);
            if (out.size() >= MAX_CANDIDATES) break;
        }
        return new ArrayList<>(out.values());
    }

    private static List<Object> textCandidates(Ir.Field f, List<String> literals, Set<Character> fills) {
        int n = Math.max(f.length, 1);
        List<String> raw = new ArrayList<>();
        raw.add(" ".repeat(n));
        for (String l : literals) {
            raw.add(l);
            if (!l.isEmpty()) raw.add((l.charAt(0) == 'A' ? 'B' : 'A') + l.substring(1));
        }
        for (char c : fills) raw.add(String.valueOf(c).repeat(n));
        raw.addAll(List.of("0".repeat(n), "A".repeat(n), "a".repeat(n), "A1".repeat(n), "9".repeat(n), "\u0000".repeat(n), "\u00FF".repeat(n)));
        Map<String, Object> out = new LinkedHashMap<>();
        for (String s : raw) {
            String fitted = s.length() >= n ? s.substring(0, n) : s + " ".repeat(n - s.length());
            out.putIfAbsent(fitted, fitted);
            if (out.size() >= MAX_CANDIDATES) break;
        }
        return new ArrayList<>(out.values());
    }

    private static void collect(Object m, List<String> nums, List<String> strs, Set<Character> fills) {
        switch (m) {
            case CNum n -> { collect(n.l(), nums, strs, fills); collect(n.r(), nums, strs, fills); }
            case CStrEq e -> { collect(e.l(), nums, strs, fills); collect(e.r(), nums, strs, fills); }
            case C88Num n -> { for (Num[] v : n.values()) for (Num x : v) collect(x, nums, strs, fills); }
            case C88Str n -> { for (Str v : n.values()) collect(v, nums, strs, fills); }
            case CSign s -> collect(s.x(), nums, strs, fills);
            case CAnd a -> { collect(a.l(), nums, strs, fills); collect(a.r(), nums, strs, fills); }
            case COr o -> { collect(o.l(), nums, strs, fills); collect(o.r(), nums, strs, fills); }
            case CNot x -> collect(x.x(), nums, strs, fills);
            case NBin b -> { collect(b.l(), nums, strs, fills); collect(b.r(), nums, strs, fills); }
            case NNeg x -> collect(x.x(), nums, strs, fills);
            case NLit l -> { if (!nums.contains(l.text())) nums.add(l.text()); }
            case SLit l -> { if (!strs.contains(l.text())) strs.add(l.text()); }
            case SFill f -> fills.add(f.c());
            default -> { }
        }
    }

    // ---- the faithful runtime evaluation ----------------------------------------------------------------------------

    /** A field of the item's declared layout over storage of its own (as SkeletonEmitter.fieldCtor declares it). */
    static CobField field(Ir.Field f) {
        Area a = new Area(Math.max(f.length, 1));
        a.fill((byte) ' ');
        return switch (f.category) {
            case "Group" -> CobField.group(f.field, a, 0, f.length, null, null);
            case "Alphanumeric", "Alphabetic" -> CobField.alnum(f.field, a, 0, f.length, f.justified, null, null);
            case "NumericEdited" -> CobField.edited(f.field, CobField.Cat.NUM_EDITED, a, 0, f.length, f.digits, f.impliedDecimals, f.blankWhenZero,
                    Edit.expand(f.pic), null, null);
            case "AlphanumericEdited" -> CobField.edited(f.field, CobField.Cat.ALNUM_EDITED, a, 0, f.length, 0, 0, false, Edit.expand(f.pic), null, null);
            case "Numeric" -> {
                CobField.Use use = switch (f.usage == null ? "" : f.usage) {
                    case "COMP-3" -> CobField.Use.PACKED;
                    case "COMP" -> CobField.Use.BINARY;
                    case "COMP-5" -> CobField.Use.COMP5;
                    default -> CobField.Use.DISPLAY;
                };
                yield CobField.num(f.field, use, a, 0, f.length, f.digits, f.impliedDecimals, f.signed, f.signSeparate, f.signLeading, null, null);
            }
            default -> throw new IllegalArgumentException("no case layout for a " + f.category + " item");
        };
    }

    static boolean cond(Ir.Node n, Map<Integer, CobField> fields, java.util.function.IntFunction<Ir.Field> dictionary) {
        return switch (n.k) {
            case "rel" -> {
                int c = arithmetic(n.l) || arithmetic(n.r) ? num(n.l, fields).compareTo(num(n.r, fields))
                        : CobRt.compare(operand(n.l, fields), operand(n.r, fields));
                yield switch (n.op) {
                    case "eq" -> c == 0;
                    case "ne" -> c != 0;
                    case "lt" -> c < 0;
                    case "le" -> c <= 0;
                    case "gt" -> c > 0;
                    case "ge" -> c >= 0;
                    default -> throw new IllegalArgumentException(n.op);
                };
            }
            case "and" -> cond(n.l, fields, dictionary) && cond(n.r, fields, dictionary);
            case "or" -> cond(n.l, fields, dictionary) || cond(n.r, fields, dictionary);
            case "not" -> !cond(n.l, fields, dictionary);
            case "c88" -> {
                Ir.Field c = dictionary.apply(n.id);
                List<Object[]> values = new ArrayList<>();
                for (Ir.Value v : c.values) {
                    Ir.Value hi = null;
                    if (v.hi != null) { hi = new Ir.Value(); hi.v = v.hi; hi.q = Boolean.TRUE.equals(v.hq); }
                    values.add(new Object[]{value(v), hi == null ? null : value(hi)});
                }
                yield CobRt.is88(fields.get(c.parentId), values.toArray(new Object[0][]));
            }
            case "class" -> {
                boolean t = "NUMERIC".equals(n.v) ? CobRt.isNumeric(operand(n.l, fields)) : CobRt.isAlphabetic(operand(n.l, fields), n.v);
                yield "not".equals(n.op) != t;
            }
            case "sign" -> {
                int s = num(n.l, fields).signum();
                boolean t = switch (n.v) {
                    case "POSITIVE" -> s > 0;
                    case "NEGATIVE" -> s < 0;
                    default -> s == 0;
                };
                yield "not".equals(n.op) != t;
            }
            default -> throw new IllegalArgumentException("condition kind " + n.k);
        };
    }

    /** Level-88 definitions of the program whose cases are being computed. */
    static final ThreadLocal<Map<Integer, Ir.Field>> CURRENT_88 = ThreadLocal.withInitial(HashMap::new);

    private static boolean arithmetic(Ir.Node n) {
        return "bin".equals(n.k) || "neg".equals(n.k) || "fn".equals(n.k);
    }

    private static Object value(Ir.Value v) {
        if (v.q) return v.v;
        Fig fig = fig(v.v.toUpperCase());
        if (fig != null) return fig;
        return new NumLit(RulePlanner.literalNum(v.v).text());
    }

    private static Fig fig(String u) {
        String f = SkeletonEmitter.figurative(u);
        if (f == null) return null;
        return Fig.valueOf(f.substring(4));
    }

    private static Object operand(Ir.Node n, Map<Integer, CobField> fields) {
        return switch (n.k) {
            case "ref" -> fields.get(n.id);
            case "num" -> new NumLit(RulePlanner.literalNum(n.v).text());
            case "str" -> n.v;
            case "fig" -> fig(n.v.toUpperCase());
            default -> throw new IllegalArgumentException("operand kind " + n.k);
        };
    }

    static BigDecimal num(Ir.Node n, Map<Integer, CobField> fields) {
        return switch (n.k) {
            case "ref" -> fields.get(n.id).dec();
            case "num" -> new NumLit(RulePlanner.literalNum(n.v).text()).v;
            case "bin" -> switch (n.op) {
                case "+" -> CobRt.add(num(n.l, fields), num(n.r, fields));
                case "-" -> CobRt.sub(num(n.l, fields), num(n.r, fields));
                case "*" -> CobRt.mul(num(n.l, fields), num(n.r, fields));
                default -> throw new IllegalArgumentException(n.op);
            };
            case "neg" -> num(n.l, fields).negate();
            case "fig" -> BigDecimal.ZERO;
            default -> throw new IllegalArgumentException("numeric kind " + n.k);
        };
    }
}
