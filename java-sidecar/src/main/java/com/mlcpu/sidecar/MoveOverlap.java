package com.mlcpu.sidecar;

import com.mlcpu.rt.RawMove;
import com.mlcpu.rt.RawMoveProfile;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.function.Function;

/**
 * Static classification of the (sender, receiver) pairs of MOVE statements and implicit moves (READ/RETURN INTO,
 * WRITE/REWRITE/RELEASE FROM) over the transpiler's own storage plan: the areas {@link SkeletonEmitter} allocates
 * (REDEFINES and the records of one FD share an area), the items' offsets, subscripts, reference modification and OCCURS
 * DEPENDING ON. The byte-range decision is {@link RawMove.Geometry}, the one the runtime applies, and the raw/conversion
 * split mirrors {@code CobRt.moveField}.
 *
 * <p>Classes: {@code non-storage-sender} (literal, figurative, function), {@code distinct-storage},
 * {@code same-storage-disjoint}, {@code identical}, {@code order-independent}, {@code shifted} (raw; a frontier unless a
 * profile covers it), {@code conversion-overlap} (overlapping, but the runtime decodes the sender before writing: not a
 * raw copy), {@code possible-dynamic} (subscript, reference modification or ODO length decide at run time),
 * {@code possible-alias} (LINKAGE storage a caller or pointer may alias), {@code unresolved}.
 */
final class MoveOverlap {
    static final Set<String> NOTABLE = Set.of("identical", "order-independent", "shifted", "conversion-overlap", "possible-dynamic",
            "possible-alias", "unresolved", "statement-frontier");

    private final Ir ir;
    private final Map<Integer, Ir.Field> byId;
    private final Map<Integer, String> areaOf;
    private final Set<String> linkageAreas;
    private final Set<String> externalAreas;
    private final Set<Integer> odoGroups;
    private final Function<String, String> fileArea;
    /** SET ADDRESS OF / ADDRESS OF anywhere, or a RECURSIVE program: LINKAGE may alias any storage. */
    private final boolean pointerAliasing;

    MoveOverlap(Ir ir, Map<Integer, Ir.Field> byId, Map<Integer, String> areaOf, Set<String> linkageAreas, Set<String> externalAreas,
                Set<Integer> odoGroups, Function<String, String> fileArea) {
        this.ir = ir;
        this.byId = byId;
        this.areaOf = areaOf;
        this.linkageAreas = linkageAreas;
        this.externalAreas = externalAreas;
        this.odoGroups = odoGroups;
        this.fileArea = fileArea;
        boolean pointers = ir.program != null && ir.program.recursive;
        if (ir.semantics != null && ir.semantics.paragraphs != null)
            for (Ir.SemParagraph p : ir.semantics.paragraphs) pointers |= addresses(p.statements);
        this.pointerAliasing = pointers;
    }

    private static boolean addresses(List<Ir.Stmt> stmts) {
        if (stmts == null) return false;
        for (Ir.Stmt s : stmts) {
            if (s.frontier != null && s.frontier.contains("ADDRESS OF")) return true;
            if (s.n != null) for (Ir.Node n : s.n.values()) if (addr(n)) return true;
            if (s.l != null) for (List<Ir.Node> l : s.l.values()) for (Ir.Node n : l) if (addr(n)) return true;
            if (s.blocks != null) for (List<Ir.Stmt> b : s.blocks.values()) if (addresses(b)) return true;
        }
        return false;
    }

    private static boolean addr(Ir.Node n) {
        if (n == null) return false;
        if ("addr".equals(n.k)) return true;
        if (addr(n.l) || addr(n.r) || addr(n.start) || addr(n.len)) return true;
        if (n.args != null) for (Ir.Node a : n.args) if (addr(a)) return true;
        return false;
    }

    static final class Unresolved extends RuntimeException {
        Unresolved(String m) { super(m, null, false, false); }
    }

    /** A byte span of an operand: exact, or (exact == false) the bound of every position it can take at run time. */
    record Span(String area, int pos, int len, boolean exact, List<String> dynamic, String item, Ir.Field field, boolean refmod) {
        Map<String, Object> json() {
            Map<String, Object> m = new LinkedHashMap<>();
            m.put("item", item);
            m.put("area", area);
            m.put("offset", pos);
            m.put("length", len);
            if (!exact) m.put("bound", dynamic);
            return m;
        }
    }

    static boolean special(Ir.Field f) {
        return f.parentId == null && f.line == 0 && Set.of("RETURN-CODE", "SORT-RETURN", "TALLY").contains(f.field);
    }

