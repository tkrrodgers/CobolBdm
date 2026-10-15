package com.mlcpu.loans.migration;

import com.mlcpu.loans.api.Calculation;
import com.mlcpu.loans.api.CurrencyTerms;
import com.mlcpu.loans.api.Frequency;
import com.mlcpu.loans.api.InterestMethod;
import com.mlcpu.loans.api.MonthDays;
import com.mlcpu.loans.api.ScheduleRequest;
import com.mlcpu.loans.api.YearDays;
import com.mlcpu.loans.api.legacy.LegacyCurrency;
import com.mlcpu.loans.api.legacy.LegacyRequest;
import com.mlcpu.loans.api.legacy.LegacyRunControl;
import com.mlcpu.loans.api.migration.LegacyLoan;
import com.mlcpu.loans.api.migration.LegacyTerms;
import com.mlcpu.loans.api.migration.LegacyTermsOutcome;
import com.mlcpu.loans.api.migration.Unrepresentable;
import java.math.RoundingMode;
import java.util.Map;

/**
 * LEG-02: a legacy request as the service's terms. The checks run in the order of the digest; the first value the API cannot
 * represent ends them (D-5). The request's rounding multiples of installments, days-in-year strategy, partial periods and
 * interest recognition are not carried (D-4).
 */
final class TermsMapping {

    private static final Map<String, Frequency> FREQUENCIES = Map.of("DAYS", Frequency.DAYS, "WEEKS", Frequency.WEEKS, "MONTHS",
            Frequency.MONTHS, "YEARS", Frequency.YEARS);
    private static final Map<Integer, MonthDays> MONTH_DAYS = Map.of(1, MonthDays.ACTUAL, 30, MonthDays.THIRTY);
    private static final Map<Integer, YearDays> YEAR_DAYS = Map.of(1, YearDays.ACTUAL, 360, YearDays.DAYS_360, 364, YearDays.DAYS_364,
            365, YearDays.DAYS_365);
    private static final Map<String, InterestMethod> METHODS = Map.of("DECLINING_BALANCE", InterestMethod.DECLINING_BALANCE, "FLAT",
            InterestMethod.FLAT);
    private static final Map<String, RoundingMode> MODES = Map.of("UP", RoundingMode.UP, "DOWN", RoundingMode.DOWN, "CEILING",
            RoundingMode.CEILING, "FLOOR", RoundingMode.FLOOR, "HALF_UP", RoundingMode.HALF_UP, "HALF_DOWN", RoundingMode.HALF_DOWN,
            "HALF_EVEN", RoundingMode.HALF_EVEN);

    LegacyTermsOutcome terms(LegacyLoan loan) {
        LegacyRequest q = loan.request();
        LegacyCurrency currency = loan.currency();
        LegacyRunControl control = loan.runControl();
        if (currency == null) {
            return new Unrepresentable(q.loanId(), "RQ-CURRENCY-CODE", q.currencyCode());
        }
        if (!FREQUENCIES.containsKey(q.freqType())) {
            return new Unrepresentable(q.loanId(), "RQ-FREQ-TYPE", q.freqType());
        }
        if (!MONTH_DAYS.containsKey(q.daysInMonth())) {
            return new Unrepresentable(q.loanId(), "RQ-DAYS-IN-MONTH", String.valueOf(q.daysInMonth()));
        }
        if (!YEAR_DAYS.containsKey(q.daysInYear())) {
            return new Unrepresentable(q.loanId(), "RQ-DAYS-IN-YEAR", String.valueOf(q.daysInYear()));
        }
        if (!METHODS.containsKey(q.interestMethod())) {
            return new Unrepresentable(q.loanId(), "RQ-INTEREST-METHOD", q.interestMethod());
        }
        if (!MODES.containsKey(control.roundingMode())) {
            return new Unrepresentable(q.loanId(), "RC-ROUNDING-MODE", control.roundingMode());
        }
        return new LegacyTerms(new ScheduleRequest(
                q.loanId(),
                q.schedStartDate(),
                new CurrencyTerms(currency.code(), currency.decimalPlaces(), currency.multiplesInd() ? currency.multiples() : null),
                q.disbAmount(),
                q.disbDate(),
                q.numRepayments(),
                q.repayEvery(),
                FREQUENCIES.get(q.freqType()),
                q.annualRate(),
                q.downpayPctInd() ? q.downpayPct() : null,
                q.downpayEnabled(),
                MONTH_DAYS.get(q.daysInMonth()),
                YEAR_DAYS.get(q.daysInYear()),
                q.fixedLengthInd() ? q.fixedLength() : null,
                METHODS.get(q.interestMethod()),
                q.fullTermTranche(),
                new Calculation(control.precision(), MODES.get(control.roundingMode()))));
    }
}
