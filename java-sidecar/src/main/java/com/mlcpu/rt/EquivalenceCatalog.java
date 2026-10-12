package com.mlcpu.rt;

import java.math.BigDecimal;
import java.math.BigInteger;
import java.math.MathContext;
import java.math.RoundingMode;
import java.time.DateTimeException;
import java.time.LocalDate;
import java.time.temporal.ChronoUnit;
import java.util.ArrayList;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Set;
import java.util.function.Predicate;

/**
 * The JDK candidates of the library-equivalence probe ({@link EquivalenceProbe}): decimal and date operands read from
 * linkage fields, expressions over them ({@code BigDecimal.round/setScale/multiply/divide/add/subtract} with a
 * {@code MathContext}, {@code LocalDate.plus}, {@code ChronoUnit.between}, ...), the output representations that write
 * a result back into fields (a coefficient/exponent pair stripped of trailing zeros, a shift count, a scaled field, a
 * day number, a YYYYMMDD date), and the enumeration of hypotheses in order of simplicity.
 */
final class EquivalenceCatalog {
    private EquivalenceCatalog() {}

    static final RoundingMode[] MODES = {RoundingMode.UP, RoundingMode.DOWN, RoundingMode.CEILING, RoundingMode.FLOOR,
        RoundingMode.HALF_UP, RoundingMode.HALF_DOWN, RoundingMode.HALF_EVEN};
    static final ChronoUnit[] UNITS = {ChronoUnit.DAYS, ChronoUnit.WEEKS, ChronoUnit.MONTHS, ChronoUnit.YEARS, ChronoUnit.DECADES};
    static final BigDecimal HUNDRED = BigDecimal.valueOf(100);

    /** An input that does not decode (invalid packed digits, a day number outside the calendar of its epoch). */
    static final class Invalid extends RuntimeException {
        Invalid(String what) { super("invalid " + what, null, false, false); }
    }

    /** One evaluation: the CALL's arguments before it, the enum constant tried for the selector, decoded fields. */
    static final class Env {
        final byte[][] args;
        final BigDecimal[] cache;
        final boolean[] cached;
        RoundingMode mode;
        ChronoUnit unit;

        Env(byte[][] args, int fields) {
            this.args = args;
            this.cache = new BigDecimal[fields];
            this.cached = new boolean[fields];
        }

        /** A numeric field's value (null when its bytes are not valid for its usage). */
        BigDecimal num(EquivalenceProbe.PField f) {
            if (!cached[f.id]) {
                cache[f.id] = f.num(args);
                cached[f.id] = true;
            }
            return cache[f.id];
        }

        BigDecimal need(EquivalenceProbe.PField f) {
            BigDecimal v = num(f);
            if (v == null) throw new Invalid(f.name);
            return v;
        }
    }

    // ------------------------------------------------------------------ operands

    /** A decimal operand: a coefficient with an exponent (or scale) field, or one numeric field. */
    static final class Src {
        final String label;
        final String ident;
        final EquivalenceProbe.PField coef;
        final EquivalenceProbe.PField exp;
        final boolean scaleField;

        Src(String label, String ident, EquivalenceProbe.PField coef, EquivalenceProbe.PField exp, boolean scaleField) {
            this.label = label;
            this.ident = ident;
            this.coef = coef;
            this.exp = exp;
            this.scaleField = scaleField;
        }

        boolean pair() { return exp != null; }

        BigDecimal value(Env e) {
            BigDecimal c = e.need(coef);
            if (exp == null) return c;
            int ev = e.need(exp).intValue();
            return new BigDecimal(c.unscaledValue(), c.scale() + (scaleField ? ev : -ev));
        }

        String binding() {
            if (exp == null)
                return ident + " = " + coef.name + (coef.scale > 0 ? " (a BigDecimal of scale " + coef.scale + ")" : " (a BigDecimal of scale 0)");
            return ident + " = new BigDecimal(" + coef.name + ", " + (scaleField ? exp.name : "-" + exp.name) + ") (the value " + coef.name
                   + " × 10^" + (scaleField ? "-" : "") + exp.name + ")";
        }

        void fields(Set<EquivalenceProbe.PField> s) {
            s.add(coef);
            if (exp != null) s.add(exp);
        }
    }

    /** An integer parameter (a precision, a scale, a count). */
    static final class IntP {
        final EquivalenceProbe.PField f;
        final String ident;

        IntP(EquivalenceProbe.PField f) {
            this.f = f;
            this.ident = EquivalenceProbe.camel(f.name);
        }

