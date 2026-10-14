package com.mlcpu.loans.kernel;

import java.math.BigDecimal;
import java.math.MathContext;

/**
 * An amount of the loan's currency (rules MNY). Every amount is held at the currency's decimal places, rounded by the
 * calculation's rounding mode. Adding and subtracting are exact before that rounding; multiplying and dividing first round to
 * the calculation's precision (significant digits). An operation that cannot change the amount (adding zero, multiplying by
 * one) returns the amount itself.
 */
public final class Money implements Comparable<Money> {

    private final BigDecimal amount;
    private final int decimalPlaces;
    private final MathContext mathContext;

    private Money(BigDecimal amount, int decimalPlaces, MathContext mathContext) {
        this.decimalPlaces = decimalPlaces;
        this.mathContext = mathContext;
        BigDecimal a = amount == null ? BigDecimal.ZERO : amount;
        this.amount = a.stripTrailingZeros().setScale(decimalPlaces, mathContext.getRoundingMode());
    }

    public static Money of(BigDecimal amount, int decimalPlaces, MathContext mathContext) {
        return new Money(amount, decimalPlaces, mathContext);
    }

    public static Money zero(int decimalPlaces, MathContext mathContext) {
        return new Money(BigDecimal.ZERO, decimalPlaces, mathContext);
    }

    /** The amount at this amount's decimal places and calculation. */
    public Money of(BigDecimal value) {
        return new Money(value, decimalPlaces, mathContext);
    }

    public Money zero() {
        return new Money(BigDecimal.ZERO, decimalPlaces, mathContext);
    }

    public BigDecimal amount() {
        return amount;
    }

    public int decimalPlaces() {
        return decimalPlaces;
    }

    public MathContext mathContext() {
        return mathContext;
    }

    public Money plus(Money other) {
        return other == null ? this : plus(other.amount);
    }

    public Money plus(BigDecimal other) {
        if (other == null || other.compareTo(BigDecimal.ZERO) == 0) {
            return this;
        }
        return of(amount.add(other));
    }

    public Money minus(Money other) {
        return other == null ? this : minus(other.amount);
    }

    public Money minus(BigDecimal other) {
        if (other == null || other.compareTo(BigDecimal.ZERO) == 0) {
            return this;
        }
        return of(amount.subtract(other));
    }

    public Money multipliedBy(long factor) {
        if (factor == 1) {
            return this;
        }
        return of(amount.multiply(BigDecimal.valueOf(factor), mathContext));
    }

    public Money dividedBy(long divisor) {
        if (divisor == 1) {
            return this;
        }
        return of(amount.divide(BigDecimal.valueOf(divisor), mathContext));
    }

    public Money negated() {
        if (isZero()) {
            return this;
        }
        return of(amount.negate());
    }

    public Money abs() {
        return isNegative() ? negated() : this;
    }

    public boolean isZero() {
        return amount.compareTo(BigDecimal.ZERO) == 0;
    }

    public boolean isPositive() {
        return amount.compareTo(BigDecimal.ZERO) > 0;
    }

    public boolean isNegative() {
        return amount.compareTo(BigDecimal.ZERO) < 0;
    }

    public boolean exceeds(Money other) {
        return compareTo(other) > 0;
    }

    public boolean isBelow(Money other) {
        return compareTo(other) < 0;
    }

    public boolean sameAs(Money other) {
        return compareTo(other) == 0;
    }

    @Override
    public int compareTo(Money other) {
        return amount.compareTo(other.amount);
    }

    @Override
    public boolean equals(Object o) {
        return o instanceof Money m && m.amount.equals(amount) && m.mathContext.equals(mathContext);
    }

    @Override
    public int hashCode() {
        return amount.hashCode();
    }

    /** The value when it is above zero, else zero. */
    public static Money positiveOrZero(Money value) {
        return value == null || value.isPositive() ? value : value.zero();
    }

    /** The larger of the two; the second when they are equal. */
    public static Money max(Money first, Money second) {
        return first.exceeds(second) ? first : second;
    }

    /** The smaller of the two; the second when they are equal. */
    public static Money min(Money first, Money second) {
        return first.isBelow(second) ? first : second;
    }

    /** The value when it is above zero, else zero. */
    public static BigDecimal positiveOrZero(BigDecimal value) {
        return value != null && value.compareTo(BigDecimal.ZERO) > 0 ? value : BigDecimal.ZERO;
    }

    @Override
    public String toString() {
        return amount.toPlainString();
    }
}
