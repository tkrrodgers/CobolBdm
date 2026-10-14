# Rule digest: calendar (repayment periods) → context `calendar`, rules CAL

**Legacy program:** FNDG0100, a subprogram of the schedule job's main step. For each loan it builds the expected repayment
periods: one per installment, each with a period number, a start ("from") date and a due date. It reads only the loan terms
named below; it computes no amounts.

**In the service:** the SPI `com.mlcpu.loans.api.spi.DueDates`: `List<Period> periods(ScheduleRequest request)`, implemented
in `loans-calendar` and reached through `LoansApplication.dueDates()`. It reads `scheduleStart`, `installments`, `every`,
`frequency`, `disbursementDate` (its day of the month only) and `fixedLength`; every other field of the request is ignored.
Legacy rule ids refer to the requirements packet of FNDG0100.

| Rule | Statement | Legacy origin |
|---|---|---|
| CAL-01 | A loan with *n* installments has exactly *n* periods, in order. Period *k* runs from the due date of period *k*−1 to its own due date; period 1 runs from the schedule start. Example: start 2023-01-28, 3 monthly installments: (2023-01-28, 2023-02-28), (2023-02-28, 2023-03-28), (2023-03-28, 2023-04-28). | R-FNDG0100-001, -002, -005 (0000-MAIN, 1000-REPAYMENT-PERIOD) |
| CAL-02 | The disbursement date does not move the calendar: period 1 starts on the schedule start whatever the disbursement date is. The disbursement date's day of the month is used by CAL-07 only. | R-FNDG0100-001 (the start is the submitted-on date) |
| CAL-03 | `DAYS`: each due date is the previous one (the start for period 1) plus `every` days. Example: start 2023-03-01, every 10: 2023-03-11, 2023-03-21. | R-FNDG0100-006 (2000-NEXT-REPAYMENT-DATE) |
| CAL-04 | `WEEKS`: each due date is the previous one plus 7 × `every` days. Example: start 2023-01-28, every 2: 2023-02-11, 2023-02-25. | R-FNDG0100-006 |
| CAL-05 | `MONTHS`: each due date is the previous one plus `every` calendar months, keeping the day of the month; when that day does not exist in the target month, the month's last day. Example: 2023-01-31 + 1 month = 2023-02-28; 2024-01-31 + 1 month = 2024-02-29. CAL-07 may then change the day. | R-FNDG0100-006 |
| CAL-06 | `YEARS`: each due date is the previous one plus `every` calendar years; 29 February becomes 28 February in a year that has no 29 February. Example: 2024-02-29 + 1 year = 2025-02-28. | R-FNDG0100-006 |
| CAL-07 | Month-end day (`MONTHS` only): when the disbursement date's day of the month is above 28 and the due date from CAL-05 has a day of the month of 28 or more, the due date's day becomes the smaller of the disbursement day and the length of the due date's month. Example: start 2023-01-31, disbursed 2023-01-31, monthly: 2023-02-28, 2023-03-31, 2023-04-30 (2023-02-28 + 1 month is 2023-03-28, day 28, so it becomes 31). With a disbursement day of 28 or less nothing changes. | R-FNDG0100-007 to -012 (2100-ADJUST-DATE) |
| CAL-08 | Each due date steps from the previous period's due date as finally set (after CAL-07), not from the schedule start, so a shortened month carries forward. Example: start 2023-01-31, disbursed 2023-02-10 (day 10), monthly: 2023-02-28, 2023-03-28, 2023-04-28. | R-FNDG0100-002, -003, -005 |
| CAL-09 | Fixed length: when `fixedLength` is set, the last period's due date is the schedule start plus `fixedLength` units of the frequency (CAL-03 to CAL-06, **without** CAL-07), whenever that differs from the date CAL-03 to CAL-07 gave. Only the last period changes. Example: start 2023-01-15, 3 monthly installments, fixed length 4: 2023-02-15, 2023-03-15, 2023-05-15. Example without CAL-07: start 2023-01-29, disbursed 2023-01-31, 2 monthly installments, fixed length 2: 2023-02-28, then 2023-03-29 (not 2023-03-31). | R-FNDG0100-004, -013 to -015 (3000-MAX-DATE-FOR-FIXED-LENGTH) |

## Order

For each period, in this order: the step from the previous due date (CAL-03 to CAL-06, by frequency), the month-end day
(CAL-07, monthly only), and for the last period the fixed length (CAL-09), which replaces the date. The next period steps
from the date so set (CAL-08).

## Numbers

* Dates are `java.time.LocalDate`; there are no times and no time zones.
* `installments` and `every` are at least 1 (the terms review rejects anything else before the calendar is reached).

## Not carried forward

* R-FNDG0100-016 (4000-ADJUST-REPAYMENT-DATE): the legacy program computes a further date that it never uses (the hook for
  holidays and working days, which this estate does not have). No rule.
* R-FNDG0100-017 (9997-SIZE-ERROR) and the 480-period limit: the legacy program's capacity. The service's limits are the
  terms review's (decision D-6).
* The reject RJNULL02 (a fixed length with a frequency that does not advance): frequencies are an enum, so the API cannot
  express it (decision D-5).