        int value(Env e) {
            BigDecimal v = e.need(f);
            if (v.abs().compareTo(BigDecimal.valueOf(Integer.MAX_VALUE)) > 0) throw new Invalid(f.name);
            return v.intValue();
        }
    }

    /** A day-number encoding of dates: day number = epochDay + offset, defined from min to max. */
    record Epoch(String label, long offset, LocalDate min, LocalDate max, String base) {
        static Epoch of(String label, LocalDate dayOne, LocalDate min, LocalDate max) {
            return new Epoch(label, 1 - dayOne.toEpochDay(), min, max, javaDate(dayOne.minusDays(1)));
        }

        LocalDate date(long day) {
            long ed = day - offset;
            if (ed < min.toEpochDay() || ed > max.toEpochDay()) throw new Invalid("day number " + day);
            return LocalDate.ofEpochDay(ed);
        }

        /** The day number of a date, or null outside the encoding's calendar. */
        Long day(LocalDate d) {
            if (d.isBefore(min) || d.isAfter(max)) return null;
            return d.toEpochDay() + offset;
        }

        String toDate(String v) { return base + ".plusDays(" + v + ")"; }

        String toDay(String v) { return "ChronoUnit.DAYS.between(" + base + ", " + v + ")"; }
    }

    static String javaDate(LocalDate d) { return "LocalDate.of(" + d.getYear() + ", " + d.getMonthValue() + ", " + d.getDayOfMonth() + ")"; }

    static final LocalDate MAX_DATE = LocalDate.of(9999, 12, 31);
    static final Epoch INTEGER_OF_DATE = Epoch.of("COBOL INTEGER-OF-DATE (day 1 = 1601-01-01)", LocalDate.of(1601, 1, 1), LocalDate.of(1601, 1, 1), MAX_DATE);
    static final Epoch EPOCH_DAY = new Epoch("epoch day (0 = 1970-01-01)", 0, LocalDate.of(1, 1, 1), MAX_DATE, "LocalDate.EPOCH");
    static final Epoch LILIAN = Epoch.of("Lilian day (day 1 = 1582-10-15)", LocalDate.of(1582, 10, 15), LocalDate.of(1582, 10, 15), MAX_DATE);
    static final Epoch RATA_DIE = Epoch.of("Rata Die (day 1 = 0001-01-01)", LocalDate.of(1, 1, 1), LocalDate.of(1, 1, 1), MAX_DATE);
    static final List<Epoch> STANDARD_EPOCHS = List.of(INTEGER_OF_DATE, EPOCH_DAY, LILIAN, RATA_DIE);

    /** A date operand: a day number under an epoch, or a YYYYMMDD number (epoch null). */
    static final class DSrc {
        final EquivalenceProbe.PField f;
        final Epoch epoch;
        final String ident;

        DSrc(EquivalenceProbe.PField f, Epoch epoch) {
            this.f = f;
            this.epoch = epoch;
            this.ident = EquivalenceProbe.camel(f.name) + "Date";
        }

        LocalDate value(Env e) {
            BigDecimal v = e.need(f);
            try {
                return epoch != null ? epoch.date(v.longValue()) : ymd(v.longValue());
            } catch (Invalid x) {
                throw new Invalid(f.name);
            }
        }

        String binding() {
            return ident + " = " + (epoch != null ? epoch.toDate(f.name) + " (" + f.name + " is a " + epoch.label() + ")"
                : "LocalDate.parse(" + f.name + " as 8 digits, DateTimeFormatter.BASIC_ISO_DATE)");
        }
    }

    static LocalDate ymd(long v) {
        if (v < 101 || v > 99991231) throw new Invalid("date " + v);
        try {
            return LocalDate.of((int) (v / 10000), (int) (v / 100 % 100), (int) (v % 100));
        } catch (DateTimeException x) {
            throw new Invalid("date " + v);
        }
    }

    // ------------------------------------------------------------------ expressions

    /** An expression over the CALL's inputs; evaluates to a BigDecimal ('D'), a Long ('I') or a LocalDate ('T'). */
    abstract static class Ex {
        abstract Object eval(Env e);

        abstract String java();

        abstract void fields(Set<EquivalenceProbe.PField> s);

        abstract char type();

        boolean usesMode() { return false; }

        boolean usesUnit() { return false; }

        /** The decimal operands, for the condition learner's features. */
        void sources(List<Src> out) {}

        /** The exact intermediate a rounded operation starts from (null when there is none), for the condition learner. */
        BigDecimal exactIntermediate(Env e) { return null; }

