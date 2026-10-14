package com.mlcpu.loans.schedule;

import com.mlcpu.loans.kernel.Money;
import java.time.LocalDate;
import java.util.List;

/** One installment period of the calculation (rule AMZ): its stretches and its installment. */
record Installment(LocalDate from, LocalDate due, List<Stretch> stretches, Money installment) {

    Installment {
        stretches = List.copyOf(stretches);
    }

    static Installment of(LocalDate from, LocalDate due, Money zero) {
        return new Installment(from, due, List.of(Stretch.of(from, due, zero)), zero);
    }

    // Each change returns the period itself when it changes nothing, so a plan can tell its unchanged periods (Plan's basis).

    Installment withStretches(List<Stretch> value) {
        if (value.equals(stretches)) {
            return this;
        }
        return new Installment(from, due, value, installment);
    }

    Installment withInstallment(Money value) {
        if (value.equals(installment)) {
            return this;
        }
        return new Installment(from, due, stretches, value);
    }

    /** What the installment collects. */
    Money payable() {
        return installment;
    }

    /** Nothing to collect. */
    boolean isSettled() {
        return payable().isZero();
    }

    Stretch lastStretch() {
        return stretches.get(stretches.size() - 1);
    }

    /** The principal disbursed in the period's stretches. */
    Money disbursed(Money zero) {
        Money total = zero;
        for (Stretch s : stretches) {
            total = total.plus(s.disbursement());
        }
        return total;
    }
}
