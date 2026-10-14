# Calendar pool (slice S2): verification and selection

Built by `CalendarPool` from `pool/calendar/candidates.jsonl` (`tools/build_calendar_candidates.py`).

| | |
| --- | --- |
| Candidates (distinct calendar inputs) | 9297 |
| of them from FNDG0100 CALL traces | 40 (every one also in oracle A's schedules, same periods) |
| Reference (`RepaymentCalendar`) equal on | 9297 of 9297 |
| Chosen | 296: 60 public, 236 hidden |

## Rule mutants (FE 7.2 step 3)

| Rule | Mutant | Candidates that kill it | Chosen scenarios tagged | of them public |
| --- | --- | --- | --- | --- |
| CAL-01 | the last period is left out | 9297 | 296 | 60 |
| CAL-02 | period 1 starts on the disbursement date | 1716 | 133 | 31 |
| CAL-03 | a DAYS step adds every + 1 days | 1961 | 48 | 10 |
| CAL-04 | a WEEKS step adds every days (not 7 x every) | 2046 | 48 | 10 |
| CAL-05 | a MONTHS step adds 30 x every days | 4250 | 103 | 20 |
| CAL-06 | a YEARS step adds 365 x every days | 687 | 48 | 10 |
| CAL-07 | no month-end day | 519 | 47 | 9 |
| CAL-08 | each due date steps from the schedule start (k x every units), not from the previous due date | 89 | 22 | 4 |
| CAL-09 | the fixed length is ignored | 993 | 122 | 20 |

A scenario's rule tags are the rules whose mutants it kills (FE 7.3 asks for tags by coverage; a killed mutant is the stronger form).

Chosen by frequency: {DAYS=48, WEEKS=48, MONTHS=136, YEARS=64}
