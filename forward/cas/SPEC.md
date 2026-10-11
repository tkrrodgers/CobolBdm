# Customer Account Service: specification

**Owner:** the SME. **Status:** frozen for the forward-engineering evaluation (`tools/eval/PROTOCOL-forward.md`).

## 1. Purpose

The Customer Account Service (CAS) replaces five COBOL batch programs from two unrelated estates with one Java 21 service:

* **CardDemo:**
  * CBTRN02C posts the day's card transactions;
  * CBACT04C charges monthly interest;
  * CBTRN03C reports transactions.
* **The IBM sample:**
  * SAM1 maintains a customer master file;
  * SAM2 applies field updates to it.

It also adds a feature no legacy program has: the **customer statement**, which brings both estates together for one
customer and one cycle.

The service keeps the legacy business rules exactly, except where section 8 lists a deliberate change. The rule digests
(`digests/*.md`) state every rule with its legacy origin. This document gives the service's shape and its cross-cutting
semantics.

## 2. Bounded contexts and modules

| Context | Module and root package | Responsibility | Rules |
|---|---|---|---|
| Card accounts | `cas-accounts`, `com.mlcpu.cas.accounts` | Posting card transactions (real time and nightly batch), validating them, category balances, interest accrual and the cycle close | POST, INT (`digests/CBTRN02C.md`, `digests/CBACT04C.md`) |
| Customers | `cas-customers`, `com.mlcpu.cas.customers` | Customer master maintenance (add, delete, update), the field update policy, the 12-month average | CUST, UPD, AVG (`digests/SAM1.md`, `digests/SAM2.md`) |
| Reporting | `cas-reporting`, `com.mlcpu.cas.reporting` | The transaction report query | RPT (`digests/CBTRN03C.md`) |
| Statements | `cas-statements`, `com.mlcpu.cas.statements` | The customer statement (new) | STMT (section 6) |
| Application | `cas-application`, `com.mlcpu.cas.application` | Use-case orchestration, idempotency, wiring | IDEM (section 5) |

**Given:** `cas-api` (`com.mlcpu.cas.api`) holds everything the contexts share with the outside world: the use-case
interface `CustomerAccountService`, commands, queries, results, typed rejections, the persistence snapshots and the
outbound ports (`CasPorts`). It is fixed: implementations do not change it.

**Entry point (contract):** `com.mlcpu.cas.application.CasApplication` has
`public static CustomerAccountService create(CasPorts ports)`. Every test reaches the service through it.

**Dependencies** (enforced, `RULES.md`):
* a context module depends only on `cas-api` and the JDK. Contexts do not call each other;
* `cas-application` depends on `cas-api` and the context modules.

A context reads what it needs through the ports in `CasPorts`. For example, the statement reads accounts, the ledger and the
customer master through their ports, not through another context's classes.

## 3. Glossary

* **Account:** a card account (CardDemo). Its balance and cycle credit/debit are money with 2 decimals, at most 10 integer
  digits. It has a credit limit, an expiration date, and the disclosure group whose interest rates apply to it.
* **Card:** a card number that belongs to one account and one CardDemo customer (the card cross-reference). An account can
  have several cards. "The account's card" means its card with the lowest card number.
* **Transaction:** a card transaction as received: id (16 characters), type code (2), category code (number), source,
  description, amount (2 decimals, at most 9 integer digits), merchant data, card number and origin timestamp.
* **Ledger:** every transaction the service has posted or generated, with its account and its processed timestamp.
  Interest transactions also carry the cycle they were charged for.
* **Rejection log:** every transaction the service refused, with the reason and when.
* **Category balance:** the running balance of one account for one transaction type and category.
* **Disclosure group:** a set of annual interest rates (percent) per transaction type and category. `DEFAULT` is the
  fallback group.
* **Cycle:** a calendar month (`java.time.YearMonth`). A posting belongs to the cycle of its processed timestamp's date.
  An interest charge belongs to the cycle it was accrued for.