        String exactIntermediateJava() { return null; }
    }

    static BigDecimal dec(Object o) { return o instanceof BigDecimal b ? b : BigDecimal.valueOf((Long) o); }

    static final class DLeaf extends Ex {
        final Src s;

        DLeaf(Src s) { this.s = s; }

        Object eval(Env e) { return s.value(e); }

        String java() { return s.ident; }

        void fields(Set<EquivalenceProbe.PField> f) { s.fields(f); }

        char type() { return 'D'; }

        void sources(List<Src> out) { out.add(s); }
    }

    /** A value known to have further nonzero digits beyond its last one (a sticky digit): x + sign(x)·ulp/10. */
    static final class Tail extends Ex {
        final Src s;

        Tail(Src s) { this.s = s; }

        Object eval(Env e) {
            BigDecimal v = s.value(e);
            return v.add(BigDecimal.valueOf(v.signum() < 0 ? -1 : 1, v.scale() + 1));
        }

        String java() { return s.ident + ".add(BigDecimal.valueOf(" + s.ident + ".signum() < 0 ? -1 : 1, " + s.ident + ".scale() + 1))"; }

        void fields(Set<EquivalenceProbe.PField> f) { s.fields(f); }

        char type() { return 'D'; }

        void sources(List<Src> out) { out.add(s); }
    }

    static final class Round extends Ex {
        final Ex x;
        final IntP p;

        Round(Ex x, IntP p) {
            this.x = x;
            this.p = p;
        }

        Object eval(Env e) { return dec(x.eval(e)).round(new MathContext(p.value(e), e.mode)); }

        String java() { return x.java() + ".round(new MathContext(" + p.ident + ", mode))"; }

        void fields(Set<EquivalenceProbe.PField> f) {
            x.fields(f);
            f.add(p.f);
        }

        char type() { return 'D'; }

        boolean usesMode() { return true; }

        void sources(List<Src> out) { x.sources(out); }
    }

    static final class SetScale extends Ex {
        final Ex x;
        final IntP p;
        final boolean negate;

        SetScale(Ex x, IntP p, boolean negate) {
            this.x = x;
            this.p = p;
            this.negate = negate;
        }

        Object eval(Env e) {
            int n = p.value(e);
            return dec(x.eval(e)).setScale(negate ? -n : n, e.mode);
        }

        String java() { return x.java() + ".setScale(" + (negate ? "-" : "") + p.ident + ", mode)"; }

        void fields(Set<EquivalenceProbe.PField> f) {
            x.fields(f);
            f.add(p.f);
        }

        char type() { return 'D'; }

        boolean usesMode() { return true; }

        void sources(List<Src> out) { x.sources(out); }
    }

    /** x op y: exact (p null, no mode), with a MathContext(p, mode), or a divide to scale p. */
    static final class Bin extends Ex {
        final char op;
        final Ex x, y;
        final IntP p;
        final boolean toScale;

        Bin(char op, Ex x, Ex y, IntP p, boolean toScale) {
            this.op = op;
            this.x = x;
            this.y = y;
            this.p = p;
            this.toScale = toScale;
        }

        static String name(char op) {
            return switch (op) {
                case 'M' -> "multiply";
                case 'D' -> "divide";
                case 'A' -> "add";
                default -> "subtract";
            };
        }

        Object eval(Env e) {
            BigDecimal a = dec(x.eval(e)), b = dec(y.eval(e));
            if (p == null) {
                return switch (op) {
                    case 'M' -> a.multiply(b);
                    case 'A' -> a.add(b);
                    case 'S' -> a.subtract(b);
                    default -> a.divide(b);
                };
            }
            if (toScale) return a.divide(b, p.value(e), e.mode);
            MathContext mc = new MathContext(p.value(e), e.mode);
            return switch (op) {
                case 'M' -> a.multiply(b, mc);
                case 'A' -> a.add(b, mc);
                case 'S' -> a.subtract(b, mc);
                default -> a.divide(b, mc);
            };
        }

        String java() {
            if (p == null) return x.java() + "." + name(op) + "(" + y.java() + ")";
            if (toScale) return x.java() + ".divide(" + y.java() + ", " + p.ident + ", mode)";
            return x.java() + "." + name(op) + "(" + y.java() + ", new MathContext(" + p.ident + ", mode))";
        }

        void fields(Set<EquivalenceProbe.PField> f) {
            x.fields(f);
            y.fields(f);
            if (p != null) f.add(p.f);
        }

        char type() { return 'D'; }

        boolean usesMode() { return p != null; }

