package com.mlcpu.loans.sme;

import com.mlcpu.loans.api.Calculation;
import com.mlcpu.loans.api.CurrencyTerms;
import com.mlcpu.loans.api.DisbursementLine;
import com.mlcpu.loans.api.DownPaymentLine;
import com.mlcpu.loans.api.Frequency;
import com.mlcpu.loans.api.InstallmentLine;
import com.mlcpu.loans.api.InterestMethod;
import com.mlcpu.loans.api.MonthDays;
import com.mlcpu.loans.api.Rejected;
import com.mlcpu.loans.api.Schedule;
import com.mlcpu.loans.api.ScheduleLine;
import com.mlcpu.loans.api.ScheduleOutcome;
import com.mlcpu.loans.api.ScheduleRejection;
import com.mlcpu.loans.api.ScheduleRequest;
import com.mlcpu.loans.api.ScheduleService;
import com.mlcpu.loans.api.Scheduled;
import com.mlcpu.loans.api.Totals;
import com.mlcpu.loans.api.YearDays;
import com.mlcpu.loans.api.legacy.LegacyCurrency;
import com.mlcpu.loans.api.legacy.LegacyRequest;
import com.mlcpu.loans.api.legacy.LegacyRunControl;
import com.mlcpu.loans.api.legacy.LegacyScheduleHeader;
import com.mlcpu.loans.api.legacy.LegacyScheduleLine;
import com.mlcpu.loans.api.migration.LegacyLoan;
import com.mlcpu.loans.api.migration.LegacyTerms;
import com.mlcpu.loans.api.migration.LegacyTermsOutcome;
import com.mlcpu.loans.api.migration.Reconciliation;
import com.mlcpu.loans.api.migration.ReconciliationOutcome;
import com.mlcpu.loans.api.migration.Unrepresentable;
import java.math.BigDecimal;
import java.math.RoundingMode;
import java.time.LocalDate;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.Objects;

/**
 * One rule mutant per migration rule (digests/migration.md, MIG): the reference's terms and reconciliation (loans-migration
 * Migration, TermsMapping) with that rule broken. {@link #terms} and {@link #reconcile} with {@code null} are the reference's
 * behaviour, so the mutants differ from it in exactly one place.
 */
final class MigrationMutants {

    enum Mutant {
        MIG_01("the disbursement date is the schedule start"),
        MIG_02("the currency's rounding multiple is taken without its indicator"),
        MIG_03("364 days in a year is read as 365"),
        MIG_04("the down payment percentage is taken without its indicator"),
        MIG_05("the run's precision is ignored (always 12)"),
        MIG_06("the interest method is checked before the frequency"),
        MIG_07("an envelope reject does not match the service's OUT_OF_RANGE"),
        MIG_08("an envelope reject the service schedules is a DIFF, not D-6"),
        MIG_09("a legacy schedule without a disbursement line is compared, not D-3"),
        MIG_10("a loan the service rejects but the legacy scheduled is not reported with the service's rejection"),
        MIG_11("the lines are compared before the header and totals"),
        MIG_12("a difference in the disbursement line's balance is also explained by D-2"),
        MIG_13("a difference reports the last differing field, not the first");

        final String change;

        Mutant(String change) {
            this.change = change;
        }

        String rule() {
            return name().replace('_', '-');
        }
    }

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

    private MigrationMutants() {
    }

    static LegacyTermsOutcome terms(LegacyLoan loan, Mutant m) {
        LegacyRequest q = loan.request();
        LegacyCurrency currency = loan.currency();
        LegacyRunControl control = loan.runControl();
        if (currency == null) return new Unrepresentable(q.loanId(), "RQ-CURRENCY-CODE", q.currencyCode());
        if (m == Mutant.MIG_06 && !METHODS.containsKey(q.interestMethod())) {
            return new Unrepresentable(q.loanId(), "RQ-INTEREST-METHOD", q.interestMethod());
        }
        if (!FREQUENCIES.containsKey(q.freqType())) return new Unrepresentable(q.loanId(), "RQ-FREQ-TYPE", q.freqType());
        if (!MONTH_DAYS.containsKey(q.daysInMonth())) return new Unrepresentable(q.loanId(), "RQ-DAYS-IN-MONTH", String.valueOf(q.daysInMonth()));
        if (!YEAR_DAYS.containsKey(q.daysInYear())) return new Unrepresentable(q.loanId(), "RQ-DAYS-IN-YEAR", String.valueOf(q.daysInYear()));
        if (!METHODS.containsKey(q.interestMethod())) return new Unrepresentable(q.loanId(), "RQ-INTEREST-METHOD", q.interestMethod());
        if (!MODES.containsKey(control.roundingMode())) return new Unrepresentable(q.loanId(), "RC-ROUNDING-MODE", control.roundingMode());
        YearDays yearDays = m == Mutant.MIG_03 && q.daysInYear() == 364 ? YearDays.DAYS_365 : YEAR_DAYS.get(q.daysInYear());
        return new LegacyTerms(new ScheduleRequest(
                q.loanId(),
                q.schedStartDate(),
                new CurrencyTerms(currency.code(), currency.decimalPlaces(),
                        currency.multiplesInd() || m == Mutant.MIG_02 ? Integer.valueOf(currency.multiples()) : null),
                q.disbAmount(),
                m == Mutant.MIG_01 ? q.schedStartDate() : q.disbDate(),
                q.numRepayments(),
                q.repayEvery(),
                FREQUENCIES.get(q.freqType()),
                q.annualRate(),
                q.downpayPctInd() || m == Mutant.MIG_04 ? q.downpayPct() : null,
                q.downpayEnabled(),
                MONTH_DAYS.get(q.daysInMonth()),
                yearDays,
                q.fixedLengthInd() ? Integer.valueOf(q.fixedLength()) : null,
                METHODS.get(q.interestMethod()),
                q.fullTermTranche(),
                new Calculation(m == Mutant.MIG_05 ? 12 : control.precision(), MODES.get(control.roundingMode()))));
    }

