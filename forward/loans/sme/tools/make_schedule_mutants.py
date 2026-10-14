"""Generates the schedule's rule mutants (sme/src/main/java/com/mlcpu/loans/schedule/AmortizationMutants.java) from the reference's
Amortization.java (ForwardEngineeringFineract.md section 7.2, step 3: one mutant per digest rule). Each mutation replaces one
snippet of the reference, which must occur exactly once, so the mutants follow the reference when it changes: run this again.
The mutants live in the reference's package (the SME project compiles them with it) to reach its package-private plan classes.

Usage: python -I make_schedule_mutants.py <reference Amortization.java> <out AmortizationMutants.java>
"""
import sys

MUTANTS = [
    ("AMZ_01", "the plan has every calendar period but the last"),
    ("AMZ_02", "the disbursement period runs from (exclusive) to due (inclusive)"),
    ("AMZ_03", "the plan cut at the disbursement date is not rated"),
    ("AMZ_04", "no full-term tranche rejection"),
    ("AMZ_05", "the disbursement is added to the period's first stretch, never split"),
    ("AMZ_06", "the related periods start after the disbursement's period"),
    ("AMZ_07", "flat interest is scheduled as declining balance"),
    ("AMZ_08", "the balances are not rolled again after the installment"),
    ("AMZ_09", "a stretch that would start with nothing is not rejected"),
    ("AMZ_10", "the lines show the interest earned, not the interest due"),
    ("AMZ_11", "the annuity counts the first related period's growth too"),
    ("AMZ_12", "the last installment is not settled (no difference)"),
    ("AMZ_12_B", "no rejection of an installment carrying more principal than was disbursed"),
    ("AMZ_13", "no evening-out"),
    ("PLAN_01", "the down payment line's balance does not deduct the down payment"),
    ("PLAN_02", "an installment's total due is its principal only"),
    ("PLAN_03", "remainingDue of an installment line includes the line itself"),
    ("PLAN_04", "the totals' principal leaves out the down payment"),
    ("PLAN_04_B", "the totals' principal is the amount disbursed, not the lines' principal"),
    ("PLAN_05", "the full-term tranche is rejected before the cut plan is rated"),
]

