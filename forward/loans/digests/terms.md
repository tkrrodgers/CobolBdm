# Rule digest: terms (admissibility and the down payment) → context `terms`, rules TRM

**Legacy programs:** the schedule job's main program checks each loan request against an envelope (2200-CHECK-ENVELOPE) and
rejects what is outside it; FNLS0200 assembles the loan terms and rejects terms it cannot assemble. The run control (precision
and rounding mode) was checked once per run.

**In the service:** `loans-terms` reviews a `ScheduleRequest` before any calculation; the application returns its rejection
from `ScheduleService.schedule` (through `LoansApplication.quotes()`) as `Rejected(loanId, reason, field)`, where `field` names
the request field for `OUT_OF_RANGE` and is null otherwise. The checks run in the order of the rules; the first that fails
decides. Rejections found later, during the calculation (flat interest, full-term tranches, ...), belong to the schedule.
Decision D-6: the service's limits replace the COBOL's capacity limits; decision D-9: the calculation is checked per loan.

| Rule | Statement | Legacy origin |
|---|---|---|
| TRM-01 | `calculation.precision` from 1 to 34; otherwise `OUT_OF_RANGE`, field `calculation.precision`. | The run control (1 to 12, the run stopped otherwise; D-6, D-9) |
| TRM-02 | Rounding mode `UNNECESSARY`: `ROUNDING_MODE_NOT_OFFERED`. | The run control's modes (UNNECESSARY was not one; D-9) |
| TRM-03 | `installments` from 1 to 1,200; otherwise `OUT_OF_RANGE`, field `installments`. | RJENV020 (1 to 480, the COBOL's capacity; D-6) |
| TRM-04 | `principal` above 0 and below 10^15; otherwise `OUT_OF_RANGE`, field `principal`. | RJENV030 (below 10^9; D-6) |
| TRM-05 | `currency.decimalPlaces` from 0 to 4; otherwise `OUT_OF_RANGE`, field `currency.decimalPlaces`. Example: XAU with 5 decimal places. | RJENV040 |
| TRM-06 | `annualRatePercent` from 0 to 100, with at most 9 decimal places (trailing zeros do not count); otherwise `OUT_OF_RANGE`, field `annualRatePercent`. | RJENV050; the request's rate field has 9 decimals (D-12) |
| TRM-07 | `every` from 1 to 9,999; otherwise `OUT_OF_RANGE`, field `every`. | RJENV060 (365 days, 52 weeks, 12 months, 5 years: capacity; D-6) |
| TRM-08 | `scheduleStart`, then `disbursementDate`, from 1900-01-01 to 2199-12-31; otherwise `OUT_OF_RANGE`, field `scheduleStart` or `disbursementDate` (the first that fails). | RJENV070 (one code for both) |
| TRM-09 | When `downPaymentPercent` is given: from 0 to 100; otherwise `OUT_OF_RANGE`, field `downPaymentPercent`. | RJENV080 |
| TRM-10 | When `fixedLength` is given: (installments − 1) × every < fixedLength ≤ (installments + 1) × every; otherwise `OUT_OF_RANGE`, field `fixedLength`. Example: 12 monthly installments, every 1: 11 is rejected, 12 and 13 are accepted, 14 is rejected. | RJENV090 |
| TRM-11 | A currency with 0 decimal places and a rounding multiple above 0, with a principal above 0: `ROUNDING_MULTIPLE_NOT_OFFERED` (decision D-1). | FNLS0200 (the legacy reject RJTNNT03) |
| TRM-12 | A down payment enabled without a percentage: `DOWN_PAYMENT_PERCENT_MISSING`. | FNLS0200 (the legacy reject RJNULL01) |
| TRM-13 | Order: TRM-01, TRM-02, then TRM-03 to TRM-10 in that order (the legacy envelope's), then TRM-11, then TRM-12. Example: 0 installments and a principal of 0 give `OUT_OF_RANGE` on `installments`. | 2200-CHECK-ENVELOPE's order; the envelope before FNLS0200 |
| TRM-14 | The down payment (when enabled): the principal at the currency's decimal places times (`downPaymentPercent` ÷ 100, rounded to the calculation's precision), the product rounded to the precision and then to the currency's decimal places with the rounding mode; 0 when the principal is 0 or less. The schedule takes it at the disbursement. | FNLS0200, FNLS0300 (the percentage of the disbursement) |

## Numbers

* `principal`, `annualRatePercent` and `downPaymentPercent` are `BigDecimal` and compared by value (`compareTo`).
* TRM-14 is observable only in a schedule (the down payment line and the totals): the schedule slice's scenarios check it.

## Not carried forward

* RJENV001 (a malformed request record) and RJENV010 (a currency missing from the currency file): the API cannot carry them
  (decision D-5). The ERP's adapters validate their own input.
* The order of loan ids in a run (the job stopped on a request out of order): no order (decision D-10).
