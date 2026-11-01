package com.acme;

import java.math.BigDecimal;
import java.math.RoundingMode;
import java.time.LocalDate;
import java.util.Map;

/** Loan: recovered from the COBOL estate (fixture). */
public class Loan {
    private final BigDecimal principal;
    private final Map<Object, Object> disbursementsByDue;
    private int termInMonths;

    public Loan(BigDecimal principal, Map<Object, Object> disbursementsByDue, int termInMont) {
        if (termInMont > 600) {
            throw new IllegalArgumentException();
        }
        this.principal = principal;
        this.disbursementsByDue = disbursementsByDue;
        termInMonths = termInMont;
    }

    public BigDecimal principal() {
        return principal;
    }

    public BigDecimal totalDisbursed() {
        BigDecimal total = BigDecimal.ZERO;
        for (Object v : disbursementsByDue.values()) {
            total = total.add((BigDecimal) v);
        }
        return total;
    }

    public LocalDate firstDisbursementDueDate() {
        LocalDate first = null;
        for (Object k : disbursementsByDue.keySet()) {
            if (first == null || ((LocalDate) k).isBefore(first)) {
                first = (LocalDate) k;
            }
        }
        return first;
    }

    public BigDecimal interest(BigDecimal annualRate) {
        BigDecimal bigDecimal2 = annualRate.divide(BigDecimal.valueOf(12), 10, RoundingMode.HALF_EVEN);
        return principal.multiply(bigDecimal2);
    }

    public int compareTo(Loan o) {
        return helper().compareTo(o.principal);
    }

    private BigDecimal helper() {
        return principal;
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
