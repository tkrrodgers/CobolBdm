package com.mlcpu.loans.schedule;

import com.mlcpu.loans.kernel.Money;
import java.math.BigDecimal;
import java.util.List;

/**
 * How unevenly a run of installments ends (rule AMZ): the difference between the last unsettled installment and the one before
 * it, and the installment it is measured from.
 *
 * @param periods how many periods the run has
 */
record InstallmentSpread(Money base, Money difference, int periods) {

    /** The spread of the given periods of the plan, measured on the last two unsettled in a row. */
    static InstallmentSpread of(Plan plan, List<Integer> run) {
        for (int k = run.size() - 1; k > 0; --k) {
            Installment last = plan.at(run.get(k));
            Installment before = plan.at(run.get(k - 1));
            if (!last.isSettled() && !before.isSettled()) {
                return new InstallmentSpread(before.installment(), last.installment().minus(before.installment()), run.size());
            }
        }
        Money first = plan.at(run.get(0)).installment();
        return new InstallmentSpread(first, first.zero(), run.size());
    }

    /** The difference is worth spreading: 100 times it exceeds half the number of periods (rounded down, at least 1). */
    boolean isWorthSpreading() {
        int half = periods / 2;
        return half > 0 && !difference.isZero() && difference.abs().multipliedBy(100).exceeds(base.of(BigDecimal.valueOf(half)));
    }

    /** The installment with the difference spread over the run's periods. */
    Money spread() {
        return base.plus(difference.dividedBy(Math.max(1, periods)));
    }

    boolean isNarrowerThan(InstallmentSpread other) {
        return difference.abs().isBelow(other.difference.abs());
    }
}
