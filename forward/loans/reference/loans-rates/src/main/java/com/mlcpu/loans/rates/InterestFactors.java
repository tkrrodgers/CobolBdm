package com.mlcpu.loans.rates;

import com.mlcpu.loans.api.Calculation;
import com.mlcpu.loans.api.Frequency;
import com.mlcpu.loans.api.MonthDays;
import com.mlcpu.loans.api.ScheduleRejection;
import com.mlcpu.loans.api.YearDays;
import com.mlcpu.loans.api.spi.Factors;
import com.mlcpu.loans.api.spi.Period;
import com.mlcpu.loans.api.spi.RateFactors;
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
 * Interest rate factors (rules RATE): the share of the annual rate a stretch of a period earns. With actual days in the month it
 * is the rate times the stretch's days over the days in the year. With 30-day months it is the rate times the installment
 * interval in days (a month counts 30, a week 7) over the days in the year, in proportion to the stretch's share of the period.
 * A stretch that crosses a year end with actual days in the year earns each year's days over that year's length.
 */
public final class InterestFactors implements RateFactors {

    private static final BigDecimal HUNDRED = new BigDecimal("100");
    private static final BigDecimal WEEK_DAYS = BigDecimal.valueOf(7);
    private static final BigDecimal MONTH_DAYS = BigDecimal.valueOf(30);

    @Override
    public RateOutcome factors(RateQuery query, Calculation calculation) {
        MathContext mc = new MathContext(calculation.precision(), calculation.roundingMode());
        if (unsupported(query, query.to()) || unsupported(query, query.period().due())) {
            return new RateRejected(ScheduleRejection.PERIOD_NOT_SUPPORTED_WITH_THIRTY_DAY_MONTHS);
        }
        return new Factors(forInstallment(query, mc), forInterest(query, mc));
    }

    /** 30-day months have no rule for yearly installments, unless the stretch crosses a year end (actual days in the year). */
    private static boolean unsupported(RateQuery q, LocalDate to) {
        return q.monthDays() == MonthDays.THIRTY && q.frequency() == Frequency.YEARS && !acrossYears(q, q.from(), to);
    }

    private static boolean acrossYears(RateQuery q, LocalDate from, LocalDate to) {
        return q.yearDays() == YearDays.ACTUAL && to.getYear() - from.getYear() > 0;
    }

    /** The factor of the stretch itself, for the installment. */
    private static BigDecimal forInstallment(RateQuery q, MathContext mc) {
        BigDecimal every = BigDecimal.valueOf(q.every());
        return factor(q, q.to(), every, mc);
    }

    /** The factor from the stretch's start to the period's due date, for its interest. */
    private static BigDecimal forInterest(RateQuery q, MathContext mc) {
        if (q.monthDays() == MonthDays.ACTUAL || acrossYears(q, q.from(), q.period().due())) {
            return factor(q, q.period().due(), BigDecimal.ONE, mc);
        }
        return factor(q, q.period().due(), periodRatio(q, unit(q.frequency()), mc), mc);
    }

    private static BigDecimal factor(RateQuery q, LocalDate to, BigDecimal intervals, MathContext mc) {
        BigDecimal rate = q.annualRatePercent().divide(HUNDRED, mc);
        LocalDate from = q.from();
        if (acrossYears(q, from, to)) {
            return byYearFractions(rate, yearFractions(from, to, mc), mc);
        }
        BigDecimal daysInYear = BigDecimal.valueOf(daysInYear(q.yearDays(), from));
        BigDecimal actualDays = BigDecimal.valueOf(ChronoUnit.DAYS.between(from, to));
        BigDecimal periodDays = BigDecimal.valueOf(ChronoUnit.DAYS.between(q.period().from(), q.period().due()));
        if (q.monthDays() == MonthDays.ACTUAL) {
            return byDays(rate, actualDays, BigDecimal.ONE, daysInYear, BigDecimal.ONE, BigDecimal.ONE, mc);
        }
        BigDecimal intervalDays = switch (q.frequency()) {
            case DAYS -> BigDecimal.ONE;
            case WEEKS -> WEEK_DAYS;
            case MONTHS -> MONTH_DAYS;
            case YEARS -> throw new IllegalStateException("yearly installments with 30-day months are rejected before");
        };
        return byDays(rate, intervalDays, intervals, daysInYear, actualDays, periodDays, mc);
    }

