package com.mlcpu.loans.calendar;

import com.mlcpu.loans.api.Frequency;
import com.mlcpu.loans.api.ScheduleRequest;
import com.mlcpu.loans.api.spi.DueDates;
import com.mlcpu.loans.api.spi.Period;
import java.time.LocalDate;
import java.time.YearMonth;
import java.util.ArrayList;
import java.util.List;

/**
 * The repayment calendar (rules CAL). The first period starts on the schedule's start; each due date is {@code every} units of
 * the frequency after the previous one. Monthly due dates keep the disbursement's day of the month when it is after the 28th
 * (as far as the month allows). A fixed length sets the last due date: the start plus that many units.
 */
public final class RepaymentCalendar implements DueDates {

    @Override
    public List<Period> periods(ScheduleRequest request) {
        List<Period> periods = new ArrayList<>(request.installments());
        LocalDate last = request.scheduleStart();
        for (int i = 1; i <= request.installments(); i++) {
            LocalDate next = plus(last, request.frequency(), request.every());
            int seedDay = request.disbursementDate().getDayOfMonth();
            if (request.frequency() == Frequency.MONTHS && seedDay > 28 && next.getDayOfMonth() >= 28) {
                next = next.withDayOfMonth(Math.min(YearMonth.from(next).lengthOfMonth(), seedDay));
            }
            if (i == request.installments() && request.fixedLength() != null) {
                LocalDate end = plus(request.scheduleStart(), request.frequency(), request.fixedLength());
                if (!end.isEqual(next)) {
                    next = end;
                }
            }
            periods.add(new Period(last, next));
            last = next;
        }
        return List.copyOf(periods);
    }

    private static LocalDate plus(LocalDate date, Frequency frequency, int units) {
        return switch (frequency) {
            case DAYS -> date.plusDays(units);
            case WEEKS -> date.plusWeeks(units);
            case MONTHS -> date.plusMonths(units);
            case YEARS -> date.plusYears(units);
        };
    }
}