    private static Integer literal(Ir.Node n) {
        if (n == null || !"num".equals(n.k) || n.v == null) return null;
        try {
            return Integer.parseInt(n.v.startsWith("+") ? n.v.substring(1) : n.v);
        } catch (NumberFormatException e) {
            return null;
        }
    }

    Span span(Ir.Node n) {
        if (n == null || !"ref".equals(n.k)) throw new Unresolved("receiver is not a data item");
        Ir.Field f = n.id == null ? null : byId.get(n.id);
        if (f == null) throw new Unresolved("unknown data item " + n.v);
        if (f.lvl() == 66) throw new Unresolved("RENAMES item " + f.field + " has no layout");
        String area = special(f) ? "special:" + f.field : areaOf.get(f.id);
        if (area == null) throw new Unresolved("no storage area for " + f.field);
        int pos = f.offset, len = f.length;
        boolean exact = true;
        List<String> dynamic = new ArrayList<>();
        if (f.dims != null && !f.dims.isEmpty()) {
            List<Ir.Node> args = n.args == null ? List.of() : n.args;
            if (args.isEmpty()) throw new Unresolved("table item " + f.field + " without subscripts");
            int first = f.dims.size() - args.size();
            if (first < 0 || f.strides == null || f.strides.size() != f.dims.size()) throw new Unresolved("subscripts of " + f.field + " do not match its dimensions");
            int extra = 0;
            for (int i = 0; i < args.size(); i++) {
                Integer sub = literal(args.get(i));
                int stride = f.strides.get(first + i);
                if (sub != null && exact) pos += (sub - 1) * stride;
                else {
                    exact = false;
                    extra += (f.dims.get(first + i) - 1) * stride;
                }
            }
            if (!exact) {
                // the bound of every occurrence the subscripts can name (literal subscripts already placed are kept)
                pos = f.offset;
                extra = 0;
                for (int i = first; i < f.dims.size(); i++) extra += (f.dims.get(i) - 1) * f.strides.get(i);
                len += extra;
                dynamic.add("subscript");
            }
        }
        if (f.dependingOnId != null || odoGroups.contains(f.id)) {
            exact = false;
            dynamic.add("OCCURS DEPENDING ON");
        }
        boolean refmod = n.start != null;
        if (refmod) {
            Integer st = literal(n.start), ln = n.len == null ? null : literal(n.len);
            if (exact && st != null && (n.len == null || ln != null) && st >= 1 && st <= len) {
                pos += st - 1;
                len = ln != null ? ln : len - (st - 1);
            } else {
                exact = false;
                dynamic.add("reference modification");
            }
        }
        String item = f.field + (n.args != null && !n.args.isEmpty() ? "(" + n.args.size() + " subscripts)" : "") + (refmod ? "(refmod)" : "");
        return new Span(area, pos, len, exact, dynamic, item, f, refmod);
    }

    /** A file's (or SD's) record area as a group sender: its length varies with the record read. */
    Span fileRecord(String file, int maxRecord) {
        return new Span(fileArea.apply(file), 0, Math.max(maxRecord, 1), false, List.of("record length"), file + " record", null, false);
    }

    private enum Cat { GROUP, ALNUM, NUMERIC, NUM_EDITED, ALNUM_EDITED, INDEX, POINTER }

    private static Cat cat(Span s) {
        if (s.refmod()) return Cat.ALNUM;
        if (s.field() == null) return Cat.GROUP;
        if (special(s.field())) return Cat.NUMERIC;
        return switch (s.field().category == null ? "" : s.field().category) {
            case "Group" -> Cat.GROUP;
            case "Alphanumeric", "Alphabetic" -> Cat.ALNUM;
            case "NumericEdited" -> Cat.NUM_EDITED;
            case "AlphanumericEdited" -> Cat.ALNUM_EDITED;
            case "Index" -> Cat.INDEX;
            case "Pointer" -> Cat.POINTER;
            default -> Cat.NUMERIC;
        };
    }

    private static boolean display(Ir.Field f) {
        return f.usage == null || f.usage.isEmpty() || "DISPLAY".equals(f.usage);
    }

    /** CobRt.moveField: null when the receiver gets a conversion of the sender, else whether the raw copy is right-justified. */
    private static Boolean rawRight(Span s, Span d) {
        Cat sc = cat(s), dc = cat(d);
        if (sc == Cat.GROUP || dc == Cat.GROUP) return false;
        return switch (dc) {
            case ALNUM -> sc != Cat.NUMERIC || (display(s.field()) && !s.field().signed) ? (!d.refmod() && d.field() != null && d.field().justified) : null;
            case ALNUM_EDITED -> null;
            case NUMERIC, INDEX, NUM_EDITED, POINTER -> switch (sc) {
                case NUMERIC, INDEX, NUM_EDITED -> null;
                default -> dc == Cat.NUM_EDITED ? Boolean.FALSE : null;
            };
            default -> false;
        };
    }