        void sources(List<Src> out) {
            x.sources(out);
            y.sources(out);
        }

        BigDecimal exactIntermediate(Env e) {
            if (op == 'D') return null;
            BigDecimal a = dec(x.eval(e)), b = dec(y.eval(e));
            return switch (op) {
                case 'M' -> a.multiply(b);
                case 'A' -> a.add(b);
                default -> a.subtract(b);
            };
        }

        String exactIntermediateJava() { return op == 'D' ? null : x.java() + "." + name(op) + "(" + y.java() + ")"; }
    }

    /** inner.setScale(q, mode): an operation whose result is then brought to a scale (Money.of after multipliedBy, ...). */
    static final class Rescale extends Ex {
        final Ex inner;
        final IntP q;

        Rescale(Ex inner, IntP q) {
            this.inner = inner;
            this.q = q;
        }

        Object eval(Env e) { return dec(inner.eval(e)).setScale(q.value(e), e.mode); }

        String java() { return inner.java() + ".setScale(" + q.ident + ", mode)"; }

        void fields(Set<EquivalenceProbe.PField> f) {
            inner.fields(f);
            f.add(q.f);
        }

        char type() { return 'D'; }

        boolean usesMode() { return true; }

        void sources(List<Src> out) { inner.sources(out); }

        BigDecimal exactIntermediate(Env e) { return inner.exactIntermediate(e); }

        String exactIntermediateJava() { return inner.exactIntermediateJava(); }
    }

    /** MathUtil.percentageOf: x × (y ÷ 100), both rounded with MathContext(p, mode). */
    static final class Pct extends Ex {
        final Ex x, y;
        final IntP p;

        Pct(Ex x, Ex y, IntP p) {
            this.x = x;
            this.y = y;
            this.p = p;
        }

        Object eval(Env e) {
            MathContext mc = new MathContext(p.value(e), e.mode);
            return dec(x.eval(e)).multiply(dec(y.eval(e)).divide(HUNDRED, mc), mc);
        }

        String java() {
            return x.java() + ".multiply(" + y.java() + ".divide(BigDecimal.valueOf(100), new MathContext(" + p.ident + ", mode)), new MathContext("
                   + p.ident + ", mode))";
        }

        void fields(Set<EquivalenceProbe.PField> f) {
            x.fields(f);
            y.fields(f);
            f.add(p.f);
        }

        char type() { return 'D'; }

        boolean usesMode() { return true; }

        void sources(List<Src> out) {
            x.sources(out);
            y.sources(out);
        }
    }

    /** An integer property of a decimal: precision(), scale(), signum(). */
    static final class DProp extends Ex {
        final Ex x;
        final int which;

        DProp(Ex x, int which) {
            this.x = x;
            this.which = which;
        }

        static final String[] NAMES = {"precision()", "scale()", "signum()", "stripTrailingZeros().scale()"};

        Object eval(Env e) {
            BigDecimal v = dec(x.eval(e));
            return (long) switch (which) {
                case 0 -> v.precision();
                case 1 -> v.scale();
                case 2 -> v.signum();
                default -> v.signum() == 0 ? 0 : v.stripTrailingZeros().scale();
            };
        }

        String java() { return x.java() + "." + NAMES[which]; }

        void fields(Set<EquivalenceProbe.PField> f) { x.fields(f); }

        char type() { return 'I'; }

        void sources(List<Src> out) { x.sources(out); }
    }

    static final class TLeaf extends Ex {
        final DSrc s;

        TLeaf(DSrc s) { this.s = s; }

        Object eval(Env e) { return s.value(e); }

        String java() { return s.ident; }

        void fields(Set<EquivalenceProbe.PField> f) { f.add(s.f); }

        char type() { return 'T'; }
    }

    static final class Plus extends Ex {
        final Ex d;
        final IntP n;

        Plus(Ex d, IntP n) {
            this.d = d;
            this.n = n;
        }

        Object eval(Env e) { return ((LocalDate) d.eval(e)).plus(n.value(e), e.unit); }

        String java() { return d.java() + ".plus(" + n.ident + ", unit)"; }

        void fields(Set<EquivalenceProbe.PField> f) {
            d.fields(f);
            f.add(n.f);
        }

        char type() { return 'T'; }

        boolean usesUnit() { return true; }
    }

    static final class Between extends Ex {
        final Ex d1, d2;

        Between(Ex d1, Ex d2) {
            this.d1 = d1;
            this.d2 = d2;
        }

        Object eval(Env e) { return e.unit.between((LocalDate) d1.eval(e), (LocalDate) d2.eval(e)); }

