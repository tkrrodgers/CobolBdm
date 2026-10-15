# Rates pool (slice S3): verification and selection

Built by `RatesPool` from FNEM0300's CALL traces (`tools/build_rates_candidates.py`, `tools/trace_rates.py`).

| | |
| --- | --- |
| Candidate lines read | 23869 |
| Distinct queries | 23324 (rejections: 66) |
| Reference (`InterestFactors`) equal on | all |
| Chosen | 468: 92 public, 376 hidden |

## Rule mutants (FE 7.2 step 3)

| Rule | Mutant | Candidates that kill it | Chosen scenarios that kill it | of them public |
| --- | --- | --- | --- | --- |
| RATE-01 | the annual rate is divided by 1000 | 21463 | 353 | 69 |
| RATE-02 | actual days in the year are always 365 | 1808 | 46 | 9 |
| RATE-03 | the interest factor ends at the stretch's end, not the period's due date | 4414 | 182 | 35 |
| RATE-04 | no year crossing: the year fractions are never used | 2012 | 54 | 11 |
| RATE-05 | actual days in the month: the period's days in place of the stretch's | 2528 | 131 | 26 |
| RATE-05-B | actual days in the month: the stretch's days exact, not first rounded to the precision | 170 | 6 | 1 |
| RATE-06 | 30-day months: a month counts 31 days | 8864 | 49 | 8 |
| RATE-07 | 30-day months, interest factor: every in place of the period's length in intervals | 457 | 30 | 6 |
| RATE-08 | the period's length in intervals: whole intervals only | 383 | 13 | 3 |
| RATE-09 | factors keep the arithmetic's result, not the precision's decimal places | 21522 | 256 | 49 |
| RATE-10 | 30-day months with yearly installments: no rejection (a year counts 365 days) | 66 | 66 | 13 |
| RATE-11 | the seed is always the period's start | 280 | 11 | 2 |