    static RawMove.Kind senderKind(Span s) {
        Cat c = cat(s);
        if (c == Cat.GROUP) return RawMove.Kind.GROUP;
        if (c == Cat.ALNUM) return RawMove.Kind.ALNUM;
        Ir.Field f = s.field();
        if (c == Cat.NUMERIC && f != null && !special(f) && display(f) && !f.signed && f.impliedDecimals == 0) return RawMove.Kind.UNSIGNED_DISPLAY_INTEGER;
        return RawMove.Kind.OTHER;
    }

    static RawMove.Kind receiverKind(Span d) {
        Cat c = cat(d);
        return c == Cat.GROUP ? RawMove.Kind.GROUP : c == Cat.ALNUM ? RawMove.Kind.ALNUM : RawMove.Kind.OTHER;
    }

    private boolean linkage(String area) { return linkageAreas.contains(area); }

    private String aliasReason(String a, String b) {
        if (a.startsWith("special:") || b.startsWith("special:")) return null;
        boolean la = linkage(a), lb = linkage(b);
        if (!la && !lb) return null;
        if (la && lb) return "two LINKAGE records (a caller may pass overlapping storage)";
        if (externalAreas.contains(a) || externalAreas.contains(b)) return "LINKAGE and an EXTERNAL record (a caller may pass the EXTERNAL record)";
        if (pointerAliasing) return "LINKAGE with ADDRESS OF or RECURSIVE (pointer/recursive aliasing)";
        return null;
    }

    /** Classifies one pair; {@code sender} null means a non-storage sender. Never throws. */
    Map<String, Object> classify(Ir.Node src, Ir.Node dst) {
        Map<String, Object> m = new LinkedHashMap<>();
        if (src == null || !"ref".equals(src.k)) {
            m.put("class", "non-storage-sender");
            return m;
        }
        try {
            return classify(span(src), span(dst));
        } catch (Unresolved e) {
            m.put("class", "unresolved");
            m.put("reason", e.getMessage());
            return m;
        }
    }

    Map<String, Object> classify(Span s, Span d) {
        Map<String, Object> m = new LinkedHashMap<>();
        m.put("sender", s.json());
        m.put("receiver", d.json());
        if (!s.area().equals(d.area())) {
            String why = aliasReason(s.area(), d.area());
            m.put("class", why == null ? "distinct-storage" : "possible-alias");
            if (why != null) m.put("reason", why);
            return m;
        }
        Boolean right = rawRight(s, d);
        m.put("copy", right == null ? "conversion" : right ? "raw JUSTIFIED" : "raw");
        if (!s.exact() || !d.exact()) {
            if (!intersects(s, d)) { m.put("class", "same-storage-disjoint"); return m; }
            List<String> why = new ArrayList<>(s.dynamic());
            for (String w : d.dynamic()) if (!why.contains(w)) why.add(w);
            m.put("class", "possible-dynamic");
            m.put("reason", String.join(", ", why) + " decide(s) the overlap at run time");
            return m;
        }
        RawMove.Geometry g = new RawMove.Geometry(s.pos(), s.len(), d.pos(), d.len(), Boolean.TRUE.equals(right));
        if (g.overlap() == RawMove.Overlap.DISJOINT) { m.put("class", "same-storage-disjoint"); return m; }
        if (right == null) { m.put("class", "conversion-overlap"); return m; }
        m.put("copied", g.copied());
        m.put("shift", g.shift());
        if (g.padded()) m.put("padded", g.padLen());
        switch (g.overlap()) {
            case IDENTICAL -> m.put("class", "identical");
            case ORDER_INDEPENDENT -> m.put("class", "order-independent");
            default -> {
                m.put("class", "shifted");
                RawMove.Kind sk = senderKind(s), rk = receiverKind(d);
                m.put("kinds", sk + ">" + rk);
                m.put("policy", "default FRONTIER");
                for (String id : RawMoveProfile.builtInIds()) {
                    RawMoveProfile.Rule rule = RawMoveProfile.builtIn(id).rule(sk, rk, g);
                    m.put("profile:" + id, rule == null ? "frontier (shape not captured)" : rule.id());
                }
            }
        }
        return m;
    }

    private static boolean intersects(Span a, Span b) {
        return a.len() > 0 && b.len() > 0 && a.pos() < b.pos() + b.len() && b.pos() < a.pos() + a.len();
    }
}