    static Reconciliation reconcile(LegacyLoan loan, ScheduleService quotes, Mutant m) {
        String id = loan.request().loanId();
        String code = loan.reject() == null ? null : loan.reject().code();
        LegacyTermsOutcome t = terms(loan, m);
        if (t instanceof Unrepresentable u) {
            return new Reconciliation(id, ReconciliationOutcome.EXPLAINED, "D-5", u.field(), null, null, code, null);
        }
        ScheduleOutcome o = quotes.schedule(((LegacyTerms) t).terms());
        ScheduleRejection rejection = o instanceof Rejected r ? r.reason() : null;
        if (code != null) return legacyReject(id, code, rejection, m);
        if (m != Mutant.MIG_09 && loan.lines().stream().noneMatch(l -> "D".equals(l.recType()))) {
            return rejection == ScheduleRejection.DISBURSEMENT_OUTSIDE_TERM
                    ? new Reconciliation(id, ReconciliationOutcome.EXPLAINED, "D-3", "outcome", null, null, null, rejection)
                    : diff(id, "outcome", "scheduled without a disbursement line", rejection == null ? "scheduled" : rejection.name(), rejection);
        }
        if (rejection != null) return diff(id, "outcome", "scheduled", rejection.name(), m == Mutant.MIG_10 ? null : rejection);
        List<String[]> differences = compare(loan.header(), loan.lines(), ((Scheduled) o).schedule(), m);
        if (differences.isEmpty()) return new Reconciliation(id, ReconciliationOutcome.MATCH, null, null, null, null, null, null);
        String[] first = m == Mutant.MIG_13 ? differences.get(differences.size() - 1) : differences.get(0);
        if (differences.stream().allMatch(d -> runningBalance(d[0], loan.lines(), m)) && !continuous(loan.lines(), m)) {
            return new Reconciliation(id, ReconciliationOutcome.EXPLAINED, "D-2", first[0], null, null, null, null);
        }
        return diff(id, first[0], first[1], first[2], null);
    }

    private static Reconciliation legacyReject(String id, String code, ScheduleRejection rejection, Mutant m) {
        if (rejection != null && (code.equals(rejection.legacyCode())
                || m != Mutant.MIG_07 && code.startsWith("RJENV") && rejection == ScheduleRejection.OUT_OF_RANGE)) {
            return new Reconciliation(id, ReconciliationOutcome.LEGACY_REJECT, null, null, null, null, code, rejection);
        }
        if (m != Mutant.MIG_08 && rejection == null && code.startsWith("RJENV")) {
            return new Reconciliation(id, ReconciliationOutcome.EXPLAINED, "D-6", "outcome", null, null, code, null);
        }
        return new Reconciliation(id, ReconciliationOutcome.DIFF, null, "outcome", code, rejection == null ? "scheduled" : rejection.name(), code,
                rejection);
    }

    private static Reconciliation diff(String id, String field, String legacy, String service, ScheduleRejection rejection) {
        return new Reconciliation(id, ReconciliationOutcome.DIFF, null, field, legacy, service, null, rejection);
    }

