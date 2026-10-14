package com.mlcpu.loans.sme;

import com.mlcpu.loans.api.Rejected;
import com.mlcpu.loans.api.ScheduleRejection;
import com.mlcpu.loans.api.ScheduleRequest;
import java.math.BigDecimal;
import java.math.RoundingMode;
import java.time.LocalDate;

/** One rule mutant per terms rule: the reference's terms review (TermsReview.review) with that rule's limit or check broken. */
final class TermsMutants {

    enum Mutant {
        TRM_01("precision up to 35 is accepted"),
        TRM_02("rounding mode UNNECESSARY is accepted"),
        TRM_03("1,201 installments are accepted"),
        TRM_04("a principal of exactly 10^15 is accepted"),
        TRM_05("5 decimal places are accepted"),
        TRM_06("rates up to 101% are accepted"),
        TRM_07("every up to 10,000 is accepted"),
        TRM_08("dates up to 2200-12-31 are accepted"),
        TRM_09("down payments up to 101% are accepted"),
        TRM_10("a fixed length up to (n + 2) x every is accepted"),
        TRM_11("no rounding-multiple check"),
        TRM_12("no check of a down payment without a percentage"),
        TRM_13("the principal is checked before the installments");

        final String change;

        Mutant(String change) {
            this.change = change;
        }

        String rule() {
            return name().replace('_', '-');
        }
    }

    private TermsMutants() {
    }

    static Rejected review(ScheduleRequest q, Mutant m) {
        if (q.calculation().precision() < 1 || q.calculation().precision() > (m == Mutant.TRM_01 ? 35 : 34)) return out(q, "calculation.precision");
        if (m != Mutant.TRM_02 && q.calculation().roundingMode() == RoundingMode.UNNECESSARY) return rejected(q, ScheduleRejection.ROUNDING_MODE_NOT_OFFERED);
        if (m == Mutant.TRM_13 && badPrincipal(q, m)) return out(q, "principal");
        if (q.installments() < 1 || q.installments() > (m == Mutant.TRM_03 ? 1201 : 1200)) return out(q, "installments");
        if (badPrincipal(q, m)) return out(q, "principal");
        if (q.currency().decimalPlaces() < 0 || q.currency().decimalPlaces() > (m == Mutant.TRM_05 ? 5 : 4)) return out(q, "currency.decimalPlaces");
        if (q.annualRatePercent().signum() < 0 || q.annualRatePercent().compareTo(BigDecimal.valueOf(m == Mutant.TRM_06 ? 101 : 100)) > 0
                || q.annualRatePercent().stripTrailingZeros().scale() > 9) return out(q, "annualRatePercent");
        if (q.every() < 1 || q.every() > (m == Mutant.TRM_07 ? 10000 : 9999)) return out(q, "every");
        LocalDate last = m == Mutant.TRM_08 ? LocalDate.of(2200, 12, 31) : LocalDate.of(2199, 12, 31);
        if (q.scheduleStart().isBefore(LocalDate.of(1900, 1, 1)) || q.scheduleStart().isAfter(last)) return out(q, "scheduleStart");
        if (q.disbursementDate().isBefore(LocalDate.of(1900, 1, 1)) || q.disbursementDate().isAfter(last)) return out(q, "disbursementDate");
        if (q.downPaymentPercent() != null && (q.downPaymentPercent().signum() < 0
                || q.downPaymentPercent().compareTo(BigDecimal.valueOf(m == Mutant.TRM_09 ? 101 : 100)) > 0)) return out(q, "downPaymentPercent");
        if (q.fixedLength() != null) {
            long low = (long) (q.installments() - 1) * q.every();
            long high = (long) (q.installments() + (m == Mutant.TRM_10 ? 2 : 1)) * q.every();
            if (q.fixedLength() <= low || q.fixedLength() > high) return out(q, "fixedLength");
        }
        Integer multiple = q.currency().roundingMultiple();
        if (m != Mutant.TRM_11 && multiple != null && q.currency().decimalPlaces() == 0 && multiple > 0 && q.principal().signum() > 0) {
            return rejected(q, ScheduleRejection.ROUNDING_MULTIPLE_NOT_OFFERED);
        }
        if (m != Mutant.TRM_12 && q.downPaymentEnabled() && q.downPaymentPercent() == null) {
            return rejected(q, ScheduleRejection.DOWN_PAYMENT_PERCENT_MISSING);
        }
        return null;
    }

    private static boolean badPrincipal(ScheduleRequest q, Mutant m) {
        BigDecimal limit = BigDecimal.TEN.pow(15);
        return q.principal().signum() <= 0 || (m == Mutant.TRM_04 ? q.principal().compareTo(limit) > 0 : q.principal().compareTo(limit) >= 0);
    }

    private static Rejected rejected(ScheduleRequest q, ScheduleRejection reason) {
        return new Rejected(q.loanId(), reason, null);
    }

    private static Rejected out(ScheduleRequest q, String field) {
        return new Rejected(q.loanId(), ScheduleRejection.OUT_OF_RANGE, field);
    }
}
