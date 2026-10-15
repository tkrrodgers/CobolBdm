package com.mlcpu.loans.sme;

import com.mlcpu.loans.api.Calculation;
import com.mlcpu.loans.api.Frequency;
import com.mlcpu.loans.api.MonthDays;
import com.mlcpu.loans.api.ScheduleRejection;
import com.mlcpu.loans.api.YearDays;
import com.mlcpu.loans.api.spi.Factors;
import com.mlcpu.loans.api.spi.Period;
import com.mlcpu.loans.api.spi.RateOutcome;
import com.mlcpu.loans.api.spi.RateQuery;
import com.mlcpu.loans.api.spi.RateRejected;
import java.math.BigDecimal;
import java.math.MathContext;
import java.time.LocalDate;
import java.time.Year;
import java.time.temporal.ChronoUnit;
import java.time.temporal.TemporalAdjusters;

/**
 * One rule mutant per rate rule (ForwardEngineeringFineract.md section 7.2, step 3): the reference's rate factors
 * (InterestFactors) with that rule's code path broken. A digest rule is verified when some scenario fails on its mutant.
 */
final class RatesMutants {

    enum Mutant {
        RATE_01("the annual rate is divided by 1000"),
        RATE_02("actual days in the year are always 365"),
        RATE_03("the interest factor ends at the stretch's end, not the period's due date"),
        RATE_04("no year crossing: the year fractions are never used"),
        RATE_05("actual days in the month: the period's days in place of the stretch's"),
        RATE_05_B("actual days in the month: the stretch's days exact, not first rounded to the precision"),
        RATE_06("30-day months: a month counts 31 days"),
        RATE_07("30-day months, interest factor: every in place of the period's length in intervals"),
        RATE_08("the period's length in intervals: whole intervals only"),
        RATE_09("factors keep the arithmetic's result, not the precision's decimal places"),
        RATE_10("30-day months with yearly installments: no rejection (a year counts 365 days)"),
        RATE_11("the seed is always the period's start");

        final String change;

        Mutant(String change) {
            this.change = change;
        }

        String rule() {
            return RatesMutants.rule(label());
        }

        /** The mutant's name: its rule, and a letter for a rule's second mutant (RATE-05-B). */
        String label() {
            return name().replace('_', '-');
        }
    }

    private RatesMutants() {
    }

    /** The rule of a mutant's label (RATE-05-B -> RATE-05). */
    static String rule(String label) {
        String[] p = label.split("-");
        return p[0] + "-" + p[1];
    }

    static RateOutcome factors(RateQuery query, Calculation calculation, Mutant m) {
        MathContext mc = new MathContext(calculation.precision(), calculation.roundingMode());
        if (m != Mutant.RATE_10 && (unsupported(query, query.to(), m) || unsupported(query, query.period().due(), m))) {
            return new RateRejected(ScheduleRejection.PERIOD_NOT_SUPPORTED_WITH_THIRTY_DAY_MONTHS);
        }
        LocalDate interestEnd = m == Mutant.RATE_03 ? query.to() : query.period().due();
        return new Factors(factor(query, query.to(), BigDecimal.valueOf(query.every()), mc, m), forInterest(query, interestEnd, mc, m));
    }

    private static boolean unsupported(RateQuery q, LocalDate to, Mutant m) {
        return q.monthDays() == MonthDays.THIRTY && q.frequency() == Frequency.YEARS && !acrossYears(q, q.from(), to, m);
    }

    private static boolean acrossYears(RateQuery q, LocalDate from, LocalDate to, Mutant m) {
        return m != Mutant.RATE_04 && q.yearDays() == YearDays.ACTUAL && to.getYear() - from.getYear() > 0;
    }

    private static BigDecimal forInterest(RateQuery q, LocalDate end, MathContext mc, Mutant m) {
        if (q.monthDays() == MonthDays.ACTUAL || acrossYears(q, q.from(), end, m)) {
            return factor(q, end, BigDecimal.ONE, mc, m);
        }
        BigDecimal intervals = m == Mutant.RATE_07 ? BigDecimal.valueOf(q.every()) : periodRatio(q, unit(q.frequency()), mc, m);
        return factor(q, end, intervals, mc, m);
    }

