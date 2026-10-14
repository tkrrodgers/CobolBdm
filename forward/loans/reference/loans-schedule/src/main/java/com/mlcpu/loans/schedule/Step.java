package com.mlcpu.loans.schedule;

import com.mlcpu.loans.api.ScheduleRejection;

/** A step of the calculation: the plan it leads to, or the rejection that ends it (exactly one of the two is set). */
record Step(Plan plan, ScheduleRejection rejection) {

    static Step to(Plan plan) {
        return new Step(plan, null);
    }

    static Step stop(ScheduleRejection rejection) {
        return new Step(null, rejection);
    }

    boolean stopped() {
        return rejection != null;
    }
}
