# Rule digest: schedule (the amortization schedule) → context `schedule`, rules AMZ and PLAN

**Legacy programs:** the schedule job's main program (the period loop, the lines and totals) and its subprograms FNEM0100
(build the interest model), FNEM0200 (apply a disbursement), FNEM0400 (the installment, EMI), FNEM0500 (the last unpaid
installment, deferred interest), FNEM0600 (the balance at a date), FNEM0700 (the derived amounts), FNEM0800 (queries on the
model), FNEM0900 (the evening-out of the installments) and FNLS0300 (the disbursement and down payment).

**In the service:** `ScheduleService.schedule(request)` through `LoansApplication.quotes()`: after the terms review (TRM),
`loans-schedule` computes the schedule. It gets the periods from the SPI `DueDates` and the rate factors from the SPI
`RateFactors` (their implementations come from the calendar and rates contexts, passed in by the application); it never calls
those contexts directly. The result is `Scheduled(Schedule)` or `Rejected(loanId, reason, null)`.

**Words.** A *period* is one installment's period from `DueDates` (`from`, `due`). Each period is made of *stretches*
(`from`, `to`, the two rate factors from `RateFactors`, a *disbursement* amount carried at the stretch's end, and a starting
*balance*); a new period has one stretch equal to the period. *Money* is an amount at the currency's decimal places: every
result is rounded to those places with the calculation's rounding mode; additions and subtractions are exact before that;
multiplications and divisions are first rounded to the calculation's precision (`mc`). An addition of zero leaves an amount as
it is.

