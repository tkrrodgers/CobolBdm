using System.Text;
using MlCpu.Cobol.Analysis;

namespace MlCpu.Modernize;

/// <summary>
/// A subprogram whose CALL interface is a known JDK contract: its LINKAGE record's layout and the function codes its
/// condition names declare. The roles name the record's fields by position in the layout, not by their names.
/// </summary>
public sealed class KernelContract
{
    public required string Kind { get; init; }
    public required string ClassName { get; init; }
    public required DataItem Parms { get; init; }
    public required Dictionary<string, DataItem> Roles { get; init; }
    /// <summary>A decimal library's reached operation codes and their operations (DecimalKernels).</summary>
    public Dictionary<string, string> Operations { get; init; } = [];
    /// <summary>A decimal library's codes no caller reaches (the frontier).</summary>
    public List<string> Unreached { get; init; } = [];
    /// <summary>A decimal library's capacity and return codes, from its declarations (DecimalKernels).</summary>
    public Dictionary<string, long> Limits { get; init; } = [];
}

/// <summary>
/// Kernels the JDK replaces (P1): a date kernel over day numbers (java.time) and a decimal rounding kernel over unscaled
/// integers (BigDecimal, RoundingMode). A subprogram is one when its only LINKAGE record has the contract's layout and
/// function codes and it calls nothing. Its class becomes the JDK's (typed methods, and the CALL interface at the
/// record boundary); the lifted kernel stays as the test reference, and a generated differential gate runs both on the
/// same cases and fails the build on any difference, or when a mutant (a plausible wrong substitution) goes undetected.
/// The return codes are kept at the boundary: a range guard throws DateTimeException (RC 20), an unknown code is RC 12.
/// </summary>
static class Kernels
{
    static readonly string[] DateFunctions = ["DN", "YM", "PL", "BT", "LM", "LY", "YR", "DM", "WD", "YE"];
    static readonly string[] DateUnits = ["D", "W", "M", "Y"];
    static readonly string[] RoundFunctions = ["S", "D", "P"];
    static readonly string[] RoundModes = ["U", "D", "C", "F", "H", "N", "E"];

    static HashSet<string> Codes88(DataItem it) =>
        it.Children.Where(c => c.Level == 88).SelectMany(c => c.Values.Select(v => v.Low.Text)).ToHashSet(StringComparer.Ordinal);

    static bool Text(DataItem it, int len) => it.Category != Category.Numeric && it.Length == len;

    static bool Bin(DataItem it, int digits) =>
        it is { Category: Category.Numeric, Usage: Usage.Binary or Usage.Comp5, Scale: 0, Signed: true } && it.Digits == digits;

    public static KernelContract? Recognize(AppProgram ap)
    {
        if (!ap.IsSubprogram || ap.LinkageOrder.Count != 1) return null;
        if (ap.Classic.SelectMany(p => Model.Deep(p.Statements)).Any(s => s.Verb == "CALL")) return null;
        var rec = ap.LinkageOrder[0];
        // a REDEFINES alternative is another view of the same bytes (DT-YYYYMMDD-R: its year, month and day), no field
        static bool Redefining(DataItem d) { for (var x = d; x is not null; x = x.Parent) if (x.RedefinesItem is not null) return true; return false; }
        var f = AppModel.Leaves(rec).Where(l => !l.IsFiller && !Redefining(l)).ToList();
        if (f.Count == 8 && Text(f[0], 2) && Codes88(f[0]).SetEquals(DateFunctions) && Text(f[1], 1) && Codes88(f[1]).SetEquals(DateUnits)
            && Bin(f[2], 9) && Bin(f[3], 9) && Bin(f[4], 9) && Bin(f[5], 9)
            && f[6] is { Category: Category.Numeric, Usage: Usage.Display, Digits: 8, Scale: 0, Signed: false } && Bin(f[7], 4))
            return new KernelContract
            {
                Kind = "dates", ClassName = "DateKernel", Parms = rec,
                Roles = new() { ["function"] = f[0], ["unit"] = f[1], ["day"] = f[2], ["day2"] = f[3], ["n"] = f[4], ["result"] = f[5], ["date"] = f[6], ["rc"] = f[7] },
            };
        if (f.Count == 7 && f[0] is { Category: Category.Numeric, Usage: Usage.Comp3, Digits: 31, Scale: 0, Signed: true }
            && Text(f[1], 1) && Codes88(f[1]).SetEquals(RoundFunctions) && Bin(f[2], 4) && Text(f[3], 1) && Codes88(f[3]).SetEquals(RoundModes)
            && Text(f[4], 1) && Codes88(f[4]).IsSubsetOf(["Y"]) && Bin(f[5], 4) && Bin(f[6], 4))
            return new KernelContract
            {
                Kind = "rounding", ClassName = "RoundingKernel", Parms = rec,
                Roles = new() { ["value"] = f[0], ["function"] = f[1], ["parm"] = f[2], ["mode"] = f[3], ["sticky"] = f[4], ["rc"] = f[5], ["shift"] = f[6] },
            };
        return null;
    }

    public static string ReferenceName(AppProgram ap) => Naming.Pascal(ap.P.Id) + "Reference";

    /// <summary>The record class's view of the contract's fields: accessors, and the code enum constants.</summary>
    sealed class View(AppModel a, AppProgram ap)
    {
        public readonly RecordClass Rc = ap.Records[ap.Kernel!.Parms];
        readonly KernelContract k = ap.Kernel!;
        public string Record => Rc.Name;
        public string RecordImport => a.RecordPackage + "." + Rc.Name;
        public string Acc(string role) => Rc.Accessors[k.Roles[role].Name];
        public string? Enum(string role) => Rc.Codes.TryGetValue(k.Roles[role].Name, out var c) ? c.Enum : null;

        /// <summary>The enum constant of a code (CodeSets names it by the copybook's meaning, else by the code), null when the enum lacks it.</summary>
        public string? Const(string role, string code)
        {
            if (!Rc.Codes.TryGetValue(k.Roles[role].Name, out var c) || !c.Codes.Contains(code)) return null;
            bool meant = c.Codes.All(x => c.Names.ContainsKey(x)) && c.Names.Values.Distinct().Count() == c.Names.Count;
            return meant ? c.Names[code] : code;
        }
    }

    static string Doc(AppProgram ap) => ap.Description.Replace("*/", "* /");

    /// <summary>The JDK class of a recognized kernel; null when the record's code enums cannot name every code (the lifted class stays).</summary>
    public static string? JavaText(AppModel a, AppProgram ap)
    {
        if (ap.Kernel!.Kind == DecimalKernels.Kind) return DecimalKernels.JavaText(a, ap);
        var v = new View(a, ap);
        return ap.Kernel!.Kind switch
        {
            "dates" => DatesText(a, ap, v),
            "rounding" => RoundingText(a, ap, v),
            _ => null,
        };
    }

