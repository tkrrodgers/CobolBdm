# Terms pool (slice S4): verification and selection

Built by `TermsPool` from the schedule pool's terms rejections, the legacy envelope's rejections (`tools/build_terms_candidates.py`) and SME probes (decision D-6).

| | |
| --- | --- |
| Distinct candidates from schedule pool (C) | 1017 |
| Distinct candidates from legacy envelope (C), service limit D-6 | 47 |
| Distinct candidates from SME probe | 36 |
| Legacy envelope rejects the service accepts (D-6: wider limits; their schedules are privileged, slice S5) | 6 |
| Reference (`TermsReview`) equal on | all |
| Chosen | 62: 11 public |

## Rule mutants

| Rule | Mutant | Candidates that kill it | Chosen scenarios tagged | of them public |
| --- | --- | --- | --- | --- |
| TRM-01 | precision up to 35 is accepted | 1 | 1 | 0 |
| TRM-02 | rounding mode UNNECESSARY is accepted | 2 | 2 | 0 |
| TRM-03 | 1,201 installments are accepted | 1 | 1 | 0 |
| TRM-04 | a principal of exactly 10^15 is accepted | 1 | 1 | 0 |
| TRM-05 | 5 decimal places are accepted | 36 | 6 | 1 |
| TRM-06 | rates up to 101% are accepted | 3 | 3 | 1 |
| TRM-07 | every up to 10,000 is accepted | 1 | 1 | 0 |
| TRM-08 | dates up to 2200-12-31 are accepted | 4 | 4 | 1 |
| TRM-09 | down payments up to 101% are accepted | 4 | 4 | 1 |
| TRM-10 | a fixed length up to (n + 2) x every is accepted | 2 | 2 | 0 |
| TRM-11 | no rounding-multiple check | 423 | 6 | 1 |
| TRM-12 | no check of a down payment without a percentage | 597 | 6 | 1 |
| TRM-13 | the principal is checked before the installments | 1 | 1 | 0 |

TRM-14 (the down payment's amount) is observable only in a schedule: the schedule slice's scenarios verify it.
