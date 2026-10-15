# Loans Service: specification

**Owner:** the SME. **Status:** Phase 0. Complete for the calendar, rates, terms, schedule, disclosure and servicing slices; the sections marked *later* are completed with
their slices (their rule digests are not written yet).

## 1. Purpose

The Loans Service computes the repayment schedule of an equal-installment, declining-balance loan from proposed loan terms: a
quote that stores nothing. It replaces the schedule calculation of a COBOL batch estate, the **legacy system**: a schedule job
whose main program reads loan requests and calls subprograms for the terms, the repayment calendar, the interest rate factors
and the schedule, and writes each loan's schedule or its rejection. The service will become the loans capability of an ERP
(booking, disbursement, billing; *later*).

The service keeps the legacy business rules exactly, except where section 7 lists a decision. The rule digests
(`digests/*.md`) state every rule with its legacy origin. This document gives the service's shape and its cross-cutting
semantics.

## 2. Bounded contexts and modules

| Context | Module and root package | Responsibility | Rules |
|---|---|---|---|
| Kernel | `loans-kernel`, `com.mlcpu.loans.kernel` | Money at a currency's decimal places, the calculation's precision and rounding, date arithmetic | MNY, DATE (*later*) |
| Calendar | `loans-calendar`, `com.mlcpu.loans.calendar` | The repayment periods of a loan: one per installment, with its start and due date | CAL (`digests/calendar.md`) |
| Rates | `loans-rates`, `com.mlcpu.loans.rates` | Interest rate factors and day counts | RATE (`digests/rates.md`) |
| Terms | `loans-terms`, `com.mlcpu.loans.terms` | The admissibility of loan terms, typed rejections, the down payment | TRM (`digests/terms.md`) |
| Schedule | `loans-schedule`, `com.mlcpu.loans.schedule` | The amortization schedule: disbursement, the equal installment, the last installment, lines and totals | AMZ, PLAN (`digests/schedule.md`) |
| Disclosure | `loans-disclosure`, `com.mlcpu.loans.disclosure` | The schedule document and the disclosure (new) | RPT, DISC |
| Migration | `loans-migration`, `com.mlcpu.loans.migration` | The legacy loan book: each legacy loan as the service's terms, and its reconciliation with the legacy schedule or reject | LEG (`digests/migration.md`) |
| Application | `loans-application`, `com.mlcpu.loans.application` | The entry points, the wiring of the contexts, and the servicing use cases over the ports | SRV, IDEM (`digests/servicing.md`) |

**Given:** `loans-api` (`com.mlcpu.loans.api`) holds everything the contexts share with the outside world: the use-case
interface `ScheduleService`, the request and its terms, the outcomes and typed rejections, the service provider interfaces
(`com.mlcpu.loans.api.spi`), the servicing use cases `LoanServicing` with their commands and outcomes
(`com.mlcpu.loans.api.servicing`), the ports through which servicing reaches the ERP (`com.mlcpu.loans.api.ports`), the
migration's use case `LegacyMigration` with its results (`com.mlcpu.loans.api.migration`) and the legacy records it reads
(`com.mlcpu.loans.api.legacy`, generated from the legacy record layouts with their decoders). It is fixed: implementations do
not change it.

**The SPIs are the seams between contexts.** `DueDates` (the calendar) and `RateFactors` (the rates) are implemented by their
contexts. The schedule context reaches the calendar and the rates only through them; the application passes the
implementations in. Each SPI has its own scenarios, so each context is judged on its own.

**Entry points (contract):** `com.mlcpu.loans.application.LoansApplication` has public static methods, one per thing the
scenarios call. Each slice adds the ones its scenarios use:

| Entry point | Returns | Slice |
|---|---|---|
| `public static DueDates dueDates()` | the calendar's `DueDates` | calendar |
| `public static RateFactors rateFactors()` | the rates' `RateFactors` | rates |
| `public static ScheduleService quotes()` | the quote: terms, schedule, document, disclosure | terms, schedule, disclosure |
| `public static LoanServicing create(LoansPorts ports)` | servicing over the ERP's ports: book, disburse, bill, collect | servicing |
| `public static LegacyMigration migration()` | the legacy loan book: terms and reconciliation | migration |

**Dependencies** (enforced, `RULES.md`):
* a context module depends only on `loans-api`, `loans-kernel` and the JDK. Contexts do not call each other;
* `loans-application` depends on `loans-api`, `loans-kernel` and the context modules.

## 3. Glossary

* **Loan terms:** a `ScheduleRequest`: the loan id, the schedule start, the currency, the principal, the disbursement date,
  the number of installments, their frequency and interval, the annual interest rate, the down payment, the day-count
  conventions, the fixed length, the interest method, the full-term tranche flag and the calculation context.
* **Schedule start:** the date the loan's term starts; the first repayment period starts on it.
* **Disbursement date:** the date the principal is paid out. It does not move the calendar (its day of the month matters for
  monthly due dates, CAL-07).
* **Installment:** one repayment. A loan has `installments` of them.
* **Repayment period** (`Period`): the stretch of time one installment covers, from its start ("from") to its due date.
* **Frequency and every:** installments fall every `every` units of the frequency (`DAYS`, `WEEKS`, `MONTHS`, `YEARS`).
* **Fixed length:** when set, the loan's term ends `fixedLength` units of the frequency after the schedule start, whatever the
  installments add up to.
