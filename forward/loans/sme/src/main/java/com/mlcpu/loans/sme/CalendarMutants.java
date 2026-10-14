package com.mlcpu.loans.sme;

import com.mlcpu.loans.api.Frequency;
import com.mlcpu.loans.api.ScheduleRequest;
import com.mlcpu.loans.api.spi.Period;
import java.time.LocalDate;
import java.time.YearMonth;
import java.util.ArrayList;
import java.util.List;

/**
 * One rule mutant per calendar rule (ForwardEngineeringFineract.md section 7.2, step 3): the reference calendar with that rule's
 * code path broken. A digest rule is verified when some scenario fails on its mutant and passes on the reference.
 */
final class CalendarMutants {

    enum Mutant {
        CAL_01("the last period is left out"),
        CAL_02("period 1 starts on the disbursement date"),
        CAL_03("a DAYS step adds every + 1 days"),
        CAL_04("a WEEKS step adds every days (not 7 x every)"),
        CAL_05("a MONTHS step adds 30 x every days"),
        CAL_06("a YEARS step adds 365 x every days"),
        CAL_07("no month-end day"),
        CAL_08("each due date steps from the schedule start (k x every units), not from the previous due date"),
        CAL_09("the fixed length is ignored");

        final String change;

        Mutant(String change) {
            this.change = change;
        }

        String rule() {
            return name().replace('_', '-');
        }
    }

    private CalendarMutants() {
    }

    static List<Period> periods(ScheduleRequest q, Mutant m) {
        List<Period> periods = new ArrayList<>(q.installments());
        LocalDate start = q.scheduleStart();
        LocalDate last = m == Mutant.CAL_02 ? q.disbursementDate() : start;
        LocalDate stepFrom = start;
        for (int i = 1; i <= q.installments(); i++) {
            LocalDate next = m == Mutant.CAL_08 ? plus(start, q.frequency(), q.every() * i, m) : plus(stepFrom, q.frequency(), q.every(), m);
            int seedDay = q.disbursementDate().getDayOfMonth();
            if (m != Mutant.CAL_07 && q.frequency() == Frequency.MONTHS && seedDay > 28 && next.getDayOfMonth() >= 28) {
                next = next.withDayOfMonth(Math.min(YearMonth.from(next).lengthOfMonth(), seedDay));
            }
            if (m != Mutant.CAL_09 && i == q.installments() && q.fixedLength() != null) {
                LocalDate end = plus(start, q.frequency(), q.fixedLength(), m);
                if (!end.isEqual(next)) {
                    next = end;
                }
            }
            periods.add(new Period(last, next));
            last = next;
            stepFrom = next;
        }
        if (m == Mutant.CAL_01 && !periods.isEmpty()) {
            periods.remove(periods.size() - 1);
        }
        return periods;
    }

    private static LocalDate plus(LocalDate date, Frequency frequency, int units, Mutant m) {
        return switch (frequency) {
            case DAYS -> date.plusDays(m == Mutant.CAL_03 ? units + 1 : units);
            case WEEKS -> m == Mutant.CAL_04 ? date.plusDays(units) : date.plusWeeks(units);
            case MONTHS -> m == Mutant.CAL_05 ? date.plusDays(30L * units) : date.plusMonths(units);
            case YEARS -> m == Mutant.CAL_06 ? date.plusDays(365L * units) : date.plusYears(units);
        };
    }
}