    private static List<String[]> compare(LegacyScheduleHeader h, List<LegacyScheduleLine> lines, Schedule s, Mutant m) {
        List<String[]> head = new ArrayList<>();
        same(head, "currencyCode", h.currencyCode(), s.currencyCode());
        same(head, "decimalPlaces", String.valueOf(h.decimalPlaces()), String.valueOf(s.decimalPlaces()));
        same(head, "termDays", String.valueOf(h.termDays()), String.valueOf(s.termDays()));
        Totals t = s.totals();
        amount(head, "totals.disbursed", h.totDisbursed(), t.disbursed());
        amount(head, "totals.principal", h.totPrincipal(), t.principal());
        amount(head, "totals.interest", h.totInterest(), t.interest());
        amount(head, "totals.fee", h.totFee(), t.fee());
        amount(head, "totals.penalty", h.totPenalty(), t.penalty());
        amount(head, "totals.repayment", h.totRepayment(), t.repayment());
        amount(head, "totals.outstanding", h.totOutstanding(), t.outstanding());
        List<String[]> body = new ArrayList<>();
        if (lines.size() != s.lines().size()) {
            body.add(new String[] {"lines", String.valueOf(lines.size()), String.valueOf(s.lines().size())});
        } else {
            for (int i = 0; i < lines.size(); i++) {
                LegacyScheduleLine l = lines.get(i);
                ScheduleLine x = s.lines().get(i);
                String at = "line " + (i + 1) + " ";
                String kind = x instanceof DisbursementLine ? "D" : x instanceof DownPaymentLine ? "P" : "R";
                if (!kind.equals(l.recType())) {
                    body.add(new String[] {at + "kind", l.recType(), kind});
                    continue;
                }
                if (x instanceof DisbursementLine dl) {
                    date(body, at + "from", l.fromDate(), dl.from());
                    date(body, at + "due", l.dueDate(), dl.due());
                    amount(body, at + "amount", l.principal(), dl.amount());
                    amount(body, at + "balanceAfter", l.outstBalance(), dl.balanceAfter());
                } else if (x instanceof DownPaymentLine p) {
                    same(body, at + "number", String.valueOf(l.periodNumber()), String.valueOf(p.number()));
                    date(body, at + "from", l.fromDate(), p.from());
                    date(body, at + "due", l.dueDate(), p.due());
                    amount(body, at + "principal", l.principal(), p.principal());
                    amount(body, at + "totalDue", l.totalDue(), p.totalDue());
                    amount(body, at + "balanceAfter", l.outstBalance(), p.balanceAfter());
                    amount(body, at + "remainingDue", l.totOutstanding(), p.remainingDue());
                } else {
                    InstallmentLine r = (InstallmentLine) x;
                    same(body, at + "number", String.valueOf(l.periodNumber()), String.valueOf(r.number()));
                    date(body, at + "from", l.fromDate(), r.from());
                    date(body, at + "due", l.dueDate(), r.due());
                    amount(body, at + "principal", l.principal(), r.principal());
                    amount(body, at + "interest", l.interest(), r.interest());
                    amount(body, at + "fee", l.fee(), r.fee());
                    amount(body, at + "penalty", l.penalty(), r.penalty());
                    amount(body, at + "totalDue", l.totalDue(), r.totalDue());
                    amount(body, at + "balanceAfter", l.outstBalance(), r.balanceAfter());
                    amount(body, at + "remainingDue", l.totOutstanding(), r.remainingDue());
                }
            }
        }
        List<String[]> d = new ArrayList<>();
        if (m == Mutant.MIG_11) {
            d.addAll(body);
            d.addAll(head);
        } else {
            d.addAll(head);
            d.addAll(body);
        }
        return d;
    }

    private static boolean runningBalance(String field, List<LegacyScheduleLine> lines, Mutant m) {
        String[] parts = field.split(" ");
        if (parts.length != 3 || !"line".equals(parts[0]) || !"balanceAfter".equals(parts[2])) return false;
        return m == Mutant.MIG_12 || !"D".equals(lines.get(Integer.parseInt(parts[1]) - 1).recType());
    }

    private static boolean continuous(List<LegacyScheduleLine> lines, Mutant m) {
        for (int i = 1; i < lines.size(); i++) {
            LegacyScheduleLine l = lines.get(i);
            if (!"D".equals(l.recType()) && lines.get(i - 1).outstBalance().subtract(l.principal()).compareTo(l.outstBalance()) != 0) return false;
        }
        return true;
    }

    private static void same(List<String[]> d, String field, String legacy, String service) {
        if (!Objects.equals(legacy, service)) d.add(new String[] {field, legacy, service});
    }

    private static void date(List<String[]> d, String field, LocalDate legacy, LocalDate service) {
        same(d, field, String.valueOf(legacy), String.valueOf(service));
    }

    private static void amount(List<String[]> d, String field, BigDecimal legacy, BigDecimal service) {
        if (legacy.compareTo(service) != 0) d.add(new String[] {field, legacy.toPlainString(), service.toPlainString()});
    }
}