* **Calculation:** the precision (significant digits) and the rounding mode of every intermediate result of a loan.

## 4. Cross-cutting semantics

* **Dates** are `java.time.LocalDate`: no times, no time zones. Month and year steps are calendar steps (`plusMonths`,
  `plusYears`): a day that does not exist in the target month becomes that month's last day.
* **Amounts** are `BigDecimal`, never `double` or `float`. Every division carries a `MathContext`, or a scale and a rounding
  mode.
* **The quote is pure:** `ScheduleService`, `DueDates` and `RateFactors` change nothing and read no clock.
* **Rejections are typed:** terms the service does not schedule give a `Rejected` outcome with a `ScheduleRejection`; an SPI
  that cannot answer for the terms returns its typed rejection (`RateRejected`). A rule violation is never an exception or a
  string. An exception means a bug.
* **Determinism:** the same request gives the same result, field for field (dates, amounts with their scale, order of lines).

## 5. The calculation, by context

* **Calendar** (`digests/calendar.md`): the repayment periods.
* **Rates** (`digests/rates.md`): the two rate factors of a stretch of a period.
* **Terms** (`digests/terms.md`): the terms not offered at all, checked before any calculation, and the down payment.
* **Schedule** (`digests/schedule.md`): the plan of periods and stretches, the disbursement, the installment, its settlement
  and evening-out, and the schedule's lines and totals.
* **Disclosure** (`digests/disclosure.md`): the schedule as a document (RPT), and the cost of credit of the schedule (DISC):
  amount financed, finance charge, total of payments and the annual percentage rate by the actuarial method of 12 CFR 1026
  Appendix J.

## 6. New features

* **The disclosure** (`digests/disclosure.md`, DISC): what credit costs, as Truth in Lending discloses it: the amount
  financed, the finance charge, the total of payments and the APR. The legacy system had none, so its expected values come from
  this service's reference implementation, checked by closed forms and by an independent implementation; the unit-period of a
  several-day interval is this service's extension of Appendix J.
* **The legacy loan book's migration** (`digests/migration.md`, LEG): each loan of the legacy book (its request, the run's
  calculation and currency, and the schedule or reject the legacy job wrote) as the service's terms, and its reconciliation: the
  service recomputes the loan, and every difference from the legacy result is either explained by a decision below, named, or a
  defect. The ERP side (storing the legacy schedule, booking, the reconciliation rows) is the plugin's.
* **Servicing** (`digests/servicing.md`, SRV and IDEM): a loan booked (its schedule stored and an account opened), disbursed
  (the payment, the account's withdrawal, the down payment's invoice), billed as its installments fall due, and collected as
  its invoices are paid, every command idempotent. The ERP's ledger limits what can be booked (decisions D-11, D-12 below). The
  expected values come from this service's reference implementation over in-memory ports, checked by invariants (a paid-off
  loan's account is 0; its invoices bill the schedule).

## 7. Decisions: where the service departs from the legacy system

Every other legacy rule is kept exactly.

| Id | The legacy system | The service |
|---|---|---|
| D-1 | Stops a loan without a business reason for terms it cannot handle (flat interest, full-term tranches, rounding multiples with no decimal places, a 100% down payment, a single installment after a late disbursement) | Typed rejections with business names (`ScheduleRejection`); the legacy code is kept for the migration |
| D-2 | A cached balance could go stale and reach the output when every installment but the last rounds to zero | Balances always reflect the final installments |
| D-3 | A disbursement outside every period gave an all-zero schedule | Rejected: `DISBURSEMENT_OUTSIDE_TERM` |
| D-4 | Some inputs had no effect (rounding multiples of installments, a custom days-in-year strategy, partial-period interest, interest recognition) | Not in the API |
| D-5 | Unknown frequency, day-count and method codes stopped the run | Unrepresentable: they are enums |
| D-6 | Capacity limits of the COBOL (precision up to 12 digits, principal below 10^9, at most 480 installments, limits on `every`) | The service's own limits (`digests/terms.md`): precision 1 to 34, principal below 10^15, at most 1,200 installments, `every` up to 9,999; `OUT_OF_RANGE` names the field |
| D-7 | Rejections carried the site where the calculation stopped | Not carried |
| D-8 | A printed report with pages and edited numbers | A structured schedule document (`digests/disclosure.md`, RPT) |
| D-9 | One calculation context per run | One `Calculation` per loan |
| D-10 | Requests had to arrive in ascending loan-id order | No order; booking again with the same external id returns the first booking (IDEM-01) |
| D-11 | Currencies with 0 to 4 decimal places were scheduled | Quoted and scheduled as before; booked only with at most 2 decimal places, in a currency the ERP's ledger can post for the organization (its base currency or one with a rate to it), and with every amount below 10^12 (`BookingRejection`, `digests/servicing.md` SRV-02) |
| D-12 | Rates with up to 9 decimal places | Quoted with up to 9; booked with at most 6 (the ledger stores 6) |

## 8. Out of scope

Repayments, prepayments, rate changes and re-ageing (wave 2); charges and fees (the schedule's fee and penalty columns are
zero); holidays and working-day adjustments (the legacy estate has none).
