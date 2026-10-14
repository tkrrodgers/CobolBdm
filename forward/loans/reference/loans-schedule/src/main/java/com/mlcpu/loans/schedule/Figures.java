package com.mlcpu.loans.schedule;

import com.mlcpu.loans.kernel.Money;
import java.math.BigDecimal;
import java.math.MathContext;

/**
 * What follows from an installment period and the periods before it (rule AMZ). The interest it earns is its stretches'
 * interest and the interest the previous period earned but could not take. The interest due is that, at most what the installment collects; the principal due is the rest. Computed
 * afresh for every state of the plan, so every figure reflects the current installments (decision D-2: the legacy cache of the
 * balance could go stale).
 *
 * @param growth 1 plus the installment factors of the stretches
 * @param balance the balance after the period; null for a period without stretches
 */
record Figures(BigDecimal growth, Money earnedInterest, Money dueInterest, Money duePrincipal, Money unrecognizedInterest,
        Money balance) {

    static Figures of(Installment p, Figures previous, Money zero) {
        MathContext mc = zero.mathContext();
        BigDecimal growth = BigDecimal.ONE;
        for (Stretch s : p.stretches()) {
            growth = growth.add(s.installmentFactor());
        }
        BigDecimal sum = BigDecimal.ZERO;
        for (Stretch s : p.stretches()) {
            sum = sum.add(s.interest(p.due(), mc));
        }
        Money earned = zero.of(sum);
        if (previous != null) {
            earned = earned.plus(previous.unrecognizedInterest());
        }
        earned = Money.positiveOrZero(earned);
        Money payable = p.payable();
        Money dueInterest = Money.max(Money.min(earned, payable), zero);
        Money duePrincipal = Money.max(Money.positiveOrZero(payable.minus(dueInterest)), zero);
        Money unrecognized = Money.positiveOrZero(earned.minus(dueInterest));
        Money balance = p.stretches().isEmpty() ? null : Money.positiveOrZero(p.lastStretch().carried().minus(duePrincipal));
        return new Figures(growth, earned, dueInterest, duePrincipal, unrecognized, balance);
    }
}
