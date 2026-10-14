package com.mlcpu.loans.schedule;

import com.mlcpu.loans.kernel.Money;
import java.math.BigDecimal;
import java.math.MathContext;
import java.time.LocalDate;
import java.time.temporal.ChronoUnit;

/**
 * A stretch of a repayment period with one balance (rule AMZ). A period starts as one stretch; a disbursement inside it splits
 * it at the date, and the stretch before the split carries the disbursement, which is owed from the next stretch on. The
 * balance is the one the stretch starts with, as the last roll of the balances left it.
 */
record Stretch(LocalDate from, LocalDate due, BigDecimal installmentFactor, BigDecimal interestFactor, Money disbursement,
        Money balance) {

    static Stretch of(LocalDate from, LocalDate due, Money zero) {
        return new Stretch(from, due, BigDecimal.ZERO, BigDecimal.ZERO, zero, zero);
    }

    Stretch withDue(LocalDate value) {
        return new Stretch(from, value, installmentFactor, interestFactor, disbursement, balance);
    }

    Stretch withFactors(BigDecimal installment, BigDecimal interest) {
        return new Stretch(from, due, installment, interest, disbursement, balance);
    }

    Stretch withDisbursement(Money value) {
        return new Stretch(from, due, installmentFactor, interestFactor, value, balance);
    }

    Stretch withBalance(Money value) {
        return new Stretch(from, due, installmentFactor, interestFactor, disbursement, value);
    }

    long days() {
        return ChronoUnit.DAYS.between(from, due);
    }

    /** The balance and disbursement this stretch hands on to the next. */
    Money carried() {
        return balance.plus(disbursement);
    }

    /** The interest of this stretch: its share, by days, of the interest the balance earns to the period's due date. */
    BigDecimal interest(LocalDate periodDue, MathContext mc) {
        long daysToPeriodDue = ChronoUnit.DAYS.between(from, periodDue);
        BigDecimal interest = BigDecimal.ZERO;
        if (daysToPeriodDue != 0) {
            interest = balance.amount().multiply(interestFactor, mc).divide(BigDecimal.valueOf(daysToPeriodDue), mc)
                    .multiply(BigDecimal.valueOf(days()), mc);
        }
        // the credited interest (none in a quote) is the first term of the sum
        BigDecimal credited = balance.zero().amount();
        return Money.positiveOrZero(credited.add(interest, mc));
    }
}
