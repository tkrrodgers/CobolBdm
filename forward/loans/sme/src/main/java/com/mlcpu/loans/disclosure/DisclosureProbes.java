package com.mlcpu.loans.disclosure;

import com.mlcpu.loans.api.DisbursementLine;
import com.mlcpu.loans.api.Disclosed;
import com.mlcpu.loans.api.Documented;
import com.mlcpu.loans.api.InstallmentLine;
import com.mlcpu.loans.api.Rejected;
import com.mlcpu.loans.api.Schedule;
import com.mlcpu.loans.api.ScheduleLine;
import com.mlcpu.loans.api.ScheduleOutcome;
import java.math.BigDecimal;
import java.math.RoundingMode;
import java.time.LocalDate;
import java.util.ArrayList;
import java.util.List;

/** What the SME's disclosure pool needs from inside the context: the unit-period of a schedule, the mutants, the closed forms. */
public final class DisclosureProbes {

    private DisclosureProbes() {
    }

    private static LocalDate advance(Schedule s) {
        for (ScheduleLine l : s.lines()) {
            if (l instanceof DisbursementLine d) return d.due();
        }
        throw new IllegalArgumentException("no disbursement line");
    }

    private static List<LocalDate> paymentDates(Schedule s) {
        LocalDate a = advance(s);
        List<LocalDate> dates = new ArrayList<>();
        for (ScheduleLine l : s.lines()) {
            if (l instanceof InstallmentLine i && i.due().isAfter(a)) dates.add(i.due());
        }
        return dates;
    }

    /** The reference's unit-period of a schedule (DISC-08), e.g. MONTHS(1), TERM(3, 92 days). */
    public static String unit(Schedule s) {
        return label(UnitPeriod.of(advance(s), paymentDates(s)));
    }

    static String label(UnitPeriod u) {
        return u.kind + "(" + u.size + (u.kind == UnitPeriod.Kind.TERM ? ", " + u.termDays + " days" : "") + ")";
    }

    /** The unit-period's kind alone (for grouping). */
    public static String unitKind(Schedule s) {
        return UnitPeriod.of(advance(s), paymentDates(s)).kind.name();
    }

    /** The number of payments (installments due after the disbursement). */
    public static int payments(Schedule s) {
        return paymentDates(s).size();
    }

    /** The document under a mutant (RPT-05 and DISC-07: a rejection passed on with a field). */
    public static Object document(ScheduleOutcome o, DisclosureMutant m) {
        if (o instanceof Rejected r) return m == DisclosureMutant.RPT_05 ? new Rejected(r.loanId(), r.reason(), "schedule") : r;
        DisclosureMutant.M.set(m);
        try {
            return new Documented(new ScheduleDocumentsM().of(((com.mlcpu.loans.api.Scheduled) o).schedule()));
        } finally {
            DisclosureMutant.M.remove();
        }
    }

    /** The disclosure under a mutant. */
    public static Object disclosure(ScheduleOutcome o, DisclosureMutant m) {
        if (o instanceof Rejected r) return m == DisclosureMutant.DISC_07 ? new Rejected(r.loanId(), r.reason(), "schedule") : r;
        DisclosureMutant.M.set(m);
        try {
            return new Disclosed(new CostOfCreditM().of(((com.mlcpu.loans.api.Scheduled) o).schedule()));
        } finally {
            DisclosureMutant.M.remove();
        }
    }

    /**
     * FE 7.5's independent closed forms, or null when none applies: a finance charge of 0 gives 0.00; one payment whose term is
     * within a year (one unit-period) gives the simple rate, finance charge / amount financed x unit-periods per year x 100,
     * rounded half up to two decimals.
     */
    public static BigDecimal closedForm(Schedule s, BigDecimal financed, BigDecimal charge) {
        if (charge.signum() == 0) return BigDecimal.ZERO.setScale(2);
        UnitPeriod u = UnitPeriod.of(advance(s), paymentDates(s));
        if (u.kind != UnitPeriod.Kind.TERM) return null;
        return charge.divide(financed, UnitPeriod.MC).multiply(u.perYear(), UnitPeriod.MC).multiply(BigDecimal.valueOf(100), UnitPeriod.MC)
                .setScale(2, RoundingMode.HALF_UP);
    }
}
