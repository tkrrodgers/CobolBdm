package com.mlcpu.loans.application;

import com.mlcpu.loans.api.Disclosed;
import com.mlcpu.loans.api.DisclosureOutcome;
import com.mlcpu.loans.api.DocumentOutcome;
import com.mlcpu.loans.api.Documented;
import com.mlcpu.loans.api.Rejected;
import com.mlcpu.loans.api.ScheduleOutcome;
import com.mlcpu.loans.api.ScheduleRequest;
import com.mlcpu.loans.api.ScheduleService;
import com.mlcpu.loans.api.Scheduled;
import com.mlcpu.loans.disclosure.CostOfCredit;
import com.mlcpu.loans.disclosure.ScheduleDocuments;
import com.mlcpu.loans.schedule.Amortization;
import com.mlcpu.loans.terms.TermsReview;

/** The quote: the terms are reviewed first, then scheduled; the document and the disclosure are made from the schedule. */
final class Quotes implements ScheduleService {

    private final TermsReview terms;
    private final Amortization amortization;
    private final ScheduleDocuments documents;
    private final CostOfCredit costOfCredit;

    Quotes(TermsReview terms, Amortization amortization, ScheduleDocuments documents, CostOfCredit costOfCredit) {
        this.terms = terms;
        this.amortization = amortization;
        this.documents = documents;
        this.costOfCredit = costOfCredit;
    }

    @Override
    public ScheduleOutcome schedule(ScheduleRequest request) {
        Rejected rejection = terms.review(request);
        if (rejection != null) {
            return rejection;
        }
        return amortization.schedule(request, terms.downPayment(request));
    }

    @Override
    public DocumentOutcome document(ScheduleRequest request) {
        ScheduleOutcome s = schedule(request);
        return s instanceof Scheduled ok ? new Documented(documents.of(ok.schedule())) : (Rejected) s;
    }

    @Override
    public DisclosureOutcome disclose(ScheduleRequest request) {
        ScheduleOutcome s = schedule(request);
        return s instanceof Scheduled ok ? new Disclosed(costOfCredit.of(ok.schedule())) : (Rejected) s;
    }
}
