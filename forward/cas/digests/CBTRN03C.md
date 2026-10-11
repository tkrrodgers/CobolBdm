# Rule digest: CBTRN03C (transaction detail report) → context `reporting`, rules RPT

**Legacy program:** CBTRN03C, job TRANREPT. The job unloads the transaction file and sorts it by card number. The program
reads it with a date range (DATEPARM). It prints each transaction with its account, type and category descriptions,
grouped by card with account totals, on 20-line pages with page totals, and ends with a grand total.

**In the service:** `transactionReport(new TransactionReportQuery(startDate, endDate))` over the ledger. Pages, headings
and page totals are presentation (SPEC.md section 9). Legacy rule ids refer to `normalization/CBTRN03C/requirements.json`.

| Rule | Statement | Legacy origin |
|---|---|---|
| RPT-01 | The report holds every ledger entry whose processed date (the date of `processedTimestamp`) is between `startDate` and `endDate`, both inclusive. Entries without a processed timestamp are left out. Interest transactions are entries like any other. | MAINLINE date test on TRAN-PROC-TS(1:10); SPEC D-3 fixes quirk Q-01 (the legacy stops at the first transaction outside the period) |
| RPT-02 | Order: card number ascending, then transaction id ascending. | The job's SORT by card number over the transaction file (id order) |
| RPT-03 | Lines with the same card number form a `CardGroup`. The group's `accountId` is the card's account from the card store. An unknown card throws `ReferenceDataMissing("card", cardNumber)`. | R-CBTRN03C-001 (1500-A-LOOKUP-XREF; legacy abends) |
| RPT-04 | Each line's type description comes from the transaction types. An unknown type throws `ReferenceDataMissing("transaction type", typeCode)`. | R-CBTRN03C-002 (1500-B-LOOKUP-TRANTYPE; legacy abends) |
| RPT-05 | Each line's category description comes from the transaction categories by (type code, category code). An unknown category throws `ReferenceDataMissing("transaction category", typeCode + 4-digit category code)`. | R-CBTRN03C-003 (1500-C-LOOKUP-TRANCATG; legacy abends) |
| RPT-06 | A `ReportLine` has: transaction id, the group's account id, type code and description, category code and description, source, amount. | R-CBTRN03C-019 (1120-WRITE-DETAIL) |
| RPT-07 | Each group's `accountTotal` is the sum of its lines' amounts. Every group has one, the last included. | R-CBTRN03C-004, -006, -010, -011; SPEC D-2 fixes quirk Q-02 (the last group's total was never written) |
| RPT-08 | `grandTotal` is the sum of all lines' amounts: each amount counted once. 0.00 when the report is empty. | R-CBTRN03C-007, -008, -013; SPEC D-2 fixes quirk Q-02 (the last amount was added twice) |
| RPT-09 | The report echoes `startDate` and `endDate`. A period with no entries gives no groups and a grand total of 0.00. | R-CBTRN03C-005 |

## Numbers

* Amounts and totals have 2 decimals.
* Sums are exact (the legacy totals have 9 integer digits; overflow is out of scope).

## Not carried forward

* Page layout, headings, page totals and the 20-line page (R-CBTRN03C-007 to -009, -014 to -018, -020): these belong to
  the legacy report adapter (phase 3).
* Quirks Q-03 to Q-05 (stale record areas after a failed READ): the service throws instead (RPT-03 to RPT-05).
