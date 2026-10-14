# Rule digest: disclosure (the schedule document and the cost of credit) → context `disclosure`, rules RPT and DISC

**Legacy programs:** the schedule job's report step, FNRP0100, prints each loan's schedule: a heading (loan, currency, decimal
places, term days, periods), one line per schedule line and a totals line. There is no legacy disclosure: the cost of credit
is a new feature (decision D-8 for the document; section 6 of SPEC.md for the disclosure).

**In the service:** `ScheduleService.document(request)` and `ScheduleService.disclose(request)` through
`LoansApplication.quotes()`: the application computes the schedule (as `schedule(request)`), and `loans-disclosure` makes the
document or the disclosure from it. The context depends only on the API (`Schedule`, its lines and totals); it never calls the
schedule context. A rejected schedule gives the same rejection. Results: `Documented(ScheduleDocument)` or `Rejected`;
`Disclosed(Disclosure)` or `Rejected`.

## The schedule document (RPT)

| Rule | Statement | Legacy origin |
|---|---|---|
| RPT-01 | The heading is the schedule's: `loanId`, `currencyCode`, `decimalPlaces`, `termDays`; `periods` is the number of schedule lines (the disbursement and a down payment included). | FNRP0100 2000-LOAN-HEADER (the schedule header's period count) |
| RPT-02 | One document line per schedule line, in the schedule's order. `type`: `DISBURSEMENT`, `DOWN_PAYMENT` or `INSTALLMENT`; `number`: 0 for the disbursement, the line's number otherwise; `from` and `due` as the line's. | FNRP0100 3000-PERIOD-LINE |
| RPT-03 | The amounts by type. Disbursement: `principal` = its amount, `interest` 0, `totalDue` 0. Down payment: its `principal`, `interest` 0, its `totalDue`. Installment: its `principal`, `interest`, `totalDue`. Every line's `outstanding` is its `balanceAfter` (not `remainingDue`). A 0 is written at the currency's decimal places (USD: `0.00`). | FNRP0100 3000-PERIOD-LINE (an amount the line does not have was zero in the schedule record) |
| RPT-04 | The totals: `totalPrincipal`, `totalInterest`, `totalRepayment` and `totalDisbursed` are the schedule totals' `principal`, `interest`, `repayment` and `disbursed`. | FNRP0100 4000-LOAN-TOTALS |
| RPT-05 | A rejected schedule gives no document: the same `Rejected` (loan id, reason, field). | The report printed only scheduled loans |

Amounts keep the schedule's values and scale (the currency's decimal places); the legacy report edited them with four
decimals and thousands separators, which is layout (D-8).

## The cost of credit (DISC)

The figures of a scheduled loan. The **advance** is the amount financed, made on the disbursement date (the date of the
disbursement line). The **payments** are the installment lines due **after** the disbursement date, each its `totalDue` on its
`due` date, in order (an installment due on or before the disbursement date carries nothing and is not a payment).

