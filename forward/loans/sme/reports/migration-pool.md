# Migration pool (slice migration, S9 engine side): the reconciliation of the legacy book, and the selection

Built by `MigrationPool` from `sme/legacy-books.json` (every loan of every book, its records as the legacy datasets hold them) and SME probes.
Expected values: the reference (`loans-migration`) over `LoansApplication.quotes()`; cross-checked with the schedule pool's own
classification of the same loans (D-2 decisions, scheduled and rejected counts).

## The legacy book, reconciled (G10, engine level)

| Book | Outcomes |
| --- | --- |
| edge-p12-half_even | {EXPLAINED D-2=1, EXPLAINED D-3=45, LEGACY_REJECT=50, MATCH=1111} |
| rejects-p12-half_even | {EXPLAINED D-5=17, EXPLAINED D-6=7, LEGACY_REJECT=23, MATCH=4, undecodable (the plugin: D-5)=9} |
| sample-p03-ceiling | {EXPLAINED D-3=2, LEGACY_REJECT=10, MATCH=51} |
| sample-p03-down | {EXPLAINED D-3=2, LEGACY_REJECT=6, MATCH=55} |
| sample-p03-floor | {EXPLAINED D-2=1, EXPLAINED D-3=3, LEGACY_REJECT=4, MATCH=55} |
| sample-p03-half_down | {EXPLAINED D-2=1, EXPLAINED D-3=3, LEGACY_REJECT=2, MATCH=57} |
| sample-p03-half_even | {EXPLAINED D-3=2, LEGACY_REJECT=7, MATCH=54} |
| sample-p03-half_up | {EXPLAINED D-3=3, LEGACY_REJECT=2, MATCH=58} |
| sample-p03-up | {EXPLAINED D-3=2, LEGACY_REJECT=16, MATCH=45} |
| sample-p06-ceiling | {EXPLAINED D-3=2, LEGACY_REJECT=8, MATCH=53} |
| sample-p06-down | {EXPLAINED D-2=2, EXPLAINED D-3=2, LEGACY_REJECT=8, MATCH=51} |
| sample-p06-floor | {EXPLAINED D-2=2, EXPLAINED D-3=2, LEGACY_REJECT=12, MATCH=47} |
| sample-p06-half_down | {EXPLAINED D-3=3, LEGACY_REJECT=9, MATCH=51} |
| sample-p06-half_even | {EXPLAINED D-3=3, LEGACY_REJECT=8, MATCH=52} |
| sample-p06-half_up | {EXPLAINED D-3=2, LEGACY_REJECT=11, MATCH=50} |
| sample-p06-up | {EXPLAINED D-3=2, LEGACY_REJECT=11, MATCH=50} |
| sample-p09-ceiling | {EXPLAINED D-3=3, LEGACY_REJECT=8, MATCH=52} |
| sample-p09-down | {EXPLAINED D-2=2, EXPLAINED D-3=3, LEGACY_REJECT=8, MATCH=50} |
| sample-p09-floor | {EXPLAINED D-2=2, EXPLAINED D-3=3, LEGACY_REJECT=9, MATCH=49} |
| sample-p09-half_down | {EXPLAINED D-3=2, LEGACY_REJECT=9, MATCH=52} |
| sample-p09-half_even | {EXPLAINED D-2=1, EXPLAINED D-3=2, LEGACY_REJECT=8, MATCH=52} |
| sample-p09-half_up | {EXPLAINED D-3=2, LEGACY_REJECT=11, MATCH=50} |
| sample-p09-up | {EXPLAINED D-3=3, LEGACY_REJECT=8, MATCH=52} |
| sample-p12-ceiling | {EXPLAINED D-3=3, LEGACY_REJECT=8, MATCH=52} |
| sample-p12-down | {EXPLAINED D-2=2, EXPLAINED D-3=3, LEGACY_REJECT=10, MATCH=48} |
| sample-p12-floor | {EXPLAINED D-2=1, EXPLAINED D-3=2, LEGACY_REJECT=6, MATCH=54} |
| sample-p12-half_down | {EXPLAINED D-3=2, LEGACY_REJECT=10, MATCH=51} |
| sample-p12-half_even | {EXPLAINED D-3=2, LEGACY_REJECT=10, MATCH=51} |
| sample-p12-half_up | {EXPLAINED D-2=1, EXPLAINED D-3=2, LEGACY_REJECT=8, MATCH=52} |
| sample-p12-up | {EXPLAINED D-3=3, LEGACY_REJECT=10, MATCH=50} |
| **all** | {EXPLAINED D-2=16, EXPLAINED D-3=113, EXPLAINED D-5=17, EXPLAINED D-6=7, LEGACY_REJECT=310, MATCH=2559, undecodable (the plugin: D-5)=9} |

Requests the generated codec cannot decode (the plugin reports them; D-5):

- (the inverse mapping: 3004 requests back byte for byte, 1 but for D-4 inputs): 0
- rejects-p12-half_even: FN-LOAN-REQUEST RQ-ANNUAL-RATE: 1
- rejects-p12-half_even: FN-LOAN-REQUEST RQ-DISB-AMOUNT: 2
- rejects-p12-half_even: FN-LOAN-REQUEST RQ-DISB-DATE: 1
- rejects-p12-half_even: FN-LOAN-REQUEST RQ-DOWNPAY-ENABLED: 1
- rejects-p12-half_even: FN-LOAN-REQUEST RQ-FULL-TERM-TRANCHE: 1
- rejects-p12-half_even: FN-LOAN-REQUEST RQ-NUM-REPAYMENTS: 1
- rejects-p12-half_even: FN-LOAN-REQUEST RQ-REC-TYPE: 1
- rejects-p12-half_even: FN-LOAN-REQUEST RQ-SCHED-START-DATE: 1

## Rule mutants

| Rule | Mutant | Candidates that kill it | Chosen scenarios tagged | of them public |
| --- | --- | --- | --- | --- |
| MIG-01 | the disbursement date is the schedule start | 414 | 70 | 13 |
| MIG-02 | the currency's rounding multiple is taken without its indicator | 3006 | 225 | 47 |
| MIG-03 | 364 days in a year is read as 365 | 36 | 20 | 4 |
| MIG-04 | the down payment percentage is taken without its indicator | 2803 | 175 | 36 |
| MIG-05 | the run's precision is ignored (always 12) | 1323 | 99 | 20 |
| MIG-06 | the interest method is checked before the frequency | 1 | 1 | 0 |
| MIG-07 | an envelope reject does not match the service's OUT_OF_RANGE | 16 | 13 | 3 |
| MIG-08 | an envelope reject the service schedules is a DIFF, not D-6 | 8 | 8 | 2 |
| MIG-09 | a legacy schedule without a disbursement line is compared, not D-3 | 114 | 17 | 3 |
| MIG-10 | a loan the service rejects but the legacy scheduled is not reported with the service's rejection | 1 | 1 | 0 |
| MIG-11 | the lines are compared before the header and totals | 1 | 1 | 1 |
| MIG-12 | a difference in the disbursement line's balance is also explained by D-2 | 1 | 1 | 0 |
| MIG-13 | a difference reports the last differing field, not the first | 2 | 2 | 1 |

3069 candidates, 47 of them probes; 256 chosen, 51 public.

No problems.