    /** rate x (interval days x intervals / days in year) x stretch days / period days, at the calculation's precision. */
    private static BigDecimal byDays(BigDecimal rate, BigDecimal intervalDays, BigDecimal intervals, BigDecimal daysInYear,
            BigDecimal stretchDays, BigDecimal periodDays, MathContext mc) {
        if (periodDays.signum() == 0) {
            return BigDecimal.ZERO.setScale(mc.getPrecision());
        }
        BigDecimal fraction = intervalDays.multiply(intervals, mc).divide(daysInYear, mc);
        return rate.multiply(fraction, mc).multiply(stretchDays, mc).divide(periodDays, mc).setScale(mc.getPrecision(),
                mc.getRoundingMode());
    }

    private static BigDecimal byYearFractions(BigDecimal rate, BigDecimal yearFractions, MathContext mc) {
        BigDecimal fraction = BigDecimal.ONE.multiply(yearFractions);
        return rate.multiply(fraction, mc).multiply(BigDecimal.ONE, mc).divide(BigDecimal.ONE, mc).setScale(mc.getPrecision(),
                mc.getRoundingMode());
    }

    /** The years a stretch spans, each year's days over that year's length (a year ends on 31 December). */
    private static BigDecimal yearFractions(LocalDate from, LocalDate to, MathContext mc) {
        BigDecimal sum = BigDecimal.ZERO;
        LocalDate at = from;
        for (int year = from.getYear(); year <= to.getYear(); year++) {
            LocalDate end = year == to.getYear() ? to : LocalDate.of(year, 12, 31);
            BigDecimal days = BigDecimal.valueOf(ChronoUnit.DAYS.between(at, end));
            sum = sum.add(days.divide(BigDecimal.valueOf(Year.of(year).length()), mc), mc);
            at = end;
        }
        return sum;
    }

    private static int daysInYear(YearDays yearDays, LocalDate date) {
        return switch (yearDays) {
            case ACTUAL -> date.lengthOfYear();
            case DAYS_360 -> 360;
            case DAYS_364 -> 364;
            case DAYS_365 -> 365;
        };
    }

    private static ChronoUnit unit(Frequency f) {
        return switch (f) {
            case DAYS -> ChronoUnit.DAYS;
            case WEEKS -> ChronoUnit.WEEKS;
            case MONTHS -> ChronoUnit.MONTHS;
            case YEARS -> ChronoUnit.YEARS;
        };
    }

    /** The period's length in installment intervals, counted from the schedule's start (30-day months). */
    private static BigDecimal periodRatio(RateQuery q, ChronoUnit unit, MathContext mc) {
        Period p = q.period();
        LocalDate seed = seedDate(q, unit);
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
                return BigDecimal.valueOf(days).divide(BigDecimal.valueOf(fullDays), mc).add(BigDecimal.valueOf(multiplier));
            }
        }
        return BigDecimal.valueOf(multiplier - before - 1);
    }

    /** The date intervals are counted from: the schedule's start when the period is a whole interval from it, else the period's start. */
    private static LocalDate seedDate(RateQuery q, ChronoUnit unit) {
        Period p = q.period();
        LocalDate seed = q.scheduleStart();
        // the first whole number of intervals from the start that reaches the due date: the complete intervals before it, and one
        // more when they fall short of it
        long multiplier = Math.max(1, unit.between(seed, p.due()));
        if (seed.plus(multiplier, unit).isBefore(p.due())) {
            multiplier++;
        }
        LocalDate at = seed.plus(multiplier, unit);
        return at.equals(p.due()) && at.minus(q.every(), unit).isEqual(p.from()) ? seed : p.from();
    }
}