    private static BigDecimal factor(RateQuery q, LocalDate to, BigDecimal intervals, MathContext mc, Mutant m) {
        BigDecimal rate = q.annualRatePercent().divide(m == Mutant.RATE_01 ? new BigDecimal("1000") : new BigDecimal("100"), mc);
        LocalDate from = q.from();
        if (acrossYears(q, from, to, m)) {
            return scale(rate.multiply(BigDecimal.ONE.multiply(yearFractions(from, to, mc)), mc).multiply(BigDecimal.ONE, mc).divide(BigDecimal.ONE, mc), mc, m);
        }
        BigDecimal daysInYear = BigDecimal.valueOf(q.yearDays() == YearDays.ACTUAL ? (m == Mutant.RATE_02 ? 365 : from.lengthOfYear())
                : switch (q.yearDays()) {
                    case DAYS_360 -> 360;
                    case DAYS_364 -> 364;
                    default -> 365;
                });
        BigDecimal actualDays = BigDecimal.valueOf(ChronoUnit.DAYS.between(from, to));
        BigDecimal periodDays = BigDecimal.valueOf(ChronoUnit.DAYS.between(q.period().from(), q.period().due()));
        if (q.monthDays() == MonthDays.ACTUAL) {
            if (m == Mutant.RATE_05_B) {
                return scale(rate.multiply(actualDays.divide(daysInYear, mc), mc), mc, m);
            }
            return byDays(rate, m == Mutant.RATE_05 ? periodDays : actualDays, BigDecimal.ONE, daysInYear, BigDecimal.ONE, BigDecimal.ONE, mc, m);
        }
        BigDecimal intervalDays = switch (q.frequency()) {
            case DAYS -> BigDecimal.ONE;
            case WEEKS -> BigDecimal.valueOf(7);
            case MONTHS -> BigDecimal.valueOf(m == Mutant.RATE_06 ? 31 : 30);
            case YEARS -> BigDecimal.valueOf(365);
        };
        return byDays(rate, intervalDays, intervals, daysInYear, actualDays, periodDays, mc, m);
    }

    private static BigDecimal byDays(BigDecimal rate, BigDecimal intervalDays, BigDecimal intervals, BigDecimal daysInYear,
            BigDecimal stretchDays, BigDecimal periodDays, MathContext mc, Mutant m) {
        if (periodDays.signum() == 0) {
            return BigDecimal.ZERO.setScale(mc.getPrecision());
        }
        BigDecimal fraction = intervalDays.multiply(intervals, mc).divide(daysInYear, mc);
        return scale(rate.multiply(fraction, mc).multiply(stretchDays, mc).divide(periodDays, mc), mc, m);
    }

    private static BigDecimal scale(BigDecimal v, MathContext mc, Mutant m) {
        return m == Mutant.RATE_09 ? v : v.setScale(mc.getPrecision(), mc.getRoundingMode());
    }

    private static BigDecimal yearFractions(LocalDate from, LocalDate to, MathContext mc) {
        BigDecimal sum = BigDecimal.ZERO;
        LocalDate at = from;
        for (int year = from.getYear(); year <= to.getYear(); year++) {
            LocalDate end = year == to.getYear() ? to : LocalDate.of(year, 12, 31);
            sum = sum.add(BigDecimal.valueOf(ChronoUnit.DAYS.between(at, end)).divide(BigDecimal.valueOf(Year.of(year).length()), mc), mc);
            at = end;
        }
        return sum;
    }

    private static ChronoUnit unit(Frequency f) {
        return switch (f) {
            case DAYS -> ChronoUnit.DAYS;
            case WEEKS -> ChronoUnit.WEEKS;
            case MONTHS -> ChronoUnit.MONTHS;
            case YEARS -> ChronoUnit.YEARS;
        };
    }

    private static BigDecimal periodRatio(RateQuery q, ChronoUnit unit, MathContext mc, Mutant m) {
        Period p = q.period();
        LocalDate seed = m == Mutant.RATE_11 ? p.from() : seedDate(q, unit);
        int before;
        if (unit == ChronoUnit.MONTHS) {
            int seedDay = seed.getDayOfMonth();
            int fromDay = p.from().getDayOfMonth();
            int fromLastDay = p.from().with(TemporalAdjusters.lastDayOfMonth()).getDayOfMonth();
            before = fromLastDay == fromDay && seedDay > fromDay ? Math.toIntExact(unit.between(seed, p.from().plusDays(1)))
                    : Math.toIntExact(unit.between(seed, p.from()));
        } else {
            before = Math.toIntExact(unit.between(seed, p.from()));
        }
        int multiplier = before + 1;
        LocalDate at = p.from();
        while (at.isBefore(p.due())) {
            at = seed.plus(multiplier, unit);
            if (!at.isAfter(p.due())) {
                multiplier++;
            } else {
                LocalDate fullPeriod = at;
                multiplier = multiplier - before - 1;
                at = seed.plus(multiplier, unit);
                long days = ChronoUnit.DAYS.between(at, p.due());
                long fullDays = ChronoUnit.DAYS.between(at, fullPeriod);
                BigDecimal whole = BigDecimal.valueOf(multiplier);
                return m == Mutant.RATE_08 ? whole : BigDecimal.valueOf(days).divide(BigDecimal.valueOf(fullDays), mc).add(whole);
            }
        }
        return BigDecimal.valueOf(multiplier - before - 1);
    }

    private static LocalDate seedDate(RateQuery q, ChronoUnit unit) {
        Period p = q.period();
        LocalDate seed = q.scheduleStart();
        long multiplier = Math.max(1, unit.between(seed, p.due()));
        while (multiplier > 1 && !seed.plus(multiplier - 1, unit).isBefore(p.due())) {
            multiplier--;
        }
        while (seed.plus(multiplier, unit).isBefore(p.due())) {
            multiplier++;
        }
        LocalDate at = seed.plus(multiplier, unit);
        return at.equals(p.due()) && at.minus(q.every(), unit).isEqual(p.from()) ? seed : p.from();
    }
}
