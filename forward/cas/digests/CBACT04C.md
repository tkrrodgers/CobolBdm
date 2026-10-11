# Rule digest: CBACT04C (monthly interest) → context `accounts`, rules INT

**Legacy program:** CBACT04C, job INTCALC. It reads the category balances (TCATBALF) in key order. For each balance it
finds the account's disclosure group rate (DISCGRP, falling back to the DEFAULT group), computes the monthly interest and
writes an interest transaction (TRANSACT). When the account changes, it adds the previous account's total interest to
its balance and resets its cycle totals (ACCTDATA).

**In the service:** `accrueInterest(new AccrueInterest(key, cycle))`. Legacy rule ids refer to the requirements packet
`normalization/CBACT04C/requirements.json`.

| Rule | Statement | Legacy origin |
|---|---|---|
| INT-01 | The run processes every category balance in key order: account id, type code, category code. An account's balances form one group. | MAINLINE (sequential read of TCATBALF), R-CBACT04C-001, -002 |
| INT-02 | An account whose `lastClosedCycle` is the requested cycle or later is skipped entirely: no charge, no change. It is listed in `skippedAccounts`, ascending. | New (SPEC IDEM-03) |
| INT-03 | Each group's account must exist. Otherwise the run throws `ReferenceDataMissing("account", <11-digit zero-padded account id>)`. | R-CBACT04C-002 (1100-GET-ACCT-DATA; legacy abends) |
| INT-04 | Each group's account must have a card: the account's card with the lowest card number goes on its interest transactions. Otherwise the run throws `ReferenceDataMissing("card", <11-digit account id>)`. | R-CBACT04C-003 (1110-GET-XREF-DATA, the alternate-key read; legacy abends) |
| INT-05 | The rate for a balance is the disclosure rate of (account's `groupId`, type code, category code). If there is none, the rate of (`DEFAULT`, type code, category code) is used. If that is missing too, the run throws `ReferenceDataMissing("disclosure group", "DEFAULT" + typeCode + 4-digit category code)`. | R-CBACT04C-004, 1200-GET-INTEREST-RATE, 1200-A-GET-DEFAULT-INT-RATE |
| INT-06 | A balance whose rate is zero is not charged: no interest transaction, nothing added. Its account is still closed (INT-09). | 1300-COMPUTE-INTEREST is performed only when the rate is not zero |
| INT-07 | Monthly interest = `balance × annualRatePercent / 1200`, **truncated** toward zero to 2 decimals. A non-zero rate always produces a charge, even when the interest is 0.00 or negative (negative balances). | R-CBACT04C-006 (COMPUTE without ROUNDED) |
| INT-08 | Each charge is appended to the ledger as an interest transaction:<br>• id per INT-11, type `01`, category 5, source `System`<br>• description `Int. for a/c ` followed by the 11-digit zero-padded account id<br>• amount = the interest; card per INT-04<br>• merchant id 0; merchant name, city and zip `""`<br>• origin and processed timestamps = now<br>• account id; `interestCycle` = the requested cycle<br>The result lists an `InterestCharge` for it. | R-CBACT04C-007, -008, -009 (1300-B-WRITE-TX) |
| INT-09 | Each processed (not skipped) account is closed:<br>• `currentBalance` += the sum of its charges<br>• `currentCycleCredit` and `currentCycleDebit` = 0.00<br>• `lastClosedCycle` = the cycle<br>This applies to **every** processed account, the last one included. The result lists an `AccountInterest` (total interest, new balance) in processing order. | R-CBACT04C-005 (1050-UPDATE-ACCOUNT); SPEC D-1 fixes quirk Q-01 |
| INT-10 | All-or-nothing: when INT-03, INT-04 or INT-05 throws, the run has changed nothing (no ledger entries, no account changes). | SPEC D-9; legacy abends after updating earlier accounts |
| INT-11 | Interest transaction ids are `"IN"` + the cycle as `yyyyMM` + an 8-digit sequence. The run's first id continues after the number of interest entries already in the ledger for that cycle, so the first run of a cycle starts at `00000001`. | SPEC D-10; R-CBACT04C-007 (legacy: PARM date + 6-digit suffix) |
| INT-12 | `InterestRunResult(cycle, charges, accounts, skippedAccounts)`. Charges and accounts are in processing order. | - |

## Numbers

* Rates have 2 decimals (annual percent).
* Interest is truncated at 2 decimals (INT-07). The account's total interest is the exact sum of its charges.
* Balances have 2 decimals.

## Not carried forward

* The PARM date (replaced by the cycle).
* The console echo of each category balance record and the "DISCLOSURE GROUP RECORD MISSING / TRY WITH DEFAULT GROUP
  CODE" lines (legacy channels).
* Quirks Q-02 to Q-04 (stale record areas after a failed READ): the service throws instead (INT-03 to INT-05).
* Quirks Q-05 and Q-06 (unpadded bytes in the id and description): ids and descriptions are exact text.
