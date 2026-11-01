package com.acme;

import java.math.BigDecimal;
import java.math.RoundingMode;
import java.time.LocalDate;
import java.util.Map;
import java.util.Optional;

/** A loan: the original the fixture's modern Loan is compared with. */
public final class Loan implements Comparable<Loan> {
    private static final int MAX_TERM = 600;
    private final BigDecimal principal;
    private final Map<LocalDate, BigDecimal> disbursementsByDueDate;
    private int termInMonths;

    public Loan(BigDecimal principal, Map<LocalDate, BigDecimal> disbursementsByDueDate, int termInMonths) {
        if (termInMonths > MAX_TERM) {
            throw new IllegalArgumentException("term too long: " + termInMonths);
        }
        this.principal = principal;
        this.disbursementsByDueDate = disbursementsByDueDate;
        this.termInMonths = termInMonths;
    }

    public BigDecimal principal() {
        return principal;
    }

    public BigDecimal totalDisbursed() {
        return disbursementsByDueDate.values().stream().reduce(BigDecimal.ZERO, BigDecimal::add);
    }

    public Optional<LocalDate> firstDisbursementDueDate() {
        return disbursementsByDueDate.keySet().stream().min(LocalDate::compareTo);
    }

    public BigDecimal interest(BigDecimal annualRate) {
        BigDecimal monthlyRate = annualRate.divide(BigDecimal.valueOf(12), 10, RoundingMode.HALF_EVEN);
        return principal.multiply(monthlyRate);
    }

    public BigDecimal interest(BigDecimal annualRate, int months) {
        return interest(annualRate).multiply(BigDecimal.valueOf(months));
    }

    public void neverCalled() {
    }

    @Override
    public int compareTo(Loan o) {
        return principal.compareTo(o.principal);
    }

    public static final class Builder {
        private BigDecimal principal;

        public Builder principal(BigDecimal principal) {
            this.principal = principal;
            return this;
        }

        public Loan build() {
            return new Loan(principal, Map.of(), 12);
        }
    }
}