EDITS = [
    # (snippet in the reference, replacement)
    ("public final class Amortization {", "public final class AmortizationMutants {\n\n    public enum Mutant {\n" +
     ",\n".join(f'        {n}("{d}")' for n, d in MUTANTS) +
     ";\n\n        public final String change;\n\n        Mutant(String change) {\n            this.change = change;\n        }\n\n"
     "        public String rule() {\n            String[] p = name().split(\"_\");\n            return p[0] + \"-\" + p[1];\n        }\n    }\n"),
    ("    private final RateFactors rates;\n\n    public Amortization(DueDates dueDates, RateFactors rates) {\n        this.dueDates = dueDates;\n        this.rates = rates;\n    }",
     "    private final RateFactors rates;\n    private final Mutant m;\n\n    public AmortizationMutants(DueDates dueDates, RateFactors rates, Mutant m) {\n"
     "        this.dueDates = dueDates;\n        this.rates = rates;\n        this.m = m;\n    }"),
    ("return new Run(request, rates).schedule(dueDates.periods(request), downPayment);", "return new Run(request, rates, m).schedule(dueDates.periods(request), downPayment);"),
    ("    private static final class Run {", "    private static final class Run {\n\n        private final Mutant m;"),
    ("        Run(ScheduleRequest q, RateFactors rates) {\n            this.q = q;\n            this.rates = rates;\n        }",
     "        Run(ScheduleRequest q, RateFactors rates, Mutant m) {\n            this.q = q;\n            this.rates = rates;\n            this.m = m;\n        }"),
    ("            Plan plan = Plan.of(calendar, zero);",
     "            Plan plan = Plan.of(m == Mutant.AMZ_01 && calendar.size() > 1 ? calendar.subList(0, calendar.size() - 1) : calendar, zero);"),
    ("                if (!disbursed && !d.isBefore(period.from()) && d.isBefore(period.due())) {\n                    disbursed = true;",
     "                if (!disbursed && (m == Mutant.AMZ_02 ? d.isAfter(period.from()) && !d.isAfter(period.due())\n"
     "                        : !d.isBefore(period.from()) && d.isBefore(period.due()))) {\n                    disbursed = true;\n"
     "                    if (m == Mutant.PLAN_05 && q.fullTermTranche()) {\n                        return rejected(ScheduleRejection.FULL_TERM_TRANCHE_NOT_OFFERED);\n                    }"),
    ("balanceBefore.plus(amount).minus(dp).amount(), null));", "(m == Mutant.PLAN_01 ? balanceBefore.plus(amount) : balanceBefore.plus(amount).minus(dp)).amount(), null));"),
    ("                Money interestDue = f.dueInterest();", "                Money interestDue = m == Mutant.AMZ_10 ? f.earnedInterest() : f.dueInterest();"),
    ("zero.plus(principalDue).plus(interestDue), f.balance() });", "m == Mutant.PLAN_02 ? zero.plus(principalDue) : zero.plus(principalDue).plus(interestDue), f.balance() });"),
    ("                            a[2].amount(), a[3].amount(), outstanding));", "                            a[2].amount(), a[3].amount(), m == Mutant.PLAN_03 ? outstanding.add(a[2].amount()) : outstanding));"),
    ("cumulativePrincipal.plus(dpTotal).amount(),",
     "(m == Mutant.PLAN_04 ? cumulativePrincipal : m == Mutant.PLAN_04_B ? toBeScheduled.plus(dpTotal) : cumulativePrincipal.plus(dpTotal)).amount(),"),
    ("                if (Money.positiveOrZero(plan.figures(i).duePrincipal()).exceeds(disbursed)) {",
     "                if (m != Mutant.AMZ_12_B && Money.positiveOrZero(plan.figures(i).duePrincipal()).exceeds(disbursed)) {"),
    ("            Step s = rate(cut, cut.all());", "            Step s = m == Mutant.AMZ_03 ? Step.to(cut) : rate(cut, cut.all());"),
    ("            if (q.fullTermTranche() && q.installments() > 0) {", "            if (m != Mutant.AMZ_04 && q.fullTermTranche() && q.installments() > 0) {"),
    ("            plan = plan.disburse(i, date, amount);", "            plan = m == Mutant.AMZ_05 ? addToFirstStretch(plan, i, amount) : plan.disburse(i, date, amount);"),
    ("            LocalDate dueFrom = plan.at(i).due();", "            LocalDate dueFrom = plan.at(i).due().plusDays(m == Mutant.AMZ_06 ? 1 : 0);"),
    ("            if (q.interestMethod() == InterestMethod.FLAT) {", "            if (m != Mutant.AMZ_07 && q.interestMethod() == InterestMethod.FLAT) {"),
    ("            s = installment(s.plan(), related).rollBalances();", "            s = m == Mutant.AMZ_08 ? Step.to(installment(s.plan(), related)) : roll(installment(s.plan(), related));"),
    ("        private static Plan installment(Plan plan, List<Integer> related) {", "        private Plan installment(Plan plan, List<Integer> related) {"),
    ("            for (int i : related.subList(1, related.size())) {", "            for (int i : m == Mutant.AMZ_11 ? related : related.subList(1, related.size())) {"),
    ("            Money difference = totalDisbursed.plus(totalDueInterest).minus(totalPayable);",
     "            Money difference = m == Mutant.AMZ_12 ? zero : totalDisbursed.plus(totalDueInterest).minus(totalPayable);"),
    ("        private Step evenOut(Plan plan, List<Integer> related) {", "        private Step evenOut(Plan plan, List<Integer> related) {\n            if (m == Mutant.AMZ_13) {\n                return Step.to(plan);\n            }"),
    ("        private static boolean inRun(Installment p, LocalDate firstFrom, LocalDate firstDue) {",
     """        /** AMZ-09's mutant: the balances rolled without the rejection of a stretch that would start with nothing. */
        private Step roll(Plan plan) {
            if (m != Mutant.AMZ_09) {
                return plan.rollBalances();
            }
            List<Installment> next = new ArrayList<>(plan.size());
            Figures previous = null;
            for (int i = 0; i < plan.size(); i++) {
                Installment p = plan.at(i);
                List<Stretch> stretches = new ArrayList<>(p.stretches());
                for (int j = 0; j < stretches.size(); j++) {
                    Money balance;
                    if (j == 0) {
                        if (i == 0) {
                            continue;
                        }
                        balance = Money.positiveOrZero(next.get(i - 1).lastStretch().carried().minus(previous.duePrincipal()));
                    } else {
                        balance = stretches.get(j - 1).carried();
                    }
                    stretches.set(j, stretches.get(j).withBalance(balance));
                }
                Installment rolled = p.withStretches(stretches);
                next.add(rolled);
                previous = Figures.of(rolled, previous, plan.zero());
            }
            return Step.to(plan.map((i, p) -> next.get(i)));
        }

        /** AMZ-05's mutant: the amount added to the period's first stretch. */
        private static Plan addToFirstStretch(Plan plan, int i, Money amount) {
            Installment p = plan.at(i);
            List<Stretch> stretches = new ArrayList<>(p.stretches());
            stretches.set(0, stretches.get(0).withDisbursement(stretches.get(0).disbursement().plus(amount)));
            return plan.with(i, p.withStretches(stretches));
        }

        private static boolean inRun(Installment p, LocalDate firstFrom, LocalDate firstDue) {"""),
]


def main(src, out):
    s = open(src, encoding="utf-8").read().replace("\r\n", "\n")
    for a, b in EDITS:
        n = s.count(a)
        if n != 1:
            raise SystemExit(f"snippet found {n} times (want 1): {a[:90]!r}")
        s = s.replace(a, b)
    # every other balance roll goes through the AMZ-09 switch
    s = s.replace(".rollBalances();", ".rollBalances();")
    s = s.replace("s = s.plan().rollBalances();", "s = roll(s.plan());").replace("Step s = trial.rollBalances();", "Step s = roll(trial);")
    s = s.replace("s = plan.map((i, p) -> taken.containsKey(i) ? p.withInstallment(taken.get(i)) : p).rollBalances();",
                  "s = roll(plan.map((i, p) -> taken.containsKey(i) ? p.withInstallment(taken.get(i)) : p));")
    s = s.replace(" * The amortization schedule", " * GENERATED by sme/tools/make_schedule_mutants.py from the reference's Amortization: do not edit.\n * The rule mutants of the amortization schedule", 1)
    open(out, "w", encoding="utf-8", newline="\n").write(s)
    print(f"{len(MUTANTS)} mutants written to {out}")


if __name__ == "__main__":
    main(*sys.argv[1:3])