* **Customer master:** the SAM customer file. Records are keyed by a 6-character key: the 5-character customer id plus a
  1-character record type.
  * Type `A` is the customer's **profile**: name, account balance, orders year to date, 12 monthly amounts, address and
    notes.
  * Any other type is a **contact** of that customer: name, description, contact info.
* **Customer link:** reference data that maps a SAM customer id to its CardDemo account ids. It is the only connection
  between the estates.
* **Text values:** stored without trailing blanks; a blank legacy field is `""`.

## 4. Use cases

All use cases are methods of `CustomerAccountService`. The time is `LocalDateTime.now(ports.clock())`.

| Method | Kind | Summary |
|---|---|---|
| `postTransaction(PostTransaction)` | command | Validate and post one transaction in real time; returns `Posted` or `Rejected` |
| `postBatch(PostBatch)` | command | The nightly batch: the same rules for each transaction in order; returns the outcomes and counts |
| `accrueInterest(AccrueInterest)` | command | Monthly interest for one cycle, and the cycle close of every account with category balances |
| `applyCustomerChanges(ApplyCustomerChanges)` | command | A batch of customer master changes, in order; returns the totals and errors |
| `transactionReport(TransactionReportQuery)` | query | Ledger transactions processed in a date range, grouped by card, with totals |
| `customerStatement(StatementQuery)` | query | The customer statement (section 6) |

**Failures:**
* A rule violation in a transaction or a customer change is an **outcome**, not an exception: `Rejected` with a
  `RejectionReason`, or a `CustomerChangeError` with a `ChangeRejection`.
* Missing reference data that a use case cannot do without throws `ReferenceDataMissing`:
  * an account or card that interest needs;
  * a disclosure group;
  * a report's card, type or category;
  * a statement's customer, link or account.
* `accrueInterest` is all-or-nothing: when it throws, it has changed nothing.

## 5. Idempotency (IDEM)

* **IDEM-01:** Every command carries an `IdempotencyKey`. When a command arrives with a key the service has already
  completed (`CommandJournal`), it changes nothing and returns the result recorded for that key.
* **IDEM-02:** A key is recorded only after its command has completed. A command that threw is not recorded.
* **IDEM-03:** Interest is also idempotent per account and cycle, whatever the key:
  * an account whose `lastClosedCycle` is the requested cycle or later is skipped;
  * a skipped account gets no charge and no change, and is listed in `skippedAccounts`.
* **IDEM-04:** A transaction whose id is already in the ledger is rejected with `DUPLICATE_TRANSACTION` (code 104), whatever
  the key.

Queries have no key and change nothing.

## 6. The customer statement (STMT)

`customerStatement(new StatementQuery(customerId, cycle))` returns a `CustomerStatement` for one SAM customer and one cycle.

* **STMT-01:** The customer is the master record with key `customerId + "A"`. If it does not exist, the query throws
  `ReferenceDataMissing("customer", customerId)`.
* **STMT-02:** The customer's accounts are those of its customer link, in the link's order.
  * no link → `ReferenceDataMissing("customer link", customerId)`;
  * a linked account that does not exist → `ReferenceDataMissing("account", <11-digit account id>)`.
* **STMT-03:** `name` and `ordersYtd` come from the profile. `monthlyAverage` is the profile's 12-month average (AVG-01,
  `digests/SAM2.md`).
* **STMT-04:** Per linked account, the cycle's **postings** are the account's ledger entries without an interest cycle whose
  processed date falls in the cycle.
* **STMT-05:** `activity` groups the cycle's postings by type code, then category code, both ascending. Each group has its
  number of postings and the sum of their amounts. Only groups with postings appear.
* **STMT-06:** `interest` is the sum of the account's ledger entries whose interest cycle is the cycle (0.00 when none).
* **STMT-07:** `closingBalance` is the account's current balance minus the sum of its ledger entries from later cycles:
  * postings processed after the cycle's last day;
  * interest charged for a later cycle.
* **STMT-08:** `openingBalance` is `closingBalance` minus the cycle's postings minus the cycle's interest.
* **STMT-09:** `rejections` are the rejection log entries with this account id whose `rejectedAt` falls in the cycle. They
  are ordered by `rejectedAt`, then transaction id, each with its transaction id, reason, amount and time.