        String java() { return "unit.between(" + d1.java() + ", " + d2.java() + ")"; }

        void fields(Set<EquivalenceProbe.PField> f) {
            d1.fields(f);
            d2.fields(f);
        }

        char type() { return 'I'; }

        boolean usesUnit() { return true; }
    }

    static final class TProp extends Ex {
        final Ex d;
        final int which;

        TProp(Ex d, int which) {
            this.d = d;
            this.which = which;
        }

        static final String[] NAMES = {"lengthOfMonth()", "lengthOfYear()", "getYear()", "getMonthValue()", "getDayOfMonth()",
            "getDayOfYear()", "getDayOfWeek().getValue()", "isLeapYear() ? 1 : 0"};

        Object eval(Env e) {
            LocalDate v = (LocalDate) d.eval(e);
            return (long) switch (which) {
                case 0 -> v.lengthOfMonth();
                case 1 -> v.lengthOfYear();
                case 2 -> v.getYear();
                case 3 -> v.getMonthValue();
                case 4 -> v.getDayOfMonth();
                case 5 -> v.getDayOfYear();
                case 6 -> v.getDayOfWeek().getValue();
                default -> v.isLeapYear() ? 1 : 0;
            };
        }

        String java() { return which == 7 ? "(" + d.java() + ".isLeapYear() ? 1 : 0)" : d.java() + "." + NAMES[which]; }

        void fields(Set<EquivalenceProbe.PField> f) { d.fields(f); }

        char type() { return 'I'; }
    }

    static final class With extends Ex {
        final Ex d;
        final IntP n;
        final int which;

        With(Ex d, IntP n, int which) {
            this.d = d;
            this.n = n;
            this.which = which;
        }

        static final String[] NAMES = {"withDayOfMonth", "withDayOfYear", "withMonth"};

        Object eval(Env e) {
            LocalDate v = (LocalDate) d.eval(e);
            int k = n.value(e);
            return switch (which) {
                case 0 -> v.withDayOfMonth(k);
                case 1 -> v.withDayOfYear(k);
                default -> v.withMonth(k);
            };
        }

        String java() { return d.java() + "." + NAMES[which] + "(" + n.ident + ")"; }

        void fields(Set<EquivalenceProbe.PField> f) {
            d.fields(f);
            f.add(n.f);
        }

        char type() { return 'T'; }
    }

    /** LocalDate.of(n, month, day): the year end (12-31) or start (01-01) of year n. */
    static final class OfYear extends Ex {
        final IntP n;
        final int month, day;

        OfYear(IntP n, int month, int day) {
            this.n = n;
            this.month = month;
            this.day = day;
        }

        Object eval(Env e) { return LocalDate.of(n.value(e), month, day); }

        String java() { return "LocalDate.of(" + n.ident + ", " + month + ", " + day + ")"; }

        void fields(Set<EquivalenceProbe.PField> f) { f.add(n.f); }

        char type() { return 'T'; }
    }

    // ------------------------------------------------------------------ output representations

    /** Where a result goes: false from encode when it cannot be represented there. */
    abstract static class Target {
        abstract boolean encode(Object r, Env env, byte[][] out);

        abstract List<EquivalenceProbe.PField> fields();

        abstract char type();

        /** The field mapping, as the prompt states it (the result is called r). */
        abstract String describe();

        String key() {
            StringBuilder sb = new StringBuilder();
            for (EquivalenceProbe.PField f : fields()) sb.append(f.id).append(',');
            return sb.toString();
        }
    }

    static final int STRIPPED = 0, EXACT = 1, DROPPED = 2;

    /** A coefficient/exponent pair: stripped of trailing zeros, as the BigDecimal is, or as kept digits and a shift count. */
    static final class PairT extends Target {
        final EquivalenceProbe.PField coef, exp;
        final boolean scaleField;
        final int repr;
        final Src base;
        final IntP p;

        PairT(EquivalenceProbe.PField coef, EquivalenceProbe.PField exp, boolean scaleField, int repr, Src base, IntP p) {
            this.coef = coef;
            this.exp = exp;
            this.scaleField = scaleField;
            this.repr = repr;
            this.base = base;
            this.p = p;
        }