    static string? DatesText(AppModel a, AppProgram ap, View v)
    {
        if (v.Enum("function") is null || v.Enum("unit") is null || DateFunctions.Any(c => v.Const("function", c) is null) || DateUnits.Any(c => v.Const("unit", c) is null)) return null;
        string F(string c) => v.Const("function", c)!;
        string U(string c) => v.Const("unit", c)!;
        string fn = v.Acc("function"), unit = v.Acc("unit"), day = v.Acc("day"), day2 = v.Acc("day2"), n = v.Acc("n"), result = v.Acc("result"), date = v.Acc("date"), rc = v.Acc("rc");
        var parms = ap.Kernel!.Parms.Name;
        return $$"""
package {{a.AppPackage}};

import java.time.DateTimeException;
import java.time.LocalDate;
import java.time.temporal.ChronoUnit;
import java.util.OptionalInt;
import {{v.RecordImport}};

/**
 * Dates as day numbers: java.time over the day numbers COBOL's FUNCTION INTEGER-OF-DATE counts (1601-01-01 is day 1,
 * 9999-12-31 is day 3,067,671). It replaces the lifted kernel {{ap.P.Id}}; the differential gate
 * {{a.AppPackage}}.reference.DateKernelGate runs both on the same records and fails the build on any difference.
 * <p>Legacy: {{ap.P.Id}} (CALL '{{ap.P.Id}}' USING {{parms}}): {{Doc(ap)}}
 * <p>Generated by MlCpu.Modernize: do not edit, regenerate.
 */
public final class DateKernel {
    /** The day before day 1. */
    private static final LocalDate DAY_ZERO = LocalDate.of(1600, 12, 31);
    /** The last day number (9999-12-31). */
    public static final int LAST_DAY = 3_067_671;
    /** The CALL interface's return code for a function or a unit it does not know. */
    public static final int BAD_REQUEST = 12;
    /** The CALL interface's return code for a day, a date or a result outside the range. */
    public static final int OUT_OF_RANGE = 20;
    /** The largest result the interface holds (PIC S9(9)). */
    private static final long MAX_RESULT = 999_999_999L;

    /** The date of a day number; DateTimeException outside 1601-01-01..9999-12-31. */
    public static LocalDate date(int day) {
        if (day < 1 || day > LAST_DAY) {
            throw new DateTimeException("day number " + day + " is outside 1601-01-01..9999-12-31");
        }
        return DAY_ZERO.plusDays(day);
    }

    /** The day number of a date; DateTimeException outside 1601-01-01..9999-12-31. */
    public static int dayNumber(LocalDate date) {
        if (date.getYear() < 1601 || date.getYear() > 9999) {
            throw new DateTimeException(date + " is outside 1601-01-01..9999-12-31");
        }
        return (int) ChronoUnit.DAYS.between(DAY_ZERO, date);
    }

    /** The day number of a yyyyMMdd number; empty when it is no date of the years 1601..9999. */
    public static OptionalInt dayNumber(long yyyymmdd) {
        int year = (int) (yyyymmdd / 10_000);
        int month = (int) (yyyymmdd / 100 % 100);
        int dayOfMonth = (int) (yyyymmdd % 100);
        if (year < 1601 || year > 9999 || month < 1 || month > 12 || dayOfMonth < 1 || dayOfMonth > LocalDate.of(year, month, 1).lengthOfMonth()) {
            return OptionalInt.empty();
        }
        return OptionalInt.of(dayNumber(LocalDate.of(year, month, dayOfMonth)));
    }

    /**
     * A day number plus an amount of units: days and weeks count days; months and years keep the day of month, clamped
     * to the target month (LocalDate.plusMonths). DateTimeException when the result is outside the range.
     */
    public static int plus(int day, long amount, ChronoUnit unit) {
        long result = switch (unit) {
            case DAYS -> day + amount;
            case WEEKS -> day + amount * 7;
            case MONTHS -> plusMonths(day, amount);
            case YEARS -> plusMonths(day, amount * 12);
            default -> throw new IllegalArgumentException("unit " + unit);
        };
        if (result < 1 || result > LAST_DAY) {
            throw new DateTimeException("day number " + result + " is outside 1601-01-01..9999-12-31");
        }
        return (int) result;
    }

    /** A day number plus months (LocalDate.plusMonths); DateTimeException for a target year outside 1601..9999. */
    public static int plusMonths(int day, long months) {
        LocalDate from = date(day);
        long targetMonth = from.getYear() * 12L + from.getMonthValue() - 1 + months;
        if (targetMonth < 1601 * 12L || targetMonth > 9999 * 12L + 11) {
            throw new DateTimeException(from + " plus " + months + " months is outside 1601..9999");
        }
        return dayNumber(from.plusMonths(months));
    }

    /** The units from one day number to another (ChronoUnit.between: weeks, months and years truncated toward zero). */
    public static long between(ChronoUnit unit, int from, int to) {
        return switch (unit) {
            case DAYS -> (long) to - from;
            case WEEKS -> ((long) to - from) / 7;
            case MONTHS -> ChronoUnit.MONTHS.between(date(from), date(to));
            case YEARS -> ChronoUnit.MONTHS.between(date(from), date(to)) / 12;
            default -> throw new IllegalArgumentException("unit " + unit);
        };
    }

    /** A day number with another day of month; empty when the month has no such day. */
    public static OptionalInt withDayOfMonth(int day, int dayOfMonth) {
        LocalDate date = date(day);
        if (dayOfMonth < 1 || dayOfMonth > date.lengthOfMonth()) {
            return OptionalInt.empty();
        }
        return OptionalInt.of(dayNumber(date.withDayOfMonth(dayOfMonth)));
    }

    /** The day number of 31 December of a year; DateTimeException outside 1601..9999. */
    public static int yearEnd(int year) {
        if (year < 1601 || year > 9999) {
            throw new DateTimeException("year " + year + " is outside 1601..9999");
        }
        return dayNumber(LocalDate.of(year, 12, 31));
    }

    /** The units from one day number to another, as the interface holds them (PIC S9(9)); DateTimeException past it. */
    public static int units(ChronoUnit unit, int from, int to) {
        return result(between(unit, from, to));
    }

    /** The day of month the interface's operand stands for: its last two digits (the legacy kernel's PIC 99). */
    public static int dayOfMonthOperand(int n) {
        return (int) (Math.abs((long) n) % 100);
    }

    /** The return code the CALL interface gives for a failure of the typed methods. */
    public static int returnCode(RuntimeException failure) {
        return failure instanceof DateTimeException ? OUT_OF_RANGE : BAD_REQUEST;
    }

    /**
     * The CALL interface (legacy {{ap.P.Id}} USING {{parms}}): the function code and its operands in the record, the result
     * and the return code back. A date outside the range is RC 20 ({{"OUT_OF_RANGE"}}), an unknown function or unit RC 12; the
     * outputs are defined only for RC 0. A yyyyMMdd that is no date and a day of month the month lacks give day 0, as
     * COBOL's INTEGER-OF-DATE does; the interface's day of month is the operand's last two digits (PIC 99). A caller whose
     * function is known calls the typed methods instead.
     */
    public void call({{v.Record}} parms) {
        parms.{{rc}}(0);
        try {
            switch (parms.{{fn}}Code()) {
                case {{F("DN")}} -> parms.{{result}}(dayNumber(parms.{{date}}()).orElse(0));
                case {{F("YM")}} -> parms.{{date}}(date(parms.{{day}}()));
                case {{F("PL")}} -> parms.{{result}}(plus(parms.{{day}}(), parms.{{n}}(), unit(parms)));
                case {{F("BT")}} -> parms.{{result}}(units(unit(parms), parms.{{day}}(), parms.{{day2}}()));
                case {{F("LM")}} -> parms.{{result}}(date(parms.{{day}}()).lengthOfMonth());
                case {{F("LY")}} -> parms.{{result}}(date(parms.{{day}}()).lengthOfYear());
                case {{F("YR")}} -> parms.{{result}}(date(parms.{{day}}()).getYear());
                case {{F("DM")}} -> parms.{{result}}(date(parms.{{day}}()).getDayOfMonth());
                case {{F("WD")}} -> parms.{{result}}(withDayOfMonth(parms.{{day}}(), dayOfMonthOperand(parms.{{n}}())).orElse(0));
                case {{F("YE")}} -> parms.{{result}}(yearEnd(parms.{{n}}()));
                default -> parms.{{rc}}(BAD_REQUEST);
            }
        } catch (DateTimeException | IllegalArgumentException e) {
            parms.{{rc}}(returnCode(e));
        }
    }

    /** The unit of the interface's unit code (the record boundary); IllegalArgumentException for a code it does not know. */
    public static ChronoUnit unit({{v.Record}} parms) {
        return switch (parms.{{unit}}Code()) {
            case {{U("D")}} -> ChronoUnit.DAYS;
            case {{U("W")}} -> ChronoUnit.WEEKS;
            case {{U("M")}} -> ChronoUnit.MONTHS;
            case {{U("Y")}} -> ChronoUnit.YEARS;
            default -> throw new IllegalArgumentException("unit " + parms.{{unit}}());
        };
    }

    /** A result the interface holds (PIC S9(9)); DateTimeException past it. */
    private static int result(long value) {
        if (Math.abs(value) > MAX_RESULT) {
            throw new DateTimeException("result " + value + " exceeds 9 digits");
        }
        return (int) value;
    }
}

""";
    }

