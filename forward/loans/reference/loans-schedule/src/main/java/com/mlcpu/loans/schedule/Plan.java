package com.mlcpu.loans.schedule;

import com.mlcpu.loans.api.ScheduleRejection;
import com.mlcpu.loans.api.spi.Factors;
import com.mlcpu.loans.api.spi.Period;
import com.mlcpu.loans.kernel.Money;
import java.time.LocalDate;
import java.time.temporal.ChronoUnit;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.function.BiFunction;

/**
 * The state of the calculation (rule AMZ): the installment periods in order, and the figures that follow from them. A plan
 * never changes; every change gives a new plan with its figures computed afresh, period by period.
 */
final class Plan {

    private final Money zero;
    private final List<Installment> periods;
    private final List<Figures> figures;

    private Plan(Money zero, List<Installment> periods) {
        this(zero, periods, null);
    }

    /**
     * A plan whose figures are computed afresh from its first period that is not the same as the basis plan's: a period's
     * figures depend only on it and the periods before it, so those before stay the basis plan's.
     */
    private Plan(Money zero, List<Installment> periods, Plan basis) {
        this.zero = zero;
        this.periods = List.copyOf(periods);
        int same = 0;
        if (basis != null) {
            int n = Math.min(this.periods.size(), basis.periods.size());
            while (same < n && this.periods.get(same) == basis.periods.get(same)) {
                same++;
            }
        }
        List<Figures> f = new ArrayList<>(this.periods.size());
        Figures previous = null;
        for (int i = 0; i < this.periods.size(); i++) {
            previous = i < same ? basis.figures.get(i) : Figures.of(this.periods.get(i), previous, zero);
            f.add(previous);
        }
        this.figures = List.copyOf(f);
    }

    /** The periods of the calendar, each one stretch with no installment yet. */
    static Plan of(List<Period> calendar, Money zero) {
        List<Installment> periods = new ArrayList<>(calendar.size());
        for (Period p : calendar) {
            periods.add(Installment.of(p.from(), p.due(), zero));
        }
        return new Plan(zero, periods);
    }

    Money zero() {
        return zero;
    }

    int size() {
        return periods.size();
    }

    Installment at(int i) {
        return periods.get(i);
    }

    Figures figures(int i) {
        return figures.get(i);
    }

    /** The plan with each period replaced by the function's result for its index and itself. */
    Plan map(BiFunction<Integer, Installment, Installment> change) {
        List<Installment> next = new ArrayList<>(periods.size());
        for (int i = 0; i < periods.size(); i++) {
            next.add(change.apply(i, periods.get(i)));
        }
        return new Plan(zero, next, this);
    }

    Plan with(int i, Installment period) {
        return map((k, p) -> k == i ? period : p);
    }

    /** A date falls in a period from its start (exclusive; inclusive for the first period) to its due date (inclusive). */
    static boolean covers(LocalDate date, LocalDate from, LocalDate to, boolean first) {
        return first ? !date.isBefore(from) && !date.isAfter(to) : date.isAfter(from) && !date.isAfter(to);
    }

    /** The period a date falls in; -1 when none. */
    int find(LocalDate date) {
        for (int i = 0; i < periods.size(); i++) {
            Installment p = periods.get(i);
            if (covers(date, p.from(), p.due(), i == 0)) {
                return i;
            }
        }
        return -1;
    }

    /** The last stretch of period i a date falls in (the first stretch of the first period includes its start); -1 when none. */
    int findStretch(int i, LocalDate date) {
        List<Stretch> stretches = periods.get(i).stretches();
        int found = -1;
        for (int j = 0; j < stretches.size(); j++) {
            Stretch s = stretches.get(j);
            if (covers(date, s.from(), s.due(), i == 0 && j == 0)) {
                found = j;
            }
        }
        return found;
    }

    /** The periods due on or after the date. */
    List<Integer> dueFrom(LocalDate date) {
        List<Integer> related = new ArrayList<>();
        for (int i = 0; i < periods.size(); i++) {
            if (!periods.get(i).due().isBefore(date)) {
                related.add(i);
            }
        }
        return List.copyOf(related);
    }