        boolean encode(Object r, Env env, byte[][] out) {
            BigDecimal v = dec(r);
            BigInteger c;
            long e;
            if (repr == STRIPPED) {
                if (v.signum() == 0) {
                    c = BigInteger.ZERO;
                    e = 0;
                } else {
                    BigDecimal s = v.stripTrailingZeros();
                    c = s.unscaledValue();
                    e = -s.scale();
                }
            } else if (repr == EXACT) {
                c = v.unscaledValue();
                e = -v.scale();
            } else {
                int shift = Math.max(0, base.value(env).precision() - p.value(env));
                BigDecimal q = v.movePointLeft(shift);
                if (q.signum() != 0 && q.stripTrailingZeros().scale() > 0) return false;
                c = q.setScale(0, RoundingMode.UNNECESSARY).unscaledValue();
                e = shift;
            }
            return coef.store(out, new BigDecimal(c)) && exp.store(out, BigDecimal.valueOf(scaleField ? -e : e));
        }

        List<EquivalenceProbe.PField> fields() { return List.of(coef, exp); }

        char type() { return 'D'; }

        String describe() {
            String ex = scaleField ? exp.name + " = r.scale()" : exp.name + " = -r.scale()";
            return switch (repr) {
                case STRIPPED -> "r = (the result).stripTrailingZeros(); " + coef.name + " = r.unscaledValue(), " + ex + " (a zero result: "
                                 + coef.name + " = 0, " + exp.name + " = 0)";
                case EXACT -> coef.name + " = r.unscaledValue(), " + ex + " (r exactly as the operation returns it, trailing zeros kept)";
                default -> exp.name + " = max(0, " + base.ident + ".precision() - " + p.ident + ") (the digits dropped), " + coef.name
                           + " = r.movePointLeft(" + exp.name + ") (the digits kept: r = " + coef.name + " × 10^" + exp.name + ")";
            };
        }
    }

    /** One numeric field holding the result exactly (no rounding or truncation by the store). */
    static final class NumT extends Target {
        final EquivalenceProbe.PField f;

        NumT(EquivalenceProbe.PField f) { this.f = f; }

        boolean encode(Object r, Env env, byte[][] out) { return f.store(out, dec(r)); }

        List<EquivalenceProbe.PField> fields() { return List.of(f); }

        char type() { return 'D'; }

        String describe() { return f.name + " = r" + (f.scale > 0 ? " (scale " + f.scale + "; r fits it exactly)" : ""); }
    }

    static final class DayT extends Target {
        final EquivalenceProbe.PField f;
        final Epoch epoch;

        DayT(EquivalenceProbe.PField f, Epoch epoch) {
            this.f = f;
            this.epoch = epoch;
        }

        boolean encode(Object r, Env env, byte[][] out) {
            Long d = epoch.day((LocalDate) r);
            return d != null && f.store(out, BigDecimal.valueOf(d));
        }

        List<EquivalenceProbe.PField> fields() { return List.of(f); }

        char type() { return 'T'; }

        String describe() { return f.name + " = " + epoch.toDay("r") + " (a " + epoch.label() + ")"; }
    }

    static final class YmdT extends Target {
        final EquivalenceProbe.PField f;

        YmdT(EquivalenceProbe.PField f) { this.f = f; }

        boolean encode(Object r, Env env, byte[][] out) {
            LocalDate d = (LocalDate) r;
            if (d.getYear() < 1 || d.getYear() > 9999) return false;
            return f.store(out, BigDecimal.valueOf(d.getYear() * 10000L + d.getMonthValue() * 100L + d.getDayOfMonth()));
        }

        List<EquivalenceProbe.PField> fields() { return List.of(f); }

        char type() { return 'T'; }

        String describe() { return f.name + " = r as yyyyMMdd (r.format(DateTimeFormatter.BASIC_ISO_DATE))"; }
    }

    // ------------------------------------------------------------------ hypotheses

    /** A candidate: an expression, where its result goes, and the selector field that codes its RoundingMode or ChronoUnit. */
    static final class Hyp {
        final Ex ex;
        final Target target;
        final EquivalenceProbe.PField sel;
        final char enumKind;
        final int rank;
        final String family;

        Hyp(Ex ex, Target target, EquivalenceProbe.PField sel, int rank, String family) {
            this.ex = ex;
            this.target = target;
            this.sel = sel;
            this.enumKind = ex.usesMode() ? 'M' : ex.usesUnit() ? 'U' : 0;
            this.rank = rank;
            this.family = family;
        }

        Object[] constants() {
            return enumKind == 'M' ? MODES : enumKind == 'U' ? UNITS : new Object[]{null};
        }

        Set<EquivalenceProbe.PField> inputs() {
            Set<EquivalenceProbe.PField> s = new LinkedHashSet<>();
            ex.fields(s);
            if (sel != null) s.add(sel);
            return s;
        }
    }

