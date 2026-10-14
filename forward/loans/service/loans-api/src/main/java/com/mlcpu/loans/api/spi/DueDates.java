package com.mlcpu.loans.api.spi;

import com.mlcpu.loans.api.ScheduleRequest;
import java.util.List;

/** The repayment calendar (rules CAL): the periods of a loan, one per installment, in order. */
public interface DueDates {
    List<Period> periods(ScheduleRequest request);
}