    static string? RoundingText(AppModel a, AppProgram ap, View v)
    {
        if (v.Enum("function") is null || v.Enum("mode") is null || RoundFunctions.Any(c => v.Const("function", c) is null) || RoundModes.Any(c => v.Const("mode", c) is null)) return null;
        string F(string c) => v.Const("function", c)!;
        string M(string c) => v.Const("mode", c)!;
        string value = v.Acc("value"), fn = v.Acc("function"), parm = v.Acc("parm"), mode = v.Acc("mode"), rc = v.Acc("rc"), shift = v.Acc("shift");
        var stickyItem = ap.Kernel!.Roles["sticky"];
        string sticky = v.Acc("sticky");
        // the inexact flag at the boundary: the record's boolean, its enum, or its character
        string inexact = v.Rc.Flags.Contains(stickyItem.Name) ? $"parms.{sticky}()"
            : v.Const("sticky", "Y") is { } sy ? $"parms.{sticky}Code() == {v.Record}.{v.Enum("sticky")}.{sy}" : $"\"Y\".equals(parms.{sticky}())";
        string exact = v.Const("sticky", "N") is { } sn ? $"parms.{sticky}Code({v.Record}.{v.Enum("sticky")}.{sn});" : $"parms.{sticky}(\"N\");";
        var parms = ap.Kernel!.Parms.Name;
        return $$"""
package {{a.AppPackage}};

import java.math.BigDecimal;
import java.math.BigInteger;
import java.math.RoundingMode;
import {{v.RecordImport}};

/**
 * Decimal rounding of unscaled integers with BigDecimal and RoundingMode: a value rounded to a precision
 * (BigDecimal.round(MathContext)) or its low-order digits dropped (BigDecimal.setScale), the true value possibly having
 * more digits than the integer holds (inexact). It replaces the lifted kernel {{ap.P.Id}}; the differential gate
 * {{a.AppPackage}}.reference.RoundingKernelGate runs both on the same records and fails the build on any difference.
 * <p>Legacy: {{ap.P.Id}} (CALL '{{ap.P.Id}}' USING {{parms}}): {{Doc(ap)}}
 * <p>Generated by MlCpu.Modernize: do not edit, regenerate.
 */
public final class RoundingKernel {
    /** The CALL interface's return code for a value the precision cannot hold without the digits past it (inexact). */
    public static final int PRECISION_EXCEEDED = 8;
    /** The CALL interface's return code for a function, a digit count or a mode it does not take. */
    public static final int BAD_REQUEST = 12;
    /** The CALL interface's return code for a result past 31 digits. */
    public static final int TOO_LARGE = 20;
    /** The digits a value holds (PIC S9(31)). */
    private static final int MAX_DIGITS = 31;
    /** The most digits one rounding drops. */
    private static final int MAX_DROP = 30;
    /**
     * The gated domain (the differential gate's contract): a digit count to drop (D) is a PIC S9(4) value, -9999..9999;
     * a precision (S, P) is -9968..9999, so that the digits to drop (at most 31 minus the precision) fit the lifted
     * kernel's PIC S9(4) shift. Below -9968 the lifted kernel's shift overflows: it sets RC 12 and goes on with the
     * shift of its previous call, a result that depends on the call history. No input outside the domain is executed.
     */
    public static final int MIN_PRECISION = -9968;
    /** The largest digit count or precision of the domain (PIC S9(4)). */
    public static final int MAX_COUNT = 9999;
    /** The smallest digit count to drop of the domain (PIC S9(4)). */
    public static final int MIN_DIGITS = -9999;

    /**
     * An input outside the gated domain: the semantic frontier of this substitution. It is no return code (the legacy
     * result there is not a function of the request); the run fails rather than guess.
     */
    public static final class SemanticFrontier extends UnsupportedOperationException {
        private static final long serialVersionUID = 1L;

        SemanticFrontier(String message) {
            super("semantic frontier: " + message);
        }
    }

    /** A value the interface holds (PIC S9(31)), else the frontier. */
    private static void checkValue(BigInteger value) {
        if (digits(value) > MAX_DIGITS) {
            throw new SemanticFrontier("a value of " + digits(value) + " digits is outside the gated domain (PIC S9(31))");
        }
    }

    /** A precision of the domain (S, P), checked before any arithmetic on it, else the frontier. */
    private static void checkPrecision(int precision) {
        if (precision < MIN_PRECISION || precision > MAX_COUNT) {
            throw new SemanticFrontier("precision " + precision + " is outside the gated domain " + MIN_PRECISION + ".." + MAX_COUNT
                + " (the legacy kernel's result there depends on its previous call)");
        }
    }

    /** A digit count to drop of the domain (D), else the frontier. */
    private static void checkDigits(int digits) {
        if (digits < MIN_DIGITS || digits > MAX_COUNT) {
            throw new SemanticFrontier("digit count " + digits + " is outside the gated domain " + MIN_DIGITS + ".." + MAX_COUNT + " (PIC S9(4))");
        }
    }

    /** The digits of an integer's magnitude (zero has one). */
    public static int digits(BigInteger value) {
        return value.abs().toString().length();
    }

    /**
     * An integer with its low-order digits dropped: BigDecimal.setScale(0, mode) of value times ten to the minus digits.
     * Inexact: the true value has non-zero digits past the integer's last (a division's remainder), so it lies beyond
     * the integer, away from zero: a half is above half and nothing is exact.
     */
    public static BigInteger dropDigits(BigInteger value, int digits, RoundingMode mode, boolean inexact) {
        BigDecimal v = inexact
            ? new BigDecimal(value.multiply(BigInteger.TEN).add(value.signum() < 0 ? BigInteger.ONE.negate() : BigInteger.ONE), digits + 1)
            : new BigDecimal(value, digits);
        return v.setScale(0, mode).unscaledValue();
    }

    /** A rounding the interface cannot complete, with its return code (8, 12 or 20). */
    public static final class Failure extends ArithmeticException {
        private static final long serialVersionUID = 1L;
        private final int returnCode;

        Failure(int returnCode, String message) {
            super(message);
            this.returnCode = returnCode;
        }

        /** The CALL interface's return code. */
        public int returnCode() {
            return returnCode;
        }
    }

    /** The kept digits of a value rounded to a precision, and the power of ten they stand for. */
    public record Shortened(BigInteger digits, int shift) {
    }

    /**
     * A value with its low-order digits dropped and put back as zeros (BigDecimal.setScale(-digits, mode) as an integer).
     * Nothing dropped returns the value, or fails (8) when it is inexact; a mode is needed only when the dropped digits are
     * not all zero (else 12, as for more than 30 digits); a result past 31 digits fails (20).
     */
    public static BigInteger roundDecimals(BigInteger value, int digits, RoundingMode mode, boolean inexact) {
        checkValue(value);
        checkDigits(digits);
        if (digits > MAX_DROP) {
            throw new Failure(BAD_REQUEST, "cannot drop " + digits + " digits");
        }
        if (digits <= 0) {
            if (inexact) {
                throw new Failure(PRECISION_EXCEEDED, "an inexact value with no digit to drop");
            }
            return value;
        }
        BigInteger rounded = dropDigits(value, digits, need(mode, value, digits, inexact), inexact).multiply(BigInteger.TEN.pow(digits));
        if (digits(rounded) > MAX_DIGITS) {
            throw new Failure(TOO_LARGE, "the rounded value exceeds 31 digits");
        }
        return rounded;
    }

    /** A value rounded to its first precision digits (BigDecimal.round(new MathContext(precision, mode)) as an integer). */
    public static BigInteger roundSignificant(BigInteger value, int precision, RoundingMode mode, boolean inexact) {
        checkValue(value);
        checkPrecision(precision);
        return roundDecimals(value, digits(value) - precision, mode, inexact);
    }

    /** A value rounded to its first precision digits, as its kept digits and their power of ten (no 31-digit overflow). */
    public static Shortened shorten(BigInteger value, int precision, RoundingMode mode, boolean inexact) {
        checkValue(value);
        checkPrecision(precision);
        int drop = digits(value) - precision;
        if (drop > MAX_DROP) {
            throw new Failure(BAD_REQUEST, "cannot drop " + drop + " digits");
        }
        if (drop <= 0) {
            if (inexact) {
                throw new Failure(PRECISION_EXCEEDED, "an inexact value with no digit to drop");
            }
            return new Shortened(value, 0);
        }
        return new Shortened(dropDigits(value, drop, need(mode, value, drop, inexact), inexact), drop);
    }

    /** The mode, or DOWN when there is none and nothing to round (the dropped digits all zero and the value exact). */
    private static RoundingMode need(RoundingMode mode, BigInteger value, int digits, boolean inexact) {
        if (mode != null) {
            return mode;
        }
        if (inexact || value.mod(BigInteger.TEN.pow(digits)).signum() != 0) {
            throw new Failure(BAD_REQUEST, "no rounding mode");
        }
        return RoundingMode.DOWN;
    }

    /**
     * The CALL interface (legacy {{ap.P.Id}} USING {{parms}}): function S rounds the value to the parameter's significant
     * digits, D drops the parameter's low-order digits, P rounds as S and returns only the kept digits and their power of
     * ten (the shift). RC 8 when nothing is dropped but the value is inexact, RC 12 for a function, digit count or mode it
     * does not take, RC 20 for a result past 31 digits; the outputs are defined only for RC 0. A caller whose function is
     * known calls the typed methods instead. A digit count or precision outside the gated domain throws SemanticFrontier.
     */
    public void call({{v.Record}} parms) {
        parms.{{rc}}(0);
        parms.{{shift}}(0);
        BigInteger value = parms.{{value}}().toBigInteger();
        boolean inexact = inexact(parms);
        RoundingMode mode = mode(parms);
        // outside the gated domain nothing runs (the frontier), checked before the digits to drop are computed
        switch (parms.{{fn}}Code()) {
            case {{F("S")}}, {{F("P")}} -> checkPrecision(parms.{{parm}}());
            case {{F("D")}} -> checkDigits(parms.{{parm}}());
            default -> {
            }
        }
        int drop = parms.{{fn}}Code() == {{v.Record}}.{{v.Enum("function")}}.{{F("D")}} ? parms.{{parm}}() : digits(value) - parms.{{parm}}();
        try {
            switch (parms.{{fn}}Code()) {
                case {{F("S")}} -> parms.{{value}}(new BigDecimal(roundSignificant(value, parms.{{parm}}(), mode, inexact)));
                case {{F("D")}} -> parms.{{value}}(new BigDecimal(roundDecimals(value, parms.{{parm}}(), mode, inexact)));
                case {{F("P")}} -> {
                    Shortened shortened = shorten(value, parms.{{parm}}(), mode, inexact);
                    parms.{{value}}(new BigDecimal(shortened.digits()));
                    parms.{{shift}}(shortened.shift());
                }
                default -> throw new Failure(BAD_REQUEST, "function " + parms.{{fn}}());
            }
            if (drop > 0) {
                {{exact}}
            }
        } catch (Failure e) {
            parms.{{rc}}(e.returnCode());
        }
    }

    /** Whether the interface's value is inexact (the record boundary). */
    public static boolean inexact({{v.Record}} parms) {
        return {{inexact}};
    }

    /** The RoundingMode of the interface's mode code (the record boundary); null for a code it does not know. */
    public static RoundingMode mode({{v.Record}} parms) {
        return switch (parms.{{mode}}Code()) {
            case {{M("U")}} -> RoundingMode.UP;
            case {{M("D")}} -> RoundingMode.DOWN;
            case {{M("C")}} -> RoundingMode.CEILING;
            case {{M("F")}} -> RoundingMode.FLOOR;
            case {{M("H")}} -> RoundingMode.HALF_UP;
            case {{M("N")}} -> RoundingMode.HALF_DOWN;
            case {{M("E")}} -> RoundingMode.HALF_EVEN;
            default -> null;
        };
    }
}

""";
    }

