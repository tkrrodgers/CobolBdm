# Rule digest: CBTRN02C (daily transaction posting) → context `accounts`, rules POST

**Legacy program:** CBTRN02C, job POSTTRAN. It reads the day's transactions (DALYTRAN) and validates each against the
card cross-reference (XREFFILE) and the account (ACCTFILE). It posts valid ones to the transaction file (TRANFILE), the
account and the category balance (TCATBALF). It writes invalid ones to the reject file (DALYREJS) with a reason. Return
code 4 when anything was rejected.

**In the service:**
* `postTransaction` posts one transaction in real time.
* `postBatch` posts the day in input order.

Both use the same rules. Legacy rule ids refer to the requirements packet `normalization/CBTRN02C/requirements.json`.

## Validation, in this order

| Rule | Statement | Legacy origin |
|---|---|---|
| POST-01 | A transaction whose id is already in the ledger is rejected with `DUPLICATE_TRANSACTION` (104). This is checked first, and no other rule runs. | New (SPEC D-8). The legacy's transaction file holds only the day (OPEN OUTPUT); a repeat within the day abends on the WRITE |
| POST-02 | The card is looked up by the transaction's card number. Unknown card → `INVALID_CARD` (100); the account is not looked up. | R-CBTRN02C-004, -005 |
| POST-03 | The account is the card's account. Not found → `ACCOUNT_NOT_FOUND` (101); no further check. | R-CBTRN02C-006, -007 |
| POST-04 | Over limit: the projected cycle balance is `currentCycleCredit - currentCycleDebit + amount`. When the credit limit is **less than** it, the reason is `OVERLIMIT` (102). A limit equal to the projected balance passes. | R-CBTRN02C-008, -009 |
| POST-05 | Expired: when the account's expiration date is **before** the date of the transaction's origin timestamp, the reason is `EXPIRED_ACCOUNT` (103). Posting on the expiration day passes. A transaction without an origin timestamp is never expired: the legacy compares blanks, which sort below every date. | R-CBTRN02C-010 |
| POST-06 | POST-04 and POST-05 are both evaluated. When both fail, the reason is `EXPIRED_ACCOUNT` (103): the legacy assigns the reasons in sequence and the later one wins. | Quirk Q-03 (kept as the business rule) |

## Posting a valid transaction

| Rule | Statement | Legacy origin |
|---|---|---|
| POST-07 | The category balance with key (account, type code, category code) gets the amount added. If there is none, it is created with the amount as its balance. | R-CBTRN02C-014, -015, -016 |
| POST-08 | The account's current balance gets the amount added. An amount ≥ 0 is added to `currentCycleCredit`, a negative amount to `currentCycleDebit`, so the debit total accumulates negative values. No other account field changes. | R-CBTRN02C-017 |
| POST-09 | The transaction is appended to the ledger with every field received, the account id, `processedTimestamp` = now, and no interest cycle. | R-CBTRN02C-011, -012, -019 |
| POST-10 | The outcome is `Posted(transactionId, accountId, newBalance)`, where `newBalance` is the account's current balance after POST-08. | - |

## Rejecting

| Rule | Statement | Legacy origin |
|---|---|---|
| POST-11 | A rejected transaction changes nothing but the rejection log. It gets one `RejectionRecord`: the transaction as received, the card's account id (null when the card is unknown; for 104, the card's account when the card is known), the reason, and `rejectedAt` = now. The outcome is `Rejected(transactionId, reason)`. | R-CBTRN02C-013 (the reject file: record plus reason trailer) |

## The batch

| Rule | Statement | Legacy origin |
|---|---|---|
| POST-12 | `postBatch` applies POST-01 to POST-11 to each transaction in input order. Each transaction sees the effects of the ones before it: cycle totals for POST-04, category balances, ledger ids for POST-01. | MAINLINE loop |
| POST-13 | `BatchPostingResult`: `processed` = number of transactions, `posted`, `rejected`, and the outcomes in input order. | R-CBTRN02C-001, -002, -003 (counts; the return code becomes the counts) |

## Numbers

* Amounts and balances have 2 decimals. Additions are exact at that scale.
* The legacy fields hold at most 9 integer digits for amounts and 10 for balances. Overflow is out of scope (SPEC.md
  section 7).

## Not carried forward

* Console lines ("TCATBAL record not found ... Creating.", the counts) and the reject file's bytes are legacy channels
  (SPEC.md section 9).
* The legacy abends on I/O errors and on an id repeated within the day. Duplicates are now POST-01, across days too.
* Quirks Q-01, Q-02 and Q-04 (stale record areas after a failed READ) leave no trace in any value the service returns.
