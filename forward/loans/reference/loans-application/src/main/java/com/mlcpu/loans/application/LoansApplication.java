package com.mlcpu.loans.application;

import com.mlcpu.loans.api.ScheduleService;
import com.mlcpu.loans.api.migration.LegacyMigration;
import com.mlcpu.loans.api.ports.LoansPorts;
import com.mlcpu.loans.api.servicing.LoanServicing;
import com.mlcpu.loans.api.spi.DueDates;
import com.mlcpu.loans.api.spi.RateFactors;
import com.mlcpu.loans.calendar.RepaymentCalendar;
import com.mlcpu.loans.disclosure.CostOfCredit;
import com.mlcpu.loans.disclosure.ScheduleDocuments;
import com.mlcpu.loans.migration.Migration;
import com.mlcpu.loans.rates.InterestFactors;
import com.mlcpu.loans.schedule.Amortization;
import com.mlcpu.loans.terms.TermsReview;

/** The loans service's entry points (rule A-8). The contexts meet here: the schedule gets the calendar and the rates as SPIs. */
public final class LoansApplication {

    private LoansApplication() {
    }

    /** The quote: schedule, document and disclosure of proposed terms; pure, no ports. */
    public static ScheduleService quotes() {
        return new Quotes(new TermsReview(), new Amortization(dueDates(), rateFactors()), new ScheduleDocuments(), new CostOfCredit());
    }

    /** The servicing use cases over the ERP's ports: book, disburse, bill, collect. */
    public static LoanServicing create(LoansPorts ports) {
        return new Servicing(quotes(), ports);
    }

    /** The legacy loan book's migration: each legacy loan as terms, and its reconciliation with the service's own schedule. */
    public static LegacyMigration migration() {
        return new Migration(quotes());
    }

    /** The repayment calendar (SPI DueDates) the schedule uses. */
    public static DueDates dueDates() {
        return new RepaymentCalendar();
    }

    /** The interest rate factors (SPI RateFactors) the schedule uses. */
    public static RateFactors rateFactors() {
        return new InterestFactors();
    }
}