    // ================================================================== the differential gates

    /// <summary>The gate of a kernel: its cases, the comparison with the lifted reference, the mutants that must fail.</summary>
    public static string GateText(AppModel a, AppProgram ap) => ap.Kernel!.Kind switch
    {
        "dates" => DateGate(a, ap),
        DecimalKernels.Kind => DecimalKernels.GateText(a, ap),
        _ => RoundingGate(a, ap),
    };

    static string DateGate(AppModel a, AppProgram ap)
    {
        var v = new View(a, ap);
        string fn = v.Acc("function"), unit = v.Acc("unit"), day = v.Acc("day"), day2 = v.Acc("day2"), n = v.Acc("n"), result = v.Acc("result"), date = v.Acc("date"), rc = v.Acc("rc");
        string fe = $"{v.Record}.{v.Enum("function")}", ue = $"{v.Record}.{v.Enum("unit")}";
        var refName = ReferenceName(ap);
        return $$"""
package {{a.AppPackage}}.reference;

import java.time.LocalDate;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.Random;
import {{a.AppPackage}}.DateKernel;
import {{v.RecordImport}};

/**
 * The differential gate of DateKernel (java.time) against {{refName}}, the lifted COBOL kernel {{ap.P.Id}}. Both run on
 * the same record image; the return code is compared always and the whole record when the lifted kernel returns 0 (its
 * outputs are defined only then, and its callers abend otherwise). The cases: every day number from 0 to one past
 * 9999-12-31 for YM, LM, LY, YR and DM; every date of 1601..9999 and invalid dates for DN; the range ends, month ends and
 * leap days of leap and common centuries with month, day and week offsets for PL and BT; days of month around every
 * month length for WD; years around the range for YE; unknown codes; and a fixed-seed sample (seed 20261101). A mutant,
 * a plausible wrong substitution, must make it fail; one that does not is a gate too weak, and fails the gate.
 * <p>Generated by MlCpu.Modernize: do not edit, regenerate.
 */
public final class DateKernelGate {
    private DateKernelGate() {
    }

    /** A CALL of the kernel interface on a record. */
    interface Kernel {
        void call({{v.Record}} parms);
    }

    /** A case: function and unit codes, the day numbers, the operand and the yyyyMMdd. */
    interface Cases {
        void accept(String function, String unit, int day, int day2, int n, int yyyymmdd);
    }

    /** One comparison of two kernels over the cases. */
    static final class Comparison implements Cases {
        private final Kernel reference;
        private final Kernel candidate;
        private final {{v.Record}} expected = new {{v.Record}}();
        private final {{v.Record}} actual = new {{v.Record}}();
        long cases;
        long differences;
        String first;

        Comparison(Kernel reference, Kernel candidate) {
            this.reference = reference;
            this.candidate = candidate;
        }

        @Override
        public void accept(String function, String unit, int day, int day2, int n, int yyyymmdd) {
            expected.text("");
            expected.{{fn}}(function);
            expected.{{unit}}(unit);
            expected.{{day}}(day);
            expected.{{day2}}(day2);
            expected.{{n}}(n);
            expected.{{date}}(yyyymmdd);
            expected.{{result}}(-1);
            expected.{{rc}}(-1);
            actual.bytes(expected.bytes());
            reference.call(expected);
            candidate.call(actual);
            cases++;
            boolean same = expected.{{rc}}() == actual.{{rc}}() && (expected.{{rc}}() != 0 || Arrays.equals(expected.bytes(), actual.bytes()));
            if (!same) {
                differences++;
                if (first == null) {
                    first = function + " " + unit + " day " + day + " day2 " + day2 + " n " + n + " date " + yyyymmdd + ": rc " + expected.{{rc}}() + "/" + actual.{{rc}}()
                        + " result " + expected.{{result}}() + "/" + actual.{{result}}() + " date " + expected.{{date}}() + "/" + actual.{{date}}();
                }
            }
        }
    }

    /** The day numbers at the edges: the range's ends, every month's first and last day and leap days of the edge years. */
    static List<Integer> edgeDays() {
        List<Integer> days = new ArrayList<>(List.of(-1, 0, 1, 2, DateKernel.LAST_DAY - 1, DateKernel.LAST_DAY, DateKernel.LAST_DAY + 1));
        int[] years = {1601, 1602, 1604, 1700, 1899, 1900, 1999, 2000, 2023, 2024, 2028, 2100, 2399, 2400, 9998, 9999};
        for (int year : years) {
            for (int month = 1; month <= 12; month++) {
                LocalDate first = LocalDate.of(year, month, 1);
                days.add(DateKernel.dayNumber(first));
                days.add(DateKernel.dayNumber(first.withDayOfMonth(first.lengthOfMonth())));
                if (month == 2) {
                    days.add(DateKernel.dayNumber(first.withDayOfMonth(28)));
                }
                if (month == 1 || month == 3) {
                    days.add(DateKernel.dayNumber(first.withDayOfMonth(30)));
                    days.add(DateKernel.dayNumber(first.withDayOfMonth(29)));
                }
            }
        }
        return days;
    }

    /** Every case; with sweep, the whole range of days and dates too. */
    static void cases(boolean sweep, Cases cases) {
        String[] dayFunctions = {"YM", "LM", "LY", "YR", "DM"};
        if (sweep) {
            for (int d = 0; d <= DateKernel.LAST_DAY + 1; d++) {
                for (String f : dayFunctions) {
                    cases.accept(f, " ", d, 0, 0, 0);
                }
            }
            LocalDate date = LocalDate.of(1601, 1, 1);
            for (int d = 1; d <= DateKernel.LAST_DAY; d++, date = date.plusDays(1)) {
                cases.accept("DN", " ", 0, 0, 0, date.getYear() * 10_000 + date.getMonthValue() * 100 + date.getDayOfMonth());
            }
        }
        int[] years = {0, 1, 1600, 1601, 1700, 1800, 1900, 2000, 2023, 2024, 2100, 2400, 9999};
        for (int year : years) {
            for (int month = 0; month <= 13; month++) {
                for (int d = 0; d <= 32; d++) {
                    cases.accept("DN", " ", 0, 0, 0, year * 10_000 + month * 100 + d);
                }
            }
        }
        cases.accept("DN", " ", 0, 0, 0, 99_999_999);
        List<Integer> edges = edgeDays();
        String[] units = {"D", "W", "M", "Y", "X", " "};
        List<Integer> offsets = new ArrayList<>();
        for (int k = -130; k <= 130; k++) {
            offsets.add(k);
        }
        offsets.addAll(List.of(-1201, -1200, 1200, 1201, -12 * 8399, 12 * 8399, -8399, 8399, -3_067_671, 3_067_671, -83_333_334, 83_333_334, -999_999_999, 999_999_999));
        for (int d : edges) {
            for (String u : units) {
                for (int k : offsets) {
                    cases.accept("PL", u, d, 0, k, 0);
                }
            }
        }
        for (int d : edges) {
            for (int d2 : edges) {
                for (String u : units) {
                    cases.accept("BT", u, d, d2, 0, 0);
                }
            }
        }
        for (int d : edges) {
            for (int k = -135; k <= 135; k++) {
                cases.accept("WD", " ", d, 0, k, 0);
            }
            cases.accept("WD", " ", d, 0, 999_999_929, 0);
            cases.accept("WD", " ", d, 0, -999_999_929, 0);
        }
        for (int year = -2; year <= 10_002; year++) {
            cases.accept("YE", " ", 0, 0, year, 0);
        }
        cases.accept("YE", " ", 0, 0, 999_999_999, 0);
        cases.accept("YE", " ", 0, 0, -999_999_999, 0);
        for (String f : new String[] {"XX", "  ", "dn", "D", "PLX"}) {
            for (int d : List.of(0, 1, 100_000, DateKernel.LAST_DAY)) {
                cases.accept(f, "M", d, d, 1, 20_240_229);
            }
        }
        Random random = new Random(20_261_101L);
        String[] functions = {"DN", "YM", "PL", "BT", "LM", "LY", "YR", "DM", "WD", "YE"};
        for (int i = 0; i < 400_000; i++) {
            String f = functions[random.nextInt(functions.length)];
            String u = units[random.nextInt(4)];
            int d = random.nextInt(DateKernel.LAST_DAY + 7) - 3;
            int d2 = random.nextInt(DateKernel.LAST_DAY + 7) - 3;
            int n = switch (random.nextInt(4)) {
                case 0 -> random.nextInt(61) - 30;
                case 1 -> random.nextInt(24_001) - 12_000;
                case 2 -> random.nextInt(20_001) - 10_000;
                default -> random.nextInt(2_000_000_000) - 1_000_000_000 + 1;
            };
            int ymd = random.nextInt(10_000) * 10_000 + random.nextInt(14) * 100 + random.nextInt(33);
            cases.accept(f, u, d, d2, n, ymd);
        }
    }

    /** A mutant: the kernel with one plausible wrong substitution. */
    record Mutant(String name, Kernel kernel) {
    }

    static List<Mutant> mutants(DateKernel kernel) {
        return List.of(
            new Mutant("plusMonths as 30 days a month", p -> {
                if (p.{{fn}}Code() == {{fe}}.{{v.Const("function", "PL")}} && p.{{unit}}Code() == {{ue}}.{{v.Const("unit", "M")}}) {
                    p.{{n}}(p.{{n}}() * 30);
                    p.{{unit}}Code({{ue}}.{{v.Const("unit", "D")}});
                    kernel.call(p);
                    p.{{n}}(p.{{n}}() / 30);
                    p.{{unit}}Code({{ue}}.{{v.Const("unit", "M")}});
                } else {
                    kernel.call(p);
                }
            }),
            new Mutant("months between as days / 30", p -> {
                kernel.call(p);
                if (p.{{fn}}Code() == {{fe}}.{{v.Const("function", "BT")}} && p.{{unit}}Code() == {{ue}}.{{v.Const("unit", "M")}} && p.{{rc}}() == 0) {
                    p.{{result}}((p.{{day2}}() - p.{{day}}()) / 30);
                }
            }),
            new Mutant("day numbers from 1601-01-01 as day 0", p -> {
                kernel.call(p);
                if (p.{{fn}}Code() == {{fe}}.{{v.Const("function", "DN")}} && p.{{rc}}() == 0 && p.{{result}}() > 0) {
                    p.{{result}}(p.{{result}}() - 1);
                }
            }),
            new Mutant("day of month not clamped to the month", p -> {
                kernel.call(p);
                if (p.{{fn}}Code() == {{fe}}.{{v.Const("function", "PL")}} && p.{{unit}}Code() == {{ue}}.{{v.Const("unit", "M")}} && p.{{rc}}() == 0) {
                    int from = DateKernel.date(p.{{day}}()).getDayOfMonth();
                    LocalDate to = DateKernel.date(p.{{result}}());
                    if (to.getDayOfMonth() < from) {
                        p.{{result}}(p.{{result}}() + from - to.getDayOfMonth());
                    }
                }
            }));
    }

    /** Runs the gate; true when the kernels agree on every case and every mutant is detected. */
    public static boolean run() {
        DateKernel kernel = new DateKernel();
        {{refName}} reference = new {{refName}}();
        Comparison gate = new Comparison(reference::call, kernel::call);
        cases(true, gate);
        System.out.println("DateKernel gate: " + gate.cases + " cases, " + gate.differences + " differences" + (gate.first == null ? "" : "; first: " + gate.first));
        boolean ok = gate.differences == 0;
        for (Mutant m : mutants(kernel)) {
            Comparison c = new Comparison(new {{refName}}()::call, m.kernel());
            cases(false, c);
            System.out.println("  mutant \"" + m.name() + "\": " + c.cases + " cases, " + c.differences + " differences" + (c.differences > 0 ? " (detected)" : " (NOT DETECTED)"));
            ok &= c.differences > 0;
        }
        return ok;
    }
}

""";
    }