    /** What a group offers the catalog: its operands, parameters, selectors and output targets. */
    static final class Shape {
        final List<Src> srcs = new ArrayList<>();
        final List<IntP> precs = new ArrayList<>();      // small integers, never negative: precisions, scales
        final List<IntP> params = new ArrayList<>();     // small integers (may be negative)
        final List<IntP> counts = new ArrayList<>();     // any integer (date arithmetic)
        final List<EquivalenceProbe.PField> modeSels = new ArrayList<>();
        final List<EquivalenceProbe.PField> unitSels = new ArrayList<>();
        final List<Target> decTargets = new ArrayList<>();
        final List<Target> intTargets = new ArrayList<>();
        final List<DSrc> dsrcs = new ArrayList<>();
        final List<Target> dateTargets = new ArrayList<>();
        /** Pair targets per (coef, exp), for the shift representation that needs the operand and the precision. */
        final List<EquivalenceProbe.PField[]> pairTargets = new ArrayList<>();
        final List<Boolean> pairScale = new ArrayList<>();
    }

    /**
     * Every hypothesis of the catalog for a group, simplest first, until {@code sink} returns false. Families: identity
     * (a value normalized or converted), round, setScale, exact arithmetic, arithmetic with a MathContext, divide to a
     * scale, an operation brought to a scale, percentage, rounding of a value with a sticky digit, decimal properties,
     * and the date operations.
     */
    static void enumerate(Shape s, Predicate<Hyp> sink) {
        int[] rank = {0};
        // identity / normalization
        for (Src x : s.srcs)
            for (Target t : s.decTargets)
                if (!emit(sink, new Hyp(new DLeaf(x), t, null, rank[0]++, "identity"))) return;
        // round(MathContext)
        for (EquivalenceProbe.PField sel : s.modeSels)
            for (Src x : s.srcs)
                for (IntP p : s.precs) {
                    Ex ex = new Round(new DLeaf(x), p);
                    for (Target t : withShift(s, s.decTargets, x, p))
                        if (!emit(sink, new Hyp(ex, t, sel, rank[0]++, "round"))) return;
                }
        // setScale(n, mode), setScale(-n, mode)
        for (EquivalenceProbe.PField sel : s.modeSels)
            for (Src x : s.srcs)
                for (IntP p : s.params)
                    for (boolean neg : new boolean[]{false, true})
                        for (Target t : s.decTargets)
                            if (!emit(sink, new Hyp(new SetScale(new DLeaf(x), p, neg), t, sel, rank[0]++, "setScale"))) return;
        // exact arithmetic
        for (char op : new char[]{'M', 'A', 'S'})
            for (Src x : s.srcs)
                for (Src y : s.srcs)
                    if (x != y && ordered(op, x, y))
                        for (Target t : s.decTargets)
                            if (!emit(sink, new Hyp(new Bin(op, new DLeaf(x), new DLeaf(y), null, false), t, null, rank[0]++, "exact"))) return;
        // arithmetic with a MathContext
        for (EquivalenceProbe.PField sel : s.modeSels)
            for (char op : new char[]{'M', 'D', 'A', 'S'})
                for (Src x : s.srcs)
                    for (Src y : s.srcs)
                        if (x != y && ordered(op, x, y))
                            for (IntP p : s.precs)
                                for (Target t : s.decTargets)
                                    if (!emit(sink, new Hyp(new Bin(op, new DLeaf(x), new DLeaf(y), p, false), t, sel, rank[0]++, "mathContext"))) return;
        // divide to a scale
        for (EquivalenceProbe.PField sel : s.modeSels)
            for (Src x : s.srcs)
                for (Src y : s.srcs)
                    if (x != y)
                        for (IntP p : s.params)
                            for (Target t : s.decTargets)
                                if (!emit(sink, new Hyp(new Bin('D', new DLeaf(x), new DLeaf(y), p, true), t, sel, rank[0]++, "divideToScale"))) return;
        // an operation brought to a scale (Money.of after an operation)
        for (EquivalenceProbe.PField sel : s.modeSels)
            for (char op : new char[]{'M', 'D', 'A', 'S'})
                for (Src x : s.srcs)
                    for (Src y : s.srcs)
                        if (x != y && ordered(op, x, y))
                            for (IntP p : s.precs)
                                for (IntP q : s.params)
                                    for (Target t : s.decTargets)
                                        if (!emit(sink, new Hyp(new Rescale(new Bin(op, new DLeaf(x), new DLeaf(y), p, false), q), t, sel, rank[0]++,
                                            "operationThenScale"))) return;
        // percentage
        for (EquivalenceProbe.PField sel : s.modeSels)
            for (Src x : s.srcs)
                for (Src y : s.srcs)
                    if (x != y)
                        for (IntP p : s.precs)
                            for (Target t : s.decTargets)
                                if (!emit(sink, new Hyp(new Pct(new DLeaf(x), new DLeaf(y), p), t, sel, rank[0]++, "percentage"))) return;
        // a value with a sticky digit, rounded
        for (EquivalenceProbe.PField sel : s.modeSels)
            for (Src x : s.srcs) {
                for (IntP p : s.precs) {
                    Ex ex = new Round(new Tail(x), p);
                    for (Target t : withShift(s, s.decTargets, x, p))
                        if (!emit(sink, new Hyp(ex, t, sel, rank[0]++, "roundSticky"))) return;
                }
                for (IntP p : s.params)
                    for (boolean neg : new boolean[]{false, true})
                        for (Target t : s.decTargets)
                            if (!emit(sink, new Hyp(new SetScale(new Tail(x), p, neg), t, sel, rank[0]++, "setScaleSticky"))) return;
            }
        // integer properties of a decimal
        for (Src x : s.srcs)
            for (int w = 0; w < DProp.NAMES.length; w++)
                for (Target t : s.intTargets)
                    if (!emit(sink, new Hyp(new DProp(new DLeaf(x), w), t, null, rank[0]++, "decimalProperty"))) return;
        // dates
        if (s.dateTargets.isEmpty() && s.intTargets.isEmpty()) return;
        for (DSrc d : s.dsrcs)
            for (Target t : s.dateTargets)
                if (!emit(sink, new Hyp(new TLeaf(d), t, null, rank[0]++, "dateConversion"))) return;
        for (DSrc d : s.dsrcs)
            for (int w = 0; w < TProp.NAMES.length; w++)
                for (Target t : s.intTargets)
                    if (!emit(sink, new Hyp(new TProp(new TLeaf(d), w), t, null, rank[0]++, "dateProperty"))) return;
        for (EquivalenceProbe.PField sel : s.unitSels) {
            for (DSrc d : s.dsrcs)
                for (IntP n : s.counts)
                    if (n.f != d.f)
                        for (Target t : s.dateTargets)
                            if (!emit(sink, new Hyp(new Plus(new TLeaf(d), n), t, sel, rank[0]++, "datePlus"))) return;
            for (DSrc d1 : s.dsrcs)
                for (DSrc d2 : s.dsrcs)
                    if (d1.f != d2.f && d1.epoch == d2.epoch)
                        for (Target t : s.intTargets)
                            if (!emit(sink, new Hyp(new Between(new TLeaf(d1), new TLeaf(d2)), t, sel, rank[0]++, "dateBetween"))) return;
        }
        for (DSrc d : s.dsrcs)
            for (IntP n : s.counts)
                if (n.f != d.f)
                    for (int w = 0; w < With.NAMES.length; w++)
                        for (Target t : s.dateTargets)
                            if (!emit(sink, new Hyp(new With(new TLeaf(d), n, w), t, null, rank[0]++, "dateWith"))) return;
        for (IntP n : s.counts)
            for (int[] md : new int[][]{{12, 31}, {1, 1}})
                for (Target t : s.dateTargets)
                    if (!emit(sink, new Hyp(new OfYear(n, md[0], md[1]), t, null, rank[0]++, "dateOf"))) return;
    }

    /** Multiply and add are commutative: the operand declared first is the receiver (one order is enough). */
    static boolean ordered(char op, Src x, Src y) {
        return (op != 'M' && op != 'A') || x.coef.id < y.coef.id;
    }

    /** The decimal targets plus, for a round of x to p digits, each pair target as kept digits and a shift count. */
    static List<Target> withShift(Shape s, List<Target> base, Src x, IntP p) {
        List<Target> out = new ArrayList<>(base);
        for (int i = 0; i < s.pairTargets.size(); i++) {
            EquivalenceProbe.PField[] ce = s.pairTargets.get(i);
            out.add(new PairT(ce[0], ce[1], s.pairScale.get(i), DROPPED, x, p));
        }
        return out;
    }

    private static boolean emit(Predicate<Hyp> sink, Hyp h) {
        char want = h.target.type();
        char have = h.ex.type();
        boolean ok = want == have || (want == 'D' && have == 'I' && h.target instanceof NumT);
        if (!ok) return true;
        return sink.test(h);
    }
}