| Rule | Statement | Legacy origin |
|---|---|---|
| AMZ-01 | The plan starts with the periods of `DueDates`, in order, each one stretch with zero factors, zero disbursement, zero balance, and no installment (0). | FNEM0100 |
| AMZ-02 | The disbursement falls in the first period with `from` ≤ disbursement date < `due`. If none: `DISBURSEMENT_OUTSIDE_TERM` (decision D-3). | the main program's period loop; FNLS0300 |
| AMZ-03 | Before the disbursement is recorded, the plan cut at the disbursement date is rated: in the period the date falls in, the stretch containing it ends at the date and later stretches go; periods due after the date lose their stretches (a period due on the date is kept whole); every remaining stretch is rated through `RateFactors`. A `RateRejected` there is the schedule's rejection, before anything else about the disbursement (so 30-day months with yearly installments are rejected before a full-term tranche). | FNEM0600 (the balance at the disbursement date) |
| AMZ-04 | A full-term tranche: `FULL_TERM_TRANCHE_NOT_OFFERED` (decision D-1), after AMZ-03. | FNEM0400 / the disbursement's legacy stop (RJTNNT02) |
| AMZ-05 | The disbursed amount is the principal (at the currency's places) less the down payment (TRM-14). It is recorded in the first period the date falls in, counting each period from `from` exclusive (inclusive for the first period) to `due` inclusive (so a date that is a period's start belongs to the period before it). In that period: if it is the last period and the date is its due date, its last stretch carries the amount when that stretch has 0 days, and otherwise the stretch is split (below); in any other period, the first stretch whose `to` is the date carries the amount, and if there is none the stretch is split. Splitting: the stretch containing the date (the last with `from` < date ≤ `to`; the first stretch if none) ends at the date and carries the amount; a new stretch from the date to its old end, with zero factors, disbursement and balance, follows it. | FNEM0200, FNEM0800 |
| AMZ-06 | The *related periods* are those due on or after the due date of the period AMZ-05 recorded the disbursement in, or, when the disbursement date is that period's due date and a next period exists, on or after the next period's due date. | FNEM0400 (1100-RELATED-PERIODS) |
| AMZ-07 | Flat interest: the related periods' stretches are rated (a `RateRejected` first), then `FLAT_INTEREST_NOT_OFFERED` (decision D-1). | FNEM0400 (1000-FLAT-INTEREST-METHOD) |
| AMZ-08 | Declining balance: the related periods' stretches are rated through `RateFactors` (a `RateRejected` is the schedule's rejection); then the balances are rolled (AMZ-09); then the installment (AMZ-11); the balances are rolled again; then the last installment is settled to the date AMZ-06 used (AMZ-12); then the evening-out (AMZ-13) if there are related periods. | FNEM0400 (2000-DECLINING-BALANCE) |
| AMZ-09 | Rolling the balances, period by period in order: the first stretch of a period after the first starts with the previous period's last stretch's balance plus its disbursement less the previous period's principal due (AMZ-10), not below 0; a later stretch of a period starts with the previous stretch's balance plus disbursement, and if that is 0 or less: `DOWN_PAYMENT_COVERS_PRINCIPAL` (decision D-1). The first period's first stretch keeps its balance. | FNEM0700 (2100-UPDATE-OUTSTANDING-BAL) |
| AMZ-10 | The figures of a period, from its own state and the previous period's figures (always computed from the current state: decision D-2): *growth* = 1 + the sum (exact) of its stretches' installment factors. A stretch's *interest* = balance × interest factor (`mc`) ÷ days(stretch from, period due) (`mc`) × days(stretch from, stretch to) (`mc`), 0 when days(stretch from, period due) is 0, and not below 0. *Earned interest* = the sum (exact) of its stretches' interest as Money, plus the previous period's unrecognized interest, not below 0. *Payable* = its installment. *Interest due* = the smaller of earned interest and payable, not below 0 (the payable on a tie). *Principal due* = payable − interest due, not below 0. *Unrecognized interest* = earned interest − interest due, not below 0. *Balance after the period* = its last stretch's balance + disbursement − principal due, not below 0. A period is *settled* when payable is 0. | FNEM0700 (3000-DERIVE-REPAYMENT-PERIOD, 3100-CALCULATED-DUE-INTEREST) |
| AMZ-11 | The equal installment: *P* = the product of the related periods' growth (each multiplication `mc`); *A* = 1, then for each related period after the first, *A* = 1 + *A* × its growth (`mc` each); both stripped of trailing zeros. The balance it amortizes: the balance after the period before the first related period (0 if none) plus the disbursements of the first related period. Installment = that balance × *P* (`mc`) ÷ *A* (`mc`), as Money; set on every related period unless it is negative. | FNEM0400 (3000-EMI-ON-ACTUAL-MODEL, 3100, 3200) |
| AMZ-12 | Settling the last installment (to a date): if any period's principal due (not below 0) exceeds the total disbursed (not below 0): `SINGLE_INSTALLMENT_AFTER_LATE_DISBURSEMENT` (decision D-1). The *last unsettled* period is the last one not settled; if all are, the last period if it has stretches and a balance above 0; if none, stop. Then *difference* = the total of all stretches' disbursements + the total of all periods' interest due − the total of all periods' payable; the last unsettled period's installment += difference. If that installment is now negative, it becomes 0 and the settlement is repeated from the start (the period before it takes the difference). | FNEM0500 (1200-FIND-LAST-UNPAID, 1300-ADJUST-LAST-UNPAID-EMI) |
| AMZ-13 | Evening-out, at most 3 rounds, each only while the previous one narrowed the gap: the *spread* of a run of periods is measured on its last two unsettled periods in a row (from the end): base = the earlier one's installment, difference = the later's − the earlier's (if no such pair: the first period's installment and 0). It is *worth spreading* when half the number of periods in the run (rounded down) is above 0, the difference is not 0, and 100 × |difference| exceeds that half (as Money). The spread installment = base + the *share*, where the share = difference ÷ (number of periods) (`mc`) taken as Money (at the currency's places with the rounding mode) before it is added: two roundings, not one (example: base 5,120.00, difference −118.60, 3 periods, precision 3 CEILING: −118.60 ÷ 3 = −39.533… → −39.5 → −39.50; spread 5,080.50). Stop when not worth spreading or when the spread installment equals the base. Otherwise, on a trial plan (the plan as it was at the first round, carried over from round to round): every period that starts no earlier than the first related period and is due no earlier than it gets the spread installment (if it is not negative); balances rolled; the last installment settled to the first related period's due date (AMZ-12); if the trial's spread (over all its periods) is not narrower than this round's, stop; otherwise the related periods take, in order, the installments of those trial periods; balances rolled; next round. | FNEM0900 |
| PLAN-01 | The lines follow the calendar's periods in order, one installment line per period (with the period's own `from` and `due`). The disbursement line (from = due = the disbursement date, amount = the principal at the currency's places, balance after = amount) comes just before the line of the period the disbursement date falls in (AMZ-02), followed, when the down payment is enabled, by the down payment line (from = due = the disbursement date, principal = total due = the down payment, balance after = the balance before the disbursement (AMZ-03) + the amount − the down payment). The periods before that one keep their installment lines, which owe nothing. Example: periods of 30 days from 2024-01-15 and a disbursement on 2024-02-14, the due date of period 1, so in period 2: installment 1 (2024-01-15 to 2024-02-14, 0.00), the disbursement, installment 2 (2024-02-14 to 2024-03-15), and so on. | FNLS0300 (1100-ADD-DISBURSEMENT-PERIOD, 1200-DOWN-PAYMENT), the main program |
| PLAN-02 | The down payment line and the installment lines are numbered in one sequence from 1, in the order of the lines (the disbursement line has no number): the down payment is number 1 only when no installment line comes before it; after installment 1 it is number 2 and the next installment 3. Each installment line has the period's principal due, interest due, fee 0, penalty 0, total due = principal + interest, and balance after = the period's balance after (AMZ-10), all in the final plan. | the main program |
| PLAN-03 | `remainingDue` of a down payment or installment line: the repayment expected (the down payment plus the total due of every installment) less the down payment and the totals due of the lines up to and including this one. | the main program |
| PLAN-04 | Totals: disbursed = the disbursed amount (AMZ-05) + the down payment; principal = the sum of the installments' principal + the down payment; interest = the sum of their interest; fee 0; penalty 0; repayment = the repayment expected (PLAN-03); outstanding 0. `termDays` = days(the first period's `from`, the last period's `due`); 0 without periods. Amounts are at the currency's places (scale included). | the main program |
| PLAN-05 | The order of the schedule's rejections: AMZ-02 (no disbursement period) for loans whose date falls in no period; otherwise AMZ-03, AMZ-04, then the rates of AMZ-07 or AMZ-08, AMZ-07's flat interest, AMZ-09, AMZ-12, in the order the calculation reaches them. | the main program and the subprograms' stops |

## Numbers

* `mc` = `new MathContext(precision, roundingMode)` of the request's calculation.
* Money at the currency's decimal places: an amount *x* becomes `x.stripTrailingZeros().setScale(decimalPlaces, roundingMode)`.
* Factors come from `RateFactors` with the calculation's precision as their decimal places (RATE-09).
* "Not below 0" leaves a positive amount as it is and gives 0 at the currency's places otherwise.

## Worked example

15,000.00 USD disbursed on the schedule start, 2023-01-28; 3 installments every 1 month; 9.4822% a year; 30-day months,
360-day years; precision 3, CEILING: every `mc` step keeps 3 significant digits, rounding up. (A loan of the corpus; its values
are the reference's.)

1. The periods (AMZ-01): 1: 2023-01-28 to 2023-02-28; 2: to 2023-03-28; 3: to 2023-04-28.
2. The disbursement falls in period 1 (AMZ-02); the plan cut at its date owes nothing (AMZ-03: 0.00). Period 1 splits at the
   date (AMZ-05): a stretch 01-28 to 01-28 carrying the disbursement 15,000.00, then 01-28 to 02-28. Related periods: 1 to 3.
3. The rate factors (`RateFactors`): installment factor 0.008 and interest factor 0.008 for each stretch with days (0.000 and
   0.008 for the stretch of 0 days). Growth (AMZ-10) = 1.008 for each period. Rolled (AMZ-09): period 1's second stretch starts
   with 0.00 + 15,000.00.
4. The installment (AMZ-11): *P* = 1.008 × 1.008 = 1.016064 → 1.02, × 1.008 = 1.02816 → 1.03. *A* = 1; 1 + 1 × 1.008 = 2.008 →
   2.01; 1 + (2.01 × 1.008 = 2.02608 → 2.03) = 3.03. Balance 15,000.00 × 1.03 = 15,450 → 15,500; ÷ 3.03 = 5,115.5… → 5,120:
   installment 5,120.00 on periods 1 to 3.
5. Rolled again (AMZ-10, AMZ-09). Period 1: 15,000.00 × 0.008 = 120; ÷ 31 days = 3.870… → 3.88; × 31 = 120.28 → 121: interest
   121.00, principal 5,120.00 − 121.00 = 4,999.00, balance 10,001.00. Period 2: 10,001.00 × 0.008 = 80.008 → 80.1; ÷ 28 →
   2.87; × 28 = 80.36 → 80.4: interest 80.40, principal 5,039.60, balance 4,961.40. Period 3: interest 40.00, principal 5,080.00,
   balance 0.00 (not below 0).
6. Settled (AMZ-12): 15,000.00 + 241.40 − 15,360.00 = −118.60, added to period 3's installment: 5,001.40.
7. Evening-out (AMZ-13). Round 1: base 5,120.00, difference −118.60 over 3 periods, worth spreading; spread installment
   5,120.00 + (−118.60 ÷ 3 → −39.5) = 5,080.50; settled: difference 0.90 (narrower: kept). Round 2: base 5,080.50, difference
   0.90; spread 5,080.80; settled: difference 0.00. Round 3: difference 0.00, not worth spreading: stop.
8. The lines (PLAN): the disbursement 15,000.00 (balance after 15,000.00); installments 1 to 3 of 5,080.80 each: principal
   4,959.80, 5,000.10, 5,040.10; interest 121.00, 80.70, 40.70; balance after 10,040.20, 5,040.10, 0.00; remaining due 10,161.60,
   5,080.80, 0.00. Totals: disbursed 15,000.00, principal 15,000.00, interest 242.40, repayment 15,242.40.

## Decisions

* D-2: every figure is computed from the current state; the legacy cache of a period's balance could go stale when every
  installment but the last rounds to zero. The scenarios of those loans carry the corrected balance (oracle A′).
* D-1, D-3: the legacy stops become the typed rejections named above.

## Not carried forward

* The deferral of interest a later period earns but cannot take into the last unpaid installment, and the interest
  "moved up" with it (FNEM0500 2000-DEFER-UNRECOGNIZED): in a quote with one disbursement it never changes the outcome. Its
  mutant was killed by none of the 136,530 loans of the corpus and none of 300,000 random requests (the periods it would
  draw interest from lie after the date it rates to, so they earn nothing). It belongs with repayments (wave 2).

* Repayments, charges, fees, penalties, interest pauses, re-ageing, balance corrections and capitalized income: the model
  carries them, the quote has none (zero everywhere).
* Several disbursements (tranches): the schedule has one; a second would be a bug, not a rule.
* The model's object identities and memo flags (FNMODEL): implementation details of the legacy cache.
