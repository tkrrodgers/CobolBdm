package com.mlcpu.loans.schedule;

import com.mlcpu.loans.api.ScheduleRequest;
import com.mlcpu.loans.kernel.Money;
import java.math.BigDecimal;
import java.time.LocalDate;
import java.util.ArrayList;
import java.util.List;

/**
 * The walk of the schedule (the debug mode's derivation, FE Appendix E 2026-10-13): the reference's intermediate values in the
 * order the digest's rules compute them, in the digest's words. AmortizationWalk (generated from the reference) calls it; one
 * walk per thread. Only the main calculation is written: the plan cut at the disbursement date is rated quietly.
 */
public final class Walk {

    private static final ThreadLocal<StringBuilder> OUT = new ThreadLocal<>();
    private static final ThreadLocal<Boolean> ON = ThreadLocal.withInitial(() -> true);
    /** Periods shown in full up to this many; beyond, the first 8 and the last 4. */
    private static final int ALL_ROWS = 14;

    private Walk() {
    }

    public static void start() {
        OUT.set(new StringBuilder());
        ON.set(true);
    }

    public static String finish() {
        String s = OUT.get() == null ? "" : OUT.get().toString();
        OUT.remove();
        return s;
    }

    static void on(boolean value) {
        ON.set(value);
    }

    private static StringBuilder out() {
        return OUT.get() == null ? new StringBuilder() : OUT.get();
    }

    static void note(String text) {
        out().append(text).append("\n\n");
    }

    static <T> T rejected(String where, T rejection) {
        note("**Rejected** while " + where + ": " + rejection);
        return rejection;
    }

    private static String m(Money v) {
        return v == null ? "-" : v.amount().toPlainString();
    }

    private static List<Integer> rows(int n) {
        List<Integer> rows = new ArrayList<>();
        for (int i = 0; i < n; i++) {
            if (n <= ALL_ROWS || i < 8 || i >= n - 4) rows.add(i);
            else if (i == 8) rows.add(-1);
        }
        return rows;
    }

    static void periods(ScheduleRequest q, Plan plan) {
        StringBuilder sb = new StringBuilder("## The periods (AMZ-01)\n");
        for (int i : rows(plan.size())) {
            sb.append(i < 0 ? "...\n" : (i + 1) + ": " + plan.at(i).from() + " to " + plan.at(i).due() + "\n");
        }
        note(sb.toString().trim());
    }

    static void disbursement(LocalDate date, int period, Money amount, Money balanceBefore) {
        note("## The disbursement (AMZ-02, AMZ-03)\n" + m(amount) + " on " + date + ", in period " + (period + 1)
                + "; the balance before it (the plan cut at that date, rated and settled): " + m(balanceBefore));
    }

    static void downPayment(Money dp) {
        note("## The down payment (PLAN-01)\n" + m(dp));
    }

    static void factors(Step s, List<Integer> related) {
        if (!ON.get() || s.stopped()) return;
        Plan plan = s.plan();
        StringBuilder sb = new StringBuilder("## The related periods' stretches and rate factors (AMZ-05, AMZ-06; the factors from the rates SPI)\n");
        sb.append("| period | stretch | disbursement | installment factor | interest factor | growth |\n|---|---|---|---|---|---|\n");
        for (int r : rows(related.size())) {
            if (r < 0) {
                sb.append("| ... | | | | | |\n");
                continue;
            }
            int i = related.get(r);
            Installment p = plan.at(i);
            boolean first = true;
            for (Stretch st : p.stretches()) {
                sb.append("| ").append(first ? String.valueOf(i + 1) : "").append(" | ").append(st.from()).append(" to ").append(st.due()).append(" | ")
                        .append(m(st.disbursement())).append(" | ").append(st.installmentFactor().toPlainString()).append(" | ")
                        .append(st.interestFactor().toPlainString()).append(" | ").append(first ? plan.figures(i).growth().toPlainString() : "").append(" |\n");
                first = false;
            }
        }
        note(sb.toString().trim());
    }

    static void installment(List<Integer> related, Money balance, BigDecimal product, BigDecimal annuity, Money amount) {
        if (!ON.get()) return;
        note("## The installment (AMZ-11)\nrelated periods " + (related.get(0) + 1) + " to " + (related.get(related.size() - 1) + 1)
                + ": balance " + m(balance) + " x product of their growth " + product.toPlainString() + " / annuity " + annuity.toPlainString()
                + " = " + m(amount) + (amount.isNegative() ? " (negative: not set)" : ""));
    }

    static Step figures(String title, Step s) {
        if (!ON.get()) return s;
        if (s.stopped()) {
            note("## " + title + "\n**Rejected:** " + s.rejection());
            return s;
        }
        Plan plan = s.plan();
        StringBuilder sb = new StringBuilder("## " + title + "\n");
        sb.append("| period | installment | interest earned | interest due | principal due | balance after |\n|---|---|---|---|---|---|\n");
        for (int i : rows(plan.size())) {
            if (i < 0) {
                sb.append("| ... | | | | | |\n");
                continue;
            }
            Figures f = plan.figures(i);
            sb.append("| ").append(i + 1).append(" | ").append(m(plan.at(i).installment())).append(" | ").append(m(f.earnedInterest())).append(" | ")
                    .append(m(f.dueInterest())).append(" | ").append(m(f.duePrincipal())).append(" | ").append(m(f.balance())).append(" |\n");
        }
        note(sb.toString().trim());
        return s;
    }

    static void settlement(int last, Money disbursed, Money interest, Money payable, Money difference) {
        if (!ON.get()) return;
        note("## The settlement of the last installment (AMZ-12)\ndisbursed " + m(disbursed) + " + interest due " + m(interest) + " - payable "
                + m(payable) + " = difference " + m(difference) + ", added to period " + (last + 1) + "'s installment");
    }

    static void spread(int round, InstallmentSpread s) {
        if (!ON.get()) return;
        note("## Evening-out, round " + round + " (AMZ-13)\nbase " + m(s.base()) + ", difference " + m(s.difference()) + " over " + s.periods()
                + " periods: " + (s.isWorthSpreading() ? "worth spreading; the spread installment " + m(s.spread()) : "not worth spreading (stop)"));
    }
}