    /** Every period. */
    List<Integer> all() {
        List<Integer> every = new ArrayList<>(periods.size());
        for (int i = 0; i < periods.size(); i++) {
            every.add(i);
        }
        return List.copyOf(every);
    }

    /** No installment computed yet. */
    boolean hasNoInstallments() {
        return periods.stream().allMatch(p -> p.installment().isZero());
    }

    /** The principal disbursed in all periods. */
    Money disbursed() {
        Money total = zero;
        for (Installment p : periods) {
            total = total.plus(p.disbursed(zero));
        }
        return Money.positiveOrZero(total);
    }

    /** The balance a period's installment amortizes: what the previous period leaves plus the period's disbursements. */
    Money balanceForInstallment(int i) {
        Money initial = i > 0 ? figures.get(i - 1).balance() : zero;
        return initial.plus(periods.get(i).disbursed(zero));
    }

    int termDays() {
        return periods.isEmpty() ? 0 : Math.toIntExact(ChronoUnit.DAYS.between(periods.get(0).from(), periods.get(periods.size() - 1).due()));
    }

    /**
     * Records the disbursement in period i (AMZ-05; i is the period {@link #find(LocalDate)} gives, so a date on a period's due
     * date falls in that period): on its stretch's due date it is added to that stretch; otherwise the stretch splits at the date,
     * and the part before the split carries it.
     */
    Plan disburse(int i, LocalDate date, Money amount) {
        Installment period = periods.get(i);
        List<Stretch> stretches = new ArrayList<>(period.stretches());
        Stretch b = stretches.get(0);
        if (date.isEqual(b.due())) {
            stretches.set(0, b.withDisbursement(b.disbursement().plus(amount)));
        } else {
            stretches.set(0, b.withDue(date).withDisbursement(b.disbursement().plus(amount)));
            stretches.add(1, Stretch.of(date, b.due(), zero));
        }
        return with(i, period.withStretches(stretches));
    }

    /** The plan with the stretches of the given periods rated: for each period index, one factor pair per stretch in order. */
    Plan withFactors(Map<Integer, List<Factors>> factors) {
        return map((i, p) -> {
            List<Factors> f = factors.get(i);
            if (f == null) {
                return p;
            }
            List<Stretch> stretches = new ArrayList<>(p.stretches().size());
            for (int j = 0; j < p.stretches().size(); j++) {
                stretches.add(p.stretches().get(j).withFactors(f.get(j).installment(), f.get(j).interest()));
            }
            return p.withStretches(stretches);
        });
    }

    /**
     * Rolls the balances forward: the first stretch of a period starts with what the previous period leaves (its last stretch's
     * balance and disbursement less its principal due), a later stretch with what the stretch before it hands on. A later
     * stretch that would start with nothing is not offered (decision D-1: the legacy calculation stopped there).
     */
    Step rollBalances() {
        List<Installment> next = new ArrayList<>(periods.size());
        Figures previous = null;
        for (int i = 0; i < periods.size(); i++) {
            Installment p = periods.get(i);
            List<Stretch> stretches = new ArrayList<>(p.stretches());
            for (int j = 0; j < stretches.size(); j++) {
                Money balance;
                if (j == 0) {
                    if (i == 0) {
                        continue;
                    }
                    balance = Money.positiveOrZero(next.get(i - 1).lastStretch().carried().minus(previous.duePrincipal()));
                } else {
                    balance = stretches.get(j - 1).carried();
                    if (!balance.isPositive()) {
                        return Step.stop(ScheduleRejection.DOWN_PAYMENT_COVERS_PRINCIPAL);
                    }
                }
                stretches.set(j, stretches.get(j).withBalance(balance));
            }
            Installment rolled = p.withStretches(stretches);
            next.add(rolled);
            previous = Figures.of(rolled, previous, zero);
        }
        return Step.to(new Plan(zero, next, this));
    }
}