* **STMT-10:** Amounts keep 2 decimals. Sums are exact: no rounding, no truncation.

## 7. Data and number rules

* Money is `BigDecimal` with the legacy field's scale:
  * 2 decimals for amounts and balances;
  * 2 decimals for interest rates (annual percent);
  * scale 0 for the 12-month average.
* Where a legacy computation truncates (COBOL without `ROUNDED`), the service truncates toward zero at the same point. The
  digests say where.
* Results compare by value and scale: `12.30` is not `12.3`.
* Amounts fit their legacy fields: transaction amounts below 10^9, account balances below 10^10. Overflow behaviour is not
  specified.
* Lists in results are in the order the digests give. Lists in snapshots returned by the ports are in key order.

## 8. Decisions: legacy behaviour not carried forward

Every other legacy rule is kept, including those that look odd (for example: an expired-account rejection wins over an
over-limit rejection; SAM update amounts have two implied decimals, also for orders).

| Id | Legacy behaviour (origin) | The service | Why |
|---|---|---|---|
| D-1 | CBACT04C never updates the **last** account of the run: its interest transactions are written, but its balance and cycle totals are not (packet quirk Q-01, an unreachable end-of-file branch) | Every account with category balances is closed: balance plus its interest, cycle credit and debit reset to 0.00 | Lost interest is a defect |
| D-2 | CBTRN03C adds the last transaction's amount to the totals a second time and never writes the last card's account total (quirk Q-02) | Every card group has its total; the grand total is the sum of the lines | Wrong totals are a defect |
| D-3 | CBTRN03C ends the report at the first transaction processed outside the period (quirk Q-01) | The report holds every ledger transaction processed in the period; entries without a processed timestamp are left out | The query filters; it does not stop |
| D-4 | SAM1 lets a comment line (code starting with `*`) set the "previous key" of the sequence check, so the change after a comment can be rejected as out of sequence | Comment lines are ignored entirely: no count, no sequence effect, no error | A comment is not a change |
| D-5 | SAM2 writes a BALANCE or ORDERS update of a **contact** record into the contact's description and info bytes | Such an update is rejected with `INVALID_FIELD` | A contact has no balance or orders |
| D-6 | SAM1 adds a contact record by copying a blank profile's bytes (digits appear in the description) | An added contact has all text fields `""` | Garbage data is a defect |
| D-7 | SAM1's sequential merge cannot change or delete a record that was added earlier in the **same** batch (NO MATCHING KEY) | Each change sees the effect of every earlier change of the batch | A limit of the file technique, not a business rule |
| D-8 | CBTRN02C writes each day's transaction file anew (`OPEN OUTPUT`): an id posted on an earlier day is posted again, and an id repeated within the day abends the run on the duplicate WRITE, after its account and category balance were already updated | The ledger keeps every day. A transaction whose id is already in it is rejected with `DUPLICATE_TRANSACTION` (104), checked before any other rule | A transaction must post once, and one bad transaction must not stop the service |
| D-9 | CBACT04C abends on a missing account, cross-reference or default disclosure group, after earlier accounts were already updated | `ReferenceDataMissing`, and nothing changes | All-or-nothing |
| D-10 | Interest transaction ids are the job's PARM date plus a run sequence | `"IN" + cycle (yyyyMM) + 8-digit sequence`, continuing after the interest entries already in the ledger for that cycle | The service has no PARM |
| D-11 | SAM1 zeroes the 12 monthly amounts of an added profile with `PERFORM ... WITH TEST AFTER`, which runs a 13th time and writes `000000000` into the first 9 characters of the occupation (packet quirk Q-01) | An added profile's occupation is `""` | Garbage data is a defect |

## 9. Non-goals in this phase

* Legacy channels: datasets, report bytes and console lines. These go to adapters in phase 3.
* Report pages and page totals.
* REST, batch scheduling, persistence technology, Spring.
* Overflow of amounts beyond their legacy fields.
* Concurrency: the service is called by one thread at a time.
* Reusing an idempotency key for a different command.
