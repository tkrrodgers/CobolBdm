# Rule digest: SAM1 (customer master update) → context `customers`, rules CUST

**Legacy program:** SAM1, job ZDERUN. It applies a change file (TRANFILE: UPDATE, ADD and DELETE requests and comment
lines) to the sequential customer master (CUSTFILE → CUSTOUT). It CALLs SAM2 for each UPDATE. It prints a report of
processed changes, errors and totals per change type (CUSTRPT).

**In the service:** `applyCustomerChanges(new ApplyCustomerChanges(key, changes))` over the customer master port. Legacy
rule ids refer to `normalization/SAM1/requirements.json`.

| Rule | Statement | Legacy origin |
|---|---|---|
| CUST-01 | Changes are applied in batch order. Each change sees the effects of every earlier change of the batch, including records added earlier in it. | 100-PROCESS-TRANSACTIONS loop; SPEC D-7 (the legacy merge could not see records added in the same run) |
| CUST-02 | A change whose code starts with `*` is a comment. It is ignored entirely: not counted, not checked, no error, and no effect on the sequence check. | R-SAM1-004 (`TRAN-COMMENT = '*'`); SPEC D-4 (the legacy let a comment's text set the previous key) |
| CUST-03 | Sequence check: a change whose `customerKey` is **lower** (string order) than the previous non-comment change's key is rejected with `OUT_OF_SEQUENCE`. It does not count in the type totals. The previous key becomes this change's key, whether it was applied or rejected. The first change has no previous key. | R-SAM1-004, -006 |
| CUST-04 | The code decides the change type: `UPDATE`, `ADD` or `DELETE`. Any other code is rejected with `INVALID_CODE` and does not count in the type totals. | R-SAM1-005 (EVALUATE TRAN-CODE ... WHEN OTHER) |
| CUST-05 | ADD: if the key exists → `DUPLICATE_KEY`. Otherwise a new record is saved:<br>• type `A`: a `CustomerProfile` with name and every other text `""`, `accountBalance` 0.00, `ordersYtd` 0, 12 monthly amounts of 0.00;<br>• any other type: a `CustomerContact` with every text `""`. | R-SAM1-011 to -015; SPEC D-6 for contacts, D-11 for the profile's occupation (quirk Q-01) |
| CUST-06 | DELETE: if the key does not exist → `NO_MATCHING_KEY`. Otherwise the record is deleted. | R-SAM1-016 to -018 |
| CUST-07 | UPDATE: if the key does not exist → `NO_MATCHING_KEY`. Otherwise the update policy applies to the record (rules UPD, `SAM2.md`). If the policy rejects it, that rejection is the error and the record is unchanged. | R-SAM1-007 to -010 |
| CUST-08 | Totals per change type, always `ADD`, `DELETE`, `UPDATE` in that order. `requested` counts the changes of that code that passed CUST-03; `processed` counts those applied; `inError` = requested − processed. | R-SAM1-034, -035, -036 (850-REPORT-TRAN-STATS) |
| CUST-09 | Every rejected change gives a `CustomerChangeError(index, customerKey, reason)`, in batch order. `index` is the change's position in the batch, from 0, counting comment lines. | 299-REPORT-BAD-TRAN, R-SAM1-019, -020 |
| CUST-10 | `changesRead` is the number of non-comment changes. | R-SAM1-003 (NUM-TRAN-RECS counts comment lines too; the service does not, D-4) |

## Not carried forward

* The report's bytes:
  * the header with date and time;
  * the "Transaction processed:" echo lines;
  * the scale lines under each error;
  * the console start and stop lines.

  These are legacy channels.
* The open and read status handling (R-SAM1-021 to -029, return code 16): storage errors are the adapter's concern.
* The CRUNCH change code is not a SAM1 change type: it is rejected as `INVALID_CODE` (CUST-04), as in the legacy.
* Quirk Q-01 (`PERFORM ... WITH TEST AFTER` overrun when an added profile's months are zeroed): the 13th pass wrote
  `000000000` into the occupation. The service's added profile has an empty occupation (SPEC D-11).
