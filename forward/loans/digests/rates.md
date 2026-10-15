# Rule digest: rates (interest rate factors) → context `rates`, rules RATE

**Legacy program:** FNEM0300, a subprogram of the schedule calculation. For the stretches of a loan's repayment periods it
computes two rate factors each: the share of the annual interest rate the stretch earns, for the installment, and the share
from the stretch's start to its period's due date, for the interest. It changes nothing else.

**In the service:** the SPI `com.mlcpu.loans.api.spi.RateFactors`: `RateOutcome factors(RateQuery query, Calculation calculation)`,
implemented in `loans-rates` and reached through `LoansApplication.rateFactors()`. One call rates one stretch: the query holds
the schedule start, the stretch's period (`period.from`, `period.due`), the stretch (`from`, `to`), the annual rate in percent,
the frequency, `every`, and the day-count conventions. The answer is `Factors(installment, interest)` or
`RateRejected(reason)`. Legacy rule ids refer to the requirements packet of FNEM0300.

**Arithmetic.** `mc` is the calculation: `new MathContext(precision, roundingMode)`. Every multiplication, division and
addition below is rounded with `mc` unless it says *exact*; the order of the operations is part of the rule.

| Rule | Statement | Legacy origin |
|---|---|---|
| RATE-01 | The rate: `annualRatePercent ÷ 100` (`mc`). | R-FNEM0300-010 (2000-RATE-FACTOR) |
| RATE-02 | Days in the year: `DAYS_360`, `DAYS_364`, `DAYS_365` give 360, 364, 365; `ACTUAL` gives the length (365 or 366) of the year in which the stretch starts (`from`). | R-FNEM0300-011, -012 |
| RATE-03 | The two factors of a stretch use the same rules with different end dates: the **installment** factor covers `from` to `to` (the stretch itself) with `every` intervals; the **interest** factor covers `from` to `period.due` with the period's length in intervals (RATE-08) in place of `every` (with 30-day months; otherwise both use 1). | R-FNEM0300-002 to -004 (1100-RATE-FACTOR-FOR-PERIOD) |
| RATE-04 | Year crossing, first: when `yearDays` is `ACTUAL` and the end date is in a later year than `from`, the factor is `rate × fractions` (`mc`), whatever the month days. `fractions` is the sum (`mc`, starting at 0) over each year *y* from `from`'s year to the end date's year of `days(at, end_y) ÷ length of year y` (`mc`), where `end_y` is 31 December of *y* (the end date for the last year) and `at` is `from`, then the previous `end_y`. Example: 12%, actual days in the year, from 2023-12-01 to 2024-01-31, precision 12, HALF_EVEN: 30 ÷ 365 = 0.0821917808219, 31 ÷ 366 = 0.0846994535519, sum 0.166891234374; × 0.12 → factor 0.020026948125. | R-FNEM0300-021 to -030 (3000-PERIOD-FRACTIONS), -062, -063 (6000-PARTIAL-PERIOD-FACTOR) |
| RATE-05 | Actual days in the month (`monthDays` `ACTUAL`, no year crossing): `fraction = days(from, end) × 1` (`mc`) `÷ daysInYear` (`mc`); factor = `rate × fraction` (`mc`). The `× 1` is the legacy's: the day count is itself rounded to the precision, which changes it when it has more digits than the precision. Example: 9.48222%, 31 days, `DAYS_365`, precision 6, HALF_UP: rate 0.0948222, fraction 0.0849315, rate × fraction 0.00805339 → factor 0.008053. Example with a long stretch: 11.98%, 1919-07-06 to 1922-07-06 (1,096 days, every 3 years), `DAYS_360`, precision 3, FLOOR: rate 0.119; 1,096 × 1 → 1,090; ÷ 360 → 3.02; × 0.119 = 0.35938 → factor 0.359 (with the days left exact: 3.04 and 0.361, which is wrong). | R-FNEM0300-013 (2000-RATE-FACTOR), -053 to -061 |
| RATE-06 | 30-day months (`monthDays` `THIRTY`, no year crossing): `intervalDays` is 1 for `DAYS`, 7 for `WEEKS`, 30 for `MONTHS`; `fraction = intervalDays × intervals` (`mc`) `÷ daysInYear` (`mc`); factor = `rate × fraction` (`mc`) `× days(from, end)` (`mc`) `÷ days(period.from, period.due)` (`mc`). A period of 0 days gives 0. Example: 7%, monthly, every 1, `DAYS_360`, a stretch of 31 days in a 31-day period, precision 12, HALF_EVEN: 30 ÷ 360 = 0.0833333333333; × 0.07 = 0.00583333333333; × 31 = 0.180833333333; ÷ 31 = 0.00583333333332 → factor 0.005833333333. | R-FNEM0300-014 to -020 (2000-RATE-FACTOR, 2500-BASED-ON-FREQUENCY), -053 to -061 (5000, 5100) |
| RATE-07 | The interest factor with 30-day months uses the period's length in intervals (RATE-08) as `intervals`, and `days(from, period.due)` as the stretch days. | R-FNEM0300-002 to -004, -014 to -019 |
| RATE-08 | The period's length in intervals (unit: the frequency's day, week, month or year). From the seed (RATE-11): `before` = whole units from the seed to `period.from` (monthly: to the day after `period.from` when `period.from` is the last day of its month and the seed's day of the month is later). Then for *m* = `before` + 1, `before` + 2, ...: the date seed + *m* units; while it is not after `period.due`, the next *m*. At the first date after `period.due`: *whole* = *m* − `before` − 1, *base* = seed + *whole* units, and the length is *whole* + `days(base, period.due) ÷ days(base, that date)` (the division `mc`, the addition exact). If a date lands exactly on `period.due` first, the length is the whole count reached. Example: start 2023-01-29, disbursed 2023-01-31, monthly, so the period runs 2023-02-28 to 2023-03-31 and the seed is its start (RATE-11): 2023-03-28 is not after the due date, 2023-04-28 is: whole 1, base 2023-03-28, length 1 + 3 ÷ 31 = 1.0967741935484 (precision 12); with 7%, `DAYS_360`, the interest factor of the stretch 2023-02-28 to 2023-03-31 is 0.006397849462. | R-FNEM0300-031 to -038 (4000-PERIOD-RATIO), -046 to -052 (4200-RATIO-STEP) |
| RATE-09 | Every factor is given with as many decimal places as the calculation's precision, rounded with its rounding mode (`setScale(precision, roundingMode)` of the result). Example: precision 3, HALF_UP, 0.0058333… → 0.006; precision 12 → 0.005833333333. | R-FNEM0300-057 to -061 (5100-RATE-TIMES-FRACTION: the money kernel's rate scaling) |
| RATE-10 | 30-day months with `YEARS` frequency have no rule: when the stretch does not cross a year (RATE-04) for the installment factor, or for the interest factor, the answer is `RateRejected(PERIOD_NOT_SUPPORTED_WITH_THIRTY_DAY_MONTHS)` (decision D-1). The installment factor is checked first. | R-FNEM0300-020 (2500-BASED-ON-FREQUENCY: the legacy reject RJUNSP01) |
| RATE-11 | The seed of RATE-08: count *n* = whole units from the schedule start to `period.due` (at least 1); lower *n* while start + (*n* − 1) units is not before `period.due` (*n* > 1); raise *n* while start + *n* units is before `period.due`. If start + *n* units is `period.due` and that date minus `every` units is `period.from`, the seed is the schedule start; otherwise it is `period.from`. | R-FNEM0300-039 to -045 (4100-SEED-DATE) |

## Order

Per stretch: the installment factor, then the interest factor; for each, RATE-10's check, then RATE-04 if the dates cross a
year, else RATE-05 or RATE-06/RATE-07 by the month days; RATE-09 last.

## Numbers

* Dates are `java.time.LocalDate`; `days(a, b)` is `ChronoUnit.DAYS.between(a, b)`; units are added with `plusDays`,
  `plusWeeks`, `plusMonths`, `plusYears`; "whole units between" is `ChronoUnit.<UNIT>.between`.
* Precision is 1 to 34; factors never have more decimal places than the precision.

## Not carried forward

* The till-date request (R-FNEM0300-005 to -009, 1500-TILL-DATE-INCLUSIVE: cut a stretch at a date, rate everything, zero the
  stretches after the date): the schedule does it with this SPI (the cutting and the zeroing are the schedule's rules).
* Unknown month-day and year-day codes (the legacy reject RJUNSP02): unrepresentable, they are enums (decision D-5).
* The decimal-floating operations (R-FNEM0300-064 to -068): `BigDecimal` with `mc` replaces them.
* R-FNEM0300-069 (size error): the legacy program's capacity.
