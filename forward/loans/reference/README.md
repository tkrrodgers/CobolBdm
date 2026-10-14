# The reference implementation (SME only)

The SME's implementation of the loans service (the forward-engineering design FE, section 6; Phase 0 step 6), in the modules of
the service (FE 5.1): the expected-value engine of the package. It must reproduce the schedule pool (S5) exactly, with the
defect fixes of the decisions D-n taken from oracle A′. No model ever sees this code (FE 4.1). The gate (FE 7.7) runs it
against every pool and the architecture rules A-1 to A-14, as `forward/cas` does with its reference.

| Module | Package | What |
| --- | --- | --- |
| `loans-kernel` | `com.mlcpu.loans.kernel` | `Money`: amounts at the currency's decimal places, the calculation's precision and rounding (MNY) |
| `loans-calendar` | `com.mlcpu.loans.calendar` | `RepaymentCalendar` implements the SPI `DueDates` (CAL) |
| `loans-rates` | `com.mlcpu.loans.rates` | `InterestFactors` implements the SPI `RateFactors` (RATE) |
| `loans-terms` | `com.mlcpu.loans.terms` | `TermsReview`: the terms not offered at all, in order, and the down payment (TRM) |
| `loans-schedule` | `com.mlcpu.loans.schedule` | `Amortization`: disbursement, installment, settlement of the last installment, deferred interest, evening-out, lines and totals (AMZ, PLAN); the calendar and the rates only through the SPIs |
| `loans-disclosure` | | document (RPT) and disclosure (DISC): Phase 0 step 9 |
| `loans-application` | `com.mlcpu.loans.application` | `LoansApplication.quotes()`: terms, then the schedule |

The schedule's state is immutable (rule A-4): a `Plan` of `Installment` periods made of `Stretch`es, and the `Figures` that
follow from them, computed afresh, period by period, for every new plan. Rejections found during the calculation travel as
values (`Step`), never as exceptions (rule A-9).

| Decision | What the reference does |
| --- | --- |
| D-1 | The cases where the legacy calculation stopped for lack of a tenant context are typed rejections (`ScheduleRejection`) at the same points of the calculation: the terms review for those checked first, the schedule and the rates for the others |
| D-2 | Every figure is computed afresh for every state of the plan, so the balance after an installment always reflects the final installments. The legacy cache could go stale when every installment but the last rounds to zero; the expected values of those loans come from oracle A′ D-2 |
| D-3 | A disbursement outside the term is the rejection `DISBURSEMENT_OUTSIDE_TERM` instead of an all-zero schedule |

## Build and run

```bash
gradle -p ../service --offline -PimplDir=../reference build          # the reference in the service's module graph (--release 17)
cd ../sme && gradle --offline -q installDist                          # the pool runner over the reference
build/install/loans-sme/bin/loans-sme pool/schedule 50 "" build/runs/diffs.txt   # 50 loans per scenario file
build/install/loans-sme/bin/loans-sme pool/schedule 0 "" build/runs/diffs.txt    # the whole pool
```

Arguments of the runner: the pool directory, loans per scenario file (0 = all), a file-name filter, the file for the
differences. It prints each scenario file's count of differing loans and a summary by kind.
