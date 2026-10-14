package com.mlcpu.loans.terms;

import com.mlcpu.loans.api.Rejected;
import com.mlcpu.loans.api.ScheduleRejection;
import com.mlcpu.loans.api.ScheduleRequest;
import com.mlcpu.loans.kernel.Money;
import java.math.BigDecimal;
import java.math.MathContext;
import java.math.RoundingMode;
import java.time.LocalDate;

/**
 * Loan terms and their admissibility (rules TRM): the terms the service does not offer at all, checked before any calculation,
 * in the order of the rules, and the down payment. Terms that fail only once the calculation reaches them (full-term tranches,
 * flat interest, 30-day months with yearly installments, ...) are the schedule's rejections.
 */
public final class TermsReview {

    private static final BigDecimal HUNDRED = BigDecimal.valueOf(100L);
    private static final BigDecimal PRINCIPAL_LIMIT = BigDecimal.TEN.pow(15);
    private static final LocalDate FIRST_DATE = LocalDate.of(1900, 1, 1);
    private static final LocalDate LAST_DATE = LocalDate.of(2199, 12, 31);

    /** The first reason the terms are not offered, in the order of the rules; null when they are. */
    public Rejected review(ScheduleRequest q) {
        if (q.calculation().precision() < 1 || q.calculation().precision() > 34) {
            return outOfRange(q, "calculation.precision");
        }
        if (q.calculation().roundingMode() == RoundingMode.UNNECESSARY) {
            return rejected(q, ScheduleRejection.ROUNDING_MODE_NOT_OFFERED);
        }
        if (q.installments() < 1 || q.installments() > 1200) {
            return outOfRange(q, "installments");
        }
        if (q.principal().signum() <= 0 || q.principal().compareTo(PRINCIPAL_LIMIT) >= 0) {
            return outOfRange(q, "principal");
        }
        if (q.currency().decimalPlaces() < 0 || q.currency().decimalPlaces() > 4) {
            return outOfRange(q, "currency.decimalPlaces");
        }
        if (q.annualRatePercent().signum() < 0 || q.annualRatePercent().compareTo(HUNDRED) > 0 || q.annualRatePercent().stripTrailingZeros().scale() > 9) {
            return outOfRange(q, "annualRatePercent");
        }
        if (q.every() < 1 || q.every() > 9999) {
            return outOfRange(q, "every");
        }
        if (outside(q.scheduleStart())) {
            return outOfRange(q, "scheduleStart");
        }
        if (outside(q.disbursementDate())) {
            return outOfRange(q, "disbursementDate");
        }
        if (q.downPaymentPercent() != null && (q.downPaymentPercent().signum() < 0 || q.downPaymentPercent().compareTo(HUNDRED) > 0)) {
            return outOfRange(q, "downPaymentPercent");
        }
        if (q.fixedLength() != null) {
            long low = (long) (q.installments() - 1) * q.every();
            long high = (long) (q.installments() + 1) * q.every();
            if (q.fixedLength() <= low || q.fixedLength() > high) {
                return outOfRange(q, "fixedLength");
            }
        }
        Integer multiple = q.currency().roundingMultiple();
        if (multiple != null && q.currency().decimalPlaces() == 0 && multiple > 0 && q.principal().signum() > 0) {
            return rejected(q, ScheduleRejection.ROUNDING_MULTIPLE_NOT_OFFERED);
        }
        if (q.downPaymentEnabled() && q.downPaymentPercent() == null) {
            return rejected(q, ScheduleRejection.DOWN_PAYMENT_PERCENT_MISSING);
        }
        return null;
    }

    private static boolean outside(LocalDate d) {
        return d.isBefore(FIRST_DATE) || d.isAfter(LAST_DATE);
    }

    private static Rejected rejected(ScheduleRequest q, ScheduleRejection reason) {
        return new Rejected(q.loanId(), reason, null);
    }

    private static Rejected outOfRange(ScheduleRequest q, String field) {
        return new Rejected(q.loanId(), ScheduleRejection.OUT_OF_RANGE, field);
    }

    /**
     * The down payment of admissible terms: the percentage of the principal (both at the currency's decimal places), the
     * percentage first divided by 100 at the calculation's precision; zero for a principal of zero or less. Null when the terms
     * have no down payment.
     */
    public Money downPayment(ScheduleRequest request) {
        if (!request.downPaymentEnabled()) {
            return null;
        }
        MathContext mc = new MathContext(request.calculation().precision(), request.calculation().roundingMode());
        Money principal = Money.of(request.principal(), request.currency().decimalPlaces(), mc);
        if (principal.amount().signum() <= 0) {
            return principal.zero();
        }
        BigDecimal share = request.downPaymentPercent().divide(HUNDRED, mc);
        return principal.of(principal.amount().multiply(share, mc));
    }
}