    static string RoundingGate(AppModel a, AppProgram ap)
    {
        var v = new View(a, ap);
        string value = v.Acc("value"), fn = v.Acc("function"), parm = v.Acc("parm"), mode = v.Acc("mode"), sticky = v.Acc("sticky"), rc = v.Acc("rc"), shift = v.Acc("shift");
        string fe = $"{v.Record}.{v.Enum("function")}", me = $"{v.Record}.{v.Enum("mode")}";
        var refName = ReferenceName(ap);
        return $$"""
package {{a.AppPackage}}.reference;

import java.math.BigDecimal;
import java.math.BigInteger;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.Random;
import {{a.AppPackage}}.RoundingKernel;
import {{v.RecordImport}};

/**
 * The differential gate of RoundingKernel (BigDecimal, RoundingMode) against {{refName}}, the lifted COBOL kernel
 * {{ap.P.Id}}. Both run on the same record image; the return code is compared always and the whole record when the lifted
 * kernel returns 0 (its outputs are defined only then, and its callers abend otherwise). The cases: zero, the powers of
 * ten and their neighbours, the halves (5, 15, 25 times a power of ten) and their neighbours, and the 31-digit extremes,
 * both signs, for every function (S, D, P and unknown), digit count -2..33 and -9968, 9999, mode (the seven and an
 * unknown) and inexact flag; and a fixed-seed sample (seed 20261101). The digit counts between the sampled ones are not
 * enumerated: the gate is equivalence on these cases. Precisions below -9968 and counts past PIC S9(4) are outside the
 * gated domain: there the lifted kernel's result depends on its previous call, and RoundingKernel throws its
 * SemanticFrontier (checked by frontier(): the typed methods and the CALL interface, at the boundary and at extreme
 * ints, and the lifted kernel's first-call and call-history results recorded). A mutant must make the gate fail.
 * <p>Generated by MlCpu.Modernize: do not edit, regenerate.
 */
public final class RoundingKernelGate {
    private RoundingKernelGate() {
    }

    /** A CALL of the kernel interface on a record. */
    interface Kernel {
        void call({{v.Record}} parms);
    }

    /** A case: the value, function, digit count, mode and inexact flag. */
    interface Cases {
        void accept(BigInteger value, String function, int parm, String mode, String sticky);
    }

    /** One comparison of two kernels over the cases. */
    static final class Comparison implements Cases {
        private final Kernel reference;
        private final Kernel candidate;
        private final {{v.Record}} expected = new {{v.Record}}();
        private final {{v.Record}} actual = new {{v.Record}}();
        long cases;
        long differences;
        String first;

        Comparison(Kernel reference, Kernel candidate) {
            this.reference = reference;
            this.candidate = candidate;
        }

        @Override
        public void accept(BigInteger value, String function, int parm, String mode, String sticky) {
            expected.text("");
            expected.{{value}}(new BigDecimal(value));
            expected.{{fn}}(function);
            expected.{{parm}}(parm);
            expected.{{mode}}(mode);
            expected.{{sticky}}(sticky);
            expected.{{rc}}(-1);
            expected.{{shift}}(-1);
            actual.bytes(expected.bytes());
            reference.call(expected);
            candidate.call(actual);
            cases++;
            boolean same = expected.{{rc}}() == actual.{{rc}}() && (expected.{{rc}}() != 0 || Arrays.equals(expected.bytes(), actual.bytes()));
            if (!same) {
                differences++;
                if (first == null) {
                    first = value + " " + function + " " + parm + " " + mode + " " + sticky + ": rc " + expected.{{rc}}() + "/" + actual.{{rc}}()
                        + " value " + expected.{{value}}() + "/" + actual.{{value}}() + " shift " + expected.{{shift}}() + "/" + actual.{{shift}}();
                }
            }
        }
    }

    /** The edge values: zero, powers of ten, halves and their neighbours, the 31-digit extremes, both signs. */
    static List<BigInteger> edgeValues() {
        List<BigInteger> values = new ArrayList<>();
        BigInteger limit = BigInteger.TEN.pow(31);
        values.add(BigInteger.ZERO);
        for (int k = 0; k <= 30; k++) {
            BigInteger p = BigInteger.TEN.pow(k);
            for (BigInteger base : List.of(p, p.multiply(BigInteger.valueOf(5)), p.multiply(BigInteger.valueOf(15)), p.multiply(BigInteger.valueOf(25)))) {
                for (int delta = -1; delta <= 1; delta++) {
                    BigInteger x = base.add(BigInteger.valueOf(delta));
                    if (x.signum() > 0 && x.compareTo(limit) < 0) {
                        values.add(x);
                        values.add(x.negate());
                    }
                }
            }
        }
        values.add(limit.subtract(BigInteger.ONE));
        values.add(limit.subtract(BigInteger.ONE).negate());
        return values;
    }

    static void cases(Cases cases) {
        String[] functions = {"S", "D", "P", "X"};
        String[] modes = {"U", "D", "C", "F", "H", "N", "E", "Z"};
        String[] flags = {"Y", "N"};
        List<Integer> parms = new ArrayList<>();
        for (int p = -2; p <= 33; p++) {
            parms.add(p);
        }
        parms.addAll(List.of(-9968, 9999));
        for (BigInteger value : edgeValues()) {
            for (String f : functions) {
                for (int p : parms) {
                    for (String m : modes) {
                        for (String s : flags) {
                            cases.accept(value, f, p, m, s);
                        }
                    }
                }
            }
        }
        Random random = new Random(20_261_101L);
        for (int i = 0; i < 300_000; i++) {
            int digits = 1 + random.nextInt(31);
            BigInteger value = new BigInteger(digits * 4, random).mod(BigInteger.TEN.pow(digits));
            if (random.nextBoolean()) {
                value = value.negate();
            }
            cases.accept(value, functions[random.nextInt(3)], random.nextInt(34) - 1, modes[random.nextInt(7)], flags[random.nextInt(2)]);
        }
    }

    /** A mutant: the kernel with one plausible wrong substitution. */
    record Mutant(String name, Kernel kernel) {
    }

    static List<Mutant> mutants(RoundingKernel kernel) {
        return List.of(
            new Mutant("HALF_EVEN as HALF_UP", p -> {
                boolean even = p.{{mode}}Code() == {{me}}.{{v.Const("mode", "E")}};
                if (even) {
                    p.{{mode}}Code({{me}}.{{v.Const("mode", "H")}});
                }
                kernel.call(p);
                if (even) {
                    p.{{mode}}Code({{me}}.{{v.Const("mode", "E")}});
                }
            }),
            new Mutant("the inexact flag ignored", p -> {
                String flag = p.{{sticky}}Text();
                p.{{sticky}}("N");
                kernel.call(p);
                if (p.{{rc}}() != 0) {
                    p.{{sticky}}(flag);
                }
            }),
            new Mutant("P without its shift", p -> {
                kernel.call(p);
                if (p.{{fn}}Code() == {{fe}}.{{v.Const("function", "P")}} && p.{{rc}}() == 0) {
                    p.{{shift}}(0);
                }
            }));
    }

    /** Whether the action throws RoundingKernel's frontier. */
    static boolean frontier(Runnable action) {
        try {
            action.run();
            return false;
        } catch (RoundingKernel.SemanticFrontier e) {
            return true;
        } catch (RuntimeException e) {
            return false;
        }
    }

    /** A record of the interface. */
    static {{v.Record}} request(BigInteger value, String function, int parm, String mode, String sticky) {
        {{v.Record}} p = new {{v.Record}}();
        p.text("");
        p.{{value}}(new BigDecimal(value));
        p.{{fn}}(function);
        p.{{parm}}(parm);
        p.{{mode}}(mode);
        p.{{sticky}}(sticky);
        return p;
    }

    /**
     * The frontier: -9968 is in the domain (no frontier, the reference agrees), -9969 and the ints past the domain throw
     * the frontier from the typed methods and the CALL interface (S and P; D past PIC S9(4)); the lifted kernel's
     * results there are recorded (a first call with the inexact flag, and the same request after another call).
     */
    static boolean frontier() {
        boolean ok = true;
        BigInteger v = BigInteger.valueOf(12_345);
        RoundingKernel kernel = new RoundingKernel();
        for (String f : List.of("S", "P")) {
            for (String s : List.of("Y", "N")) {
                {{v.Record}} expected = request(v, f, -9968, "H", s);
                {{v.Record}} actual = request(v, f, -9968, "H", s);
                new {{refName}}().call(expected);
                ok &= !frontier(() -> kernel.call(actual)) && Arrays.equals(expected.bytes(), actual.bytes());
            }
            ok &= !frontier(() -> RoundingKernel.roundSignificant(v, -9968, java.math.RoundingMode.HALF_UP, true)) && !frontier(() -> RoundingKernel.shorten(v, -9968, java.math.RoundingMode.HALF_UP, false));
            for (int p : new int[] {-9969, -9999, -32768, Integer.MIN_VALUE, Integer.MIN_VALUE + 31, 10_000, Integer.MAX_VALUE}) {
                {{v.Record}} out = request(v, f, (short) p == p ? p : 0, "H", "Y");
                boolean typed = frontier(() -> RoundingKernel.roundSignificant(v, p, java.math.RoundingMode.HALF_UP, true)) && frontier(() -> RoundingKernel.shorten(v, p, java.math.RoundingMode.HALF_UP, true));
                boolean boundary = (short) p != p || frontier(() -> kernel.call(out));
                if (!typed || !boundary) {
                    System.out.println("  frontier NOT thrown: " + f + " precision " + p);
                }
                ok &= typed && boundary;
            }
        }
        for (int p : new int[] {-10_000, 10_000, Integer.MIN_VALUE, Integer.MAX_VALUE}) {
            {{v.Record}} out = request(v, "D", (short) p == p ? p : 0, "H", "N");
            ok &= frontier(() -> RoundingKernel.roundDecimals(v, p, java.math.RoundingMode.HALF_UP, false)) && ((short) p != p || frontier(() -> kernel.call(out)));
        }
        ok &= frontier(() -> RoundingKernel.roundDecimals(BigInteger.TEN.pow(31), 1, java.math.RoundingMode.HALF_UP, false));
        // the lifted kernel outside the domain (31 digits, precision -9969: its shift of 10000 overflows PIC S9(4)): a
        // first call with the inexact flag, and the same request after a call that leaves a shift of 29
        BigInteger big = BigInteger.TEN.pow(31).subtract(BigInteger.ONE);
        {{v.Record}} first = request(big, "S", -9969, "H", "Y");
        new {{refName}}().call(first);
        {{refName}} used = new {{refName}}();
        used.call(request(big, "S", 2, "H", "N"));
        {{v.Record}} after = request(big, "S", -9969, "H", "Y");
        used.call(after);
        ok &= frontier(() -> kernel.call(request(big, "S", -9969, "H", "Y")));
        System.out.println("RoundingKernel frontier: precision -9968 in the domain, -9969 and the ints past it throw SemanticFrontier: " + (ok ? "yes" : "NO")
            + "; the lifted kernel at 31 digits, precision -9969, inexact: first call rc " + first.{{rc}}() + " value " + first.{{value}}()
            + ", after a call rc " + after.{{rc}}() + " value " + after.{{value}}() + " (the replaced JDK code: rc 12, 10000 digits to drop)"
            + (first.{{rc}}() != after.{{rc}}() || first.{{value}}().compareTo(after.{{value}}()) != 0 ? ": the result depends on the call history" : ""));
        return ok;
    }

    /** Runs the gate; true when the kernels agree on every case, every mutant is detected and the frontier holds. */
    public static boolean run() {
        RoundingKernel kernel = new RoundingKernel();
        boolean frontierOk = frontier();
        Comparison gate = new Comparison(new {{refName}}()::call, kernel::call);
        cases(gate);
        System.out.println("RoundingKernel gate: " + gate.cases + " cases, " + gate.differences + " differences" + (gate.first == null ? "" : "; first: " + gate.first));
        boolean ok = gate.differences == 0 && frontierOk;
        for (Mutant m : mutants(kernel)) {
            Comparison c = new Comparison(new {{refName}}()::call, m.kernel());
            cases(c);
            System.out.println("  mutant \"" + m.name() + "\": " + c.cases + " cases, " + c.differences + " differences" + (c.differences > 0 ? " (detected)" : " (NOT DETECTED)"));
            ok &= c.differences > 0;
        }
        return ok;
    }
}

""";
    }

    /// <summary>The runner of every kernel gate (the build's kernelGates task): exit 1 when one fails.</summary>
    public static string RunnerText(AppModel a, List<string> gates)
    {
        var sb = new StringBuilder();
        sb.Append($"package {a.AppPackage}.reference;\n\n");
        sb.Append("/**\n * The differential gates of the JDK substitutes of the estate's kernels (the build's kernelGates task, part of check):\n");
        sb.Append(" * the build fails when a substitute differs from its lifted kernel on a case or a mutant goes undetected.\n");
        sb.Append(" * <p>Generated by MlCpu.Modernize: do not edit, regenerate.\n */\n");
        sb.Append("public final class KernelGates {\n    private KernelGates() {\n    }\n\n");
        sb.Append("    /** Runs every gate; exits 1 when one fails. */\n");
        sb.Append("    public static void main(String[] args) {\n        boolean ok = true;\n");
        foreach (var g in gates) sb.Append($"        ok &= {g}.run();\n");
        sb.Append("        if (!ok) {\n            System.out.println(\"kernel gates: FAILED\");\n            System.exit(1);\n        }\n");
        sb.Append("        System.out.println(\"kernel gates: passed\");\n    }\n}\n");
        return sb.ToString();
    }
}