| Rule | Statement | Origin |
|---|---|---|
| DISC-01 | Amount financed = the disbursement line's `amount` − the down payment line's `principal` (0 without a down payment), at the currency's decimal places. | New (Truth in Lending: amount financed) |
| DISC-02 | Total of payments = the sum of `totalDue` of all installment lines, at the currency's decimal places. | New |
| DISC-03 | Finance charge = total of payments − amount financed. | New |
| DISC-04 | The annual percentage rate (APR) is the rate of the actuarial method of 12 CFR 1026 Appendix J, with one advance: amount financed = Σ payment*ₖ* ÷ ((1 + *fₖ*·*i*) × (1 + *i*)^*tₖ*), where *i* is the rate per unit-period, *tₖ* the full unit-periods and *fₖ* the fraction of a unit-period from the advance to payment *k* (DISC-08, DISC-09); APR = *i* × unit-periods per year × 100. | Appendix J (b)(1), (b)(8) |
| DISC-05 | The disclosed APR is the percentage *r* with two decimal places such that PV(*r* − 0.005) ≥ amount financed > PV(*r* + 0.005), where PV(*x*) is the right-hand side of DISC-04 at APR *x* (that is, the exact rate rounded half up to two decimals; PV falls as the rate rises). Compute PV with 34 significant digits (`MathContext.DECIMAL128`). | FE 7.5 (defines the rounding whatever the solver) |
| DISC-06 | A finance charge of 0 (or less) gives an APR of `0.00`. | New |
| DISC-07 | A rejected schedule gives no disclosure: the same `Rejected`. | New |
| DISC-08 | The unit-period. **One payment:** the term (advance to payment); one unit-period in the term, and in a year 12 ÷ the term's months when the term is a whole number of months, else 365 ÷ the term's days; when 12 months back from the payment (DISC-09) fall on or after the advance, the term is a year or more and the unit-period is a year. **Several payments:** the periods are advance to first payment, then each payment to the next. A period is *k months* when it is a whole number (*k* ≥ 1) of months (DISC-09's month count, nothing left over), else its number of days. The unit-period is the period that occurs most often among those that occur more than once; on a tie, the shorter (a month counts 30 days; between equal lengths, the days). Then: *k months* with *k* ≥ 12, or more than 365 days → a year; *k months* (*k* ≤ 11) → *k* months; a multiple of 7 days → that many weeks; other days → that many days (an extension: Appendix J has no unit-period of several days). If no period occurs more than once, the unit-period is the standard interval nearest the average period (total days from the advance to the last payment ÷ the number of periods), among: 1 day, 1 to 52 weeks (7 days each), a semimonth (15 days), 1 to 11 months (30 days each) and a year (365 days); on a tie, the shorter. | Appendix J (b)(3), (b)(4); the several-day unit-period is the SME's |
| DISC-09 | Counting from the advance *A* to a payment *P*. *Months back:* *k* months back from *P* is the same day *k* months earlier, or that month's last day when it has no such day; when *P* is the last day of its month, it is the last day of the month *k* months earlier. The *full months* from *A* to *P* are the most months back from *P* that stay on or after *A*; the *remaining days* are the days from *A* to that date. **Monthly** (1 month): *t* = full months, *f* = remaining days ÷ 30; 12 per year. ***m* months** (2 to 11): *D* = 30 × full months + remaining days; *t* = *D* ÷ (30*m*) (whole part), *f* = the rest ÷ (30*m*); 12 ÷ *m* per year. **Semimonth:** the same with 15 for 30*m*; 24 per year. ***w* weeks:** the days from *A* to *P* ÷ 7*w*, *t* the whole part, *f* the rest ÷ 7*w*; 52 ÷ *w* per year. ***d* days:** the days ÷ *d*, likewise; 365 ÷ *d* per year. **Year:** *t* = full months ÷ 12 (whole part); from *A* to the date 12*t* months back from *P*: if it is a whole number of months *j* (nothing left over), *f* = *j* ÷ 12, else *f* = its days ÷ 365; 1 per year. **One payment, term within a year:** *t* = 1, *f* = 0. Divisions to 34 significant digits. | Appendix J (b)(3)(iv), (b)(5) |

## Numbers

* `amountFinanced`, `financeCharge` and `totalOfPayments` are at the currency's decimal places (as the schedule's amounts);
  `annualPercentageRate` has two decimal places (`7.25`, `0.00`, `1200.00`).
* Example (monthly): 1,000.00 disbursed 2024-01-15, three installments of 340.02 on 2024-02-15, 03-15 and 04-15. Amount
  financed 1,000.00; total of payments 1,020.06; finance charge 20.06; unit-period one month (three periods of one month);
  *t* = 1, 2, 3 and *f* = 0; APR = 12.00.
* The same disbursed on 2024-01-20: the periods are 26 days, 1 month, 1 month, so the unit-period is one month; from
  2024-01-20 to 2024-02-15 there are 0 full months and 26 days, so *t* = 0, 1, 2 and *f* = 26 ÷ 30 for every payment;
  APR = 12.86.
* Example (weeks): payments every 4 weeks from an advance on 1969-02-06. The first period, 1969-02-06 to 1969-03-06, is 28 days
  but also a whole month (February), so it counts as 1 month; the next ones are 28 days each. The most common period is 28 days,
  a multiple of 7: the unit-period is 4 weeks, 13 per year.
* Example (days, the extension): 9 payments every 15 days from an advance on 2015-12-30, the last 12 days after the one before
  it. The most common period is 15 days, not a multiple of 7: the unit-period is 15 days, 365 ÷ 15 = 24.33… per year (not 24:
  a semimonth is only a standard interval for DISC-08's average). The last payment, 132 days after the advance: *t* = 8,
  *f* = 12 ÷ 15 = 0.8.

## Not carried forward

* The legacy report's pages, carriage control, edited numbers and run counts (D-8): layout.
* Appendix J's several advances, unequal advances and the payment-schedule rules for irregular first periods beyond DISC-08:
  a quote has one advance.
