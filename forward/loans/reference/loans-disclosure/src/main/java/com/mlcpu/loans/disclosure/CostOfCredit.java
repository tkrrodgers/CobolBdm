package com.mlcpu.loans.disclosure;

import com.mlcpu.loans.api.DisbursementLine;
import com.mlcpu.loans.api.Disclosure;
import com.mlcpu.loans.api.DownPaymentLine;
import com.mlcpu.loans.api.InstallmentLine;
import com.mlcpu.loans.api.Schedule;
import com.mlcpu.loans.api.ScheduleLine;
import java.math.BigDecimal;
import java.math.MathContext;
import java.time.LocalDate;
import java.util.ArrayList;
import java.util.List;

/**
 * The cost of credit of a schedule (DISC-01 to DISC-09): amount financed, total of payments, finance charge and the annual
 * percentage rate by the actuarial method of 12 CFR 1026 Appendix J, as a percentage with two decimals.
 */
public final class CostOfCredit {

    private static final MathContext MC = UnitPeriod.MC;
    private static final BigDecimal HUNDRED = BigDecimal.valueOf(100);
    private static final BigDecimal HALF_CENT = new BigDecimal("0.005");

    public Disclosure of(Schedule s) {
        LocalDate advance = null;
        BigDecimal principal = BigDecimal.ZERO.setScale(s.decimalPlaces());
        BigDecimal down = principal;
        BigDecimal total = principal;
        List<LocalDate> dates = new ArrayList<>();
        List<BigDecimal> payments = new ArrayList<>();
        for (ScheduleLine l : s.lines()) {
            if (l instanceof DisbursementLine d) {
                advance = d.due();
                principal = principal.add(d.amount());
            } else if (l instanceof DownPaymentLine p) {
                down = down.add(p.principal());
            }
        }
        for (ScheduleLine l : s.lines()) {
            if (l instanceof InstallmentLine i) {
                total = total.add(i.totalDue());
                // the payments: installments due after the advance (one due on or before it carries nothing)
                if (i.due().isAfter(advance)) {
                    dates.add(i.due());
                    payments.add(i.totalDue());
                }
            }
        }
        BigDecimal financed = principal.subtract(down);
        BigDecimal charge = total.subtract(financed);
        BigDecimal apr = charge.signum() <= 0 ? BigDecimal.ZERO.setScale(2) : apr(advance, dates, payments, financed);
        return new Disclosure(financed, charge, total, apr);
    }

    /** DISC-05: the two-decimal percentage r with PV(r - 0.005) >= amount financed > PV(r + 0.005). */
    static BigDecimal apr(LocalDate advance, List<LocalDate> dates, List<BigDecimal> payments, BigDecimal financed) {
        UnitPeriod unit = UnitPeriod.of(advance, dates);
        List<UnitPeriod.Count> counts = new ArrayList<>(dates.size());
        for (LocalDate d : dates) {
            counts.add(unit.count(advance, d));
        }
        BigDecimal perYear = unit.perYear();
        // the smallest c (cents) with PV((c + 0.5) / 100 percent) < amount financed
        long lo = -1;
        long hi = 1;
        while (pv(boundary(hi), perYear, counts, payments).compareTo(financed) >= 0) {
            lo = hi;
            hi *= 2;
        }
        while (hi - lo > 1) {
            long mid = lo + (hi - lo) / 2;
            if (pv(boundary(mid), perYear, counts, payments).compareTo(financed) < 0) {
                hi = mid;
            } else {
                lo = mid;
            }
        }
        return BigDecimal.valueOf(hi, 2);
    }

    /** The percentage half a cent above c cents. */
    private static BigDecimal boundary(long cents) {
        return BigDecimal.valueOf(cents, 2).add(HALF_CENT);
    }

    /** The present value of the payments at an annual percentage rate (DISC-04: the general equation of Appendix J). */
    static BigDecimal pv(BigDecimal aprPercent, BigDecimal perYear, List<UnitPeriod.Count> counts, List<BigDecimal> payments) {
        BigDecimal i = aprPercent.divide(HUNDRED, MC).divide(perYear, MC);
        BigDecimal growth = BigDecimal.ONE.add(i, MC);
        BigDecimal sum = BigDecimal.ZERO;
        for (int k = 0; k < counts.size(); k++) {
            UnitPeriod.Count c = counts.get(k);
            BigDecimal denominator = BigDecimal.ONE.add(c.f().multiply(i, MC), MC).multiply(growth.pow(c.t(), MC), MC);
            sum = sum.add(payments.get(k).divide(denominator, MC), MC);
        }
        return sum;
    }
}
