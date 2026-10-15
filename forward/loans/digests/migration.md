# Rule digest: migration (the legacy loan book, engine side) → context `migration`, rules MIG

**Legacy programs:** the schedule job read a loan request file (one record per loan), the run control (the calculation of the
run) and the currency file, and wrote, for each request in order, either a schedule (a header record and one record per line:
the disbursement `D`, the down payment `P`, the installments `R`) or a reject record with its code. Those datasets are the
legacy loan book (FE 9.2).

**In the service:** `LoansApplication.migration()` gives a `LegacyMigration` (package `com.mlcpu.loans.api.migration`). Its
input is a `LegacyLoan`: the request, the run control, the currency (null when the currency file does not have the request's
currency), and what the legacy wrote for the loan: the schedule's header and lines in sequence order, or the reject. The legacy
records are the generated types of `com.mlcpu.loans.api.legacy` (one Java record per record layout, decoded by `LegacyCodec`;
their components are named after the copybook fields, without the prefix: `RQ-DISB-AMOUNT` is `disbAmount`). `terms` maps the
request onto the service's terms; `reconcile` recomputes the loan with `ScheduleService.schedule` (from `quotes()`) and compares.
The context implements the use case; the application passes it the `ScheduleService`.

## Terms (FE LEG-02)

| Rule | Statement |
|---|---|
| MIG-01 | `loanId` = `loanId` (the 16 digits as they are), `scheduleStart` = `schedStartDate`, `principal` = `disbAmount` (as decoded: 4 decimal places), `disbursementDate` = `disbDate`, `installments` = `numRepayments`, `every` = `repayEvery`, `annualRatePercent` = `annualRate` (9 decimal places), `downPaymentEnabled` = `downpayEnabled`, `fullTermTranche` = `fullTermTranche`. |
| MIG-02 | `currency` = `CurrencyTerms(code, decimalPlaces, roundingMultiple)` of the currency record; `roundingMultiple` is its `multiples` when `multiplesInd` is true, else null. |
| MIG-03 | Codes: `freqType` `DAYS`, `WEEKS`, `MONTHS`, `YEARS` → the `Frequency` of that name; `daysInMonth` 1 → `MonthDays.ACTUAL`, 30 → `THIRTY`; `daysInYear` 1 → `YearDays.ACTUAL`, 360 → `DAYS_360`, 364 → `DAYS_364`, 365 → `DAYS_365`; `interestMethod` `DECLINING_BALANCE`, `FLAT` → the `InterestMethod` of that name. |
| MIG-04 | `downPaymentPercent` = `downpayPct` when `downpayPctInd` is true, else null; `fixedLength` = `fixedLength` when `fixedLengthInd` is true, else null. Not carried (decision D-4): `multiplesInd` and `multiples` of the request, `diyStrategy`, `allowPartial`, `intRecogDisb`. |
| MIG-05 | `calculation` = `Calculation(precision, roundingMode)` of the run control; its `roundingMode` `UP`, `DOWN`, `CEILING`, `FLOOR`, `HALF_UP`, `HALF_DOWN`, `HALF_EVEN` → the `java.math.RoundingMode` of that name. |
| MIG-06 | Unrepresentable (decision D-5): the first of these checks that fails gives `Unrepresentable(loanId, field, value)` instead of the terms: the currency is null (field `RQ-CURRENCY-CODE`, value the request's `currencyCode`); `freqType` is not one of MIG-03's (`RQ-FREQ-TYPE`); `daysInMonth` (`RQ-DAYS-IN-MONTH`, the number as text); `daysInYear` (`RQ-DAYS-IN-YEAR`); `interestMethod` (`RQ-INTEREST-METHOD`); the run control's `roundingMode` (`RC-ROUNDING-MODE`). The value is the record's, as decoded. Otherwise `LegacyTerms(terms)`. |

## Reconciliation (FE LEG-03, LEG-04)

`reconcile` returns `Reconciliation(loanId, outcome, decision, field, legacyValue, serviceValue, legacyCode, serviceRejection)`.
`legacyCode` is the reject's code whenever the legacy rejected the request, else null. `serviceRejection` is the reason of the
service's `Rejected` whenever the service rejects the terms, else null. The other components are null unless a rule sets them.

| Rule | Statement |
|---|---|
| MIG-07 | Terms first: an `Unrepresentable` gives `EXPLAINED`, decision `D-5`, field the unrepresentable field (with `legacyCode` when the legacy rejected; `serviceRejection` null: the service is not asked). Otherwise the service's outcome is `schedule(terms)`. |
| MIG-08 | The legacy rejected (code c). When the service rejects with the rejection whose `legacyCode()` is c, or c starts with `RJENV` and the service's reason is `OUT_OF_RANGE`: `LEGACY_REJECT`. (A legacy reject for a cause the API cannot express, an unknown frequency, day count or method, never gets here: its terms are `Unrepresentable`, MIG-07.) Otherwise, when c starts with `RJENV` and the service schedules the loan (its limits are wider): `EXPLAINED`, `D-6`, field `outcome`. Otherwise `DIFF`, field `outcome`, `legacyValue` c, `serviceValue` the service's reason (its name) or `scheduled`. |
| MIG-09 | The legacy scheduled the loan but its schedule has no disbursement line (decision D-3: the legacy wrote an all-zero schedule): when the service rejects with `DISBURSEMENT_OUTSIDE_TERM`, `EXPLAINED`, `D-3`, field `outcome`; otherwise `DIFF`, field `outcome`, `legacyValue` `scheduled without a disbursement line`, `serviceValue` the service's reason or `scheduled`. |
| MIG-10 | The legacy scheduled the loan and the service rejects it: `DIFF`, field `outcome`, `legacyValue` `scheduled`, `serviceValue` the reason's name. |
| MIG-11 | Both scheduled: compare, in this order, `currencyCode`, `decimalPlaces`, `termDays` (header `currencyCode`, `decimalPlaces`, `termDays`); `totals.disbursed`, `totals.principal`, `totals.interest`, `totals.fee`, `totals.penalty`, `totals.repayment`, `totals.outstanding` (header `totDisbursed` ... `totOutstanding`); then `lines`, the number of lines (when it differs, nothing after it is compared); then each line in order, numbered from 1 (the disbursement line is usually line 1): `line n kind` (the legacy's `recType` against `D`, `P` or `R` for a `DisbursementLine`, `DownPaymentLine`, `InstallmentLine`; when it differs, that line's other fields are not compared), then for a disbursement line `from`, `due`, `amount` (`principal`), `balanceAfter` (`outstBalance`); for a down payment line `number` (`periodNumber`), `from`, `due`, `principal`, `totalDue`, `balanceAfter` (`outstBalance`), `remainingDue` (`totOutstanding`); for an installment line `number`, `from`, `due`, `principal`, `interest`, `fee`, `penalty`, `totalDue`, `balanceAfter`, `remainingDue`. Field names are `line n name` (`line 3 interest`). Amounts are equal when equal in value (`compareTo`), whatever their scale. No difference: `MATCH`. |
| MIG-12 | Decision D-2 (the legacy's stale balance): when every difference is the `balanceAfter` of a down payment or installment line, and the legacy's balances do not run on (some down payment or installment line's `outstBalance` is not the previous line's `outstBalance` minus its own `principal`): `EXPLAINED`, `D-2`, field the first differing field. |
| MIG-13 | Any other difference: `DIFF`, field the first differing field in MIG-11's order, `legacyValue` and `serviceValue` its two values as text: amounts as `toPlainString()` (the legacy's with its 4 decimal places, the service's at its scale), dates as yyyy-mm-dd, numbers and codes as they are. |

## Examples

* Loan 0000000000000001 of the edge book (USD, 15,000.0000 over 3 monthly installments, 9.4822%, 30/360, declining balance,
  precision 12, HALF_EVEN): the terms are `scheduleStart` 2023-01-28, `principal` 15000.0000, `annualRatePercent` 9.482200000,
  `downPaymentPercent` null (the indicator is N, the field 0E-9 is not carried), `fixedLength` null; the service's schedule equals
  the legacy's: `MATCH`.
* A legacy reject `RJTNNT05` (a 100% down payment) and the service's `DOWN_PAYMENT_COVERS_PRINCIPAL` (legacy code RJTNNT05):
  `LEGACY_REJECT`, `legacyCode` RJTNNT05, `serviceRejection` DOWN_PAYMENT_COVERS_PRINCIPAL.
* The frequency `MONTHLY` (legacy reject RJFREQ01): terms `Unrepresentable(loanId, "RQ-FREQ-TYPE", "MONTHLY")`; reconciliation
  `EXPLAINED`, `D-5`, field `RQ-FREQ-TYPE`, `legacyCode` RJFREQ01.

## Not here

The ERP side of the migration (FE LEG-01, LEG-05): decoding the datasets (the generated `LegacyCodec`), booking the loans the
ledger accepts, storing the legacy schedule as version 0, the reconciliation rows, the run's atomicity and idempotence. Those are
the plugin's.
