# Rule digest: SAM2 (customer field update policy and 12-month average) → context `customers`, rules UPD and AVG

**Legacy program:** SAM2, CALLed by SAM1 for each UPDATE with the current customer record and the change. It validates
the change, then replaces or adds to one field: NAME, BALANCE or ORDERS. It returns an ok flag and a message. Its CRUNCH
path computes the average of the 12 monthly amounts. Legacy rule ids refer to `normalization/SAM2/requirements.json`.

**In the service:**
* The update policy is applied by CUST-07 (`SAM1.md`).
* The 12-month average is used by the customer statement (SPEC.md STMT-03).

## Reading an update amount (BALANCE and ORDERS)

The value is read as the legacy 36-character update field: `value` padded on the right with blanks to 36 characters.

| Rule | Statement | Legacy origin |
|---|---|---|
| UPD-01 | Characters 8 and 9 (1-based) are the cents. If they are not both digits, they count as `00`. | R-SAM2-002 (TRAN-UPDATE-NUM-HH NOT NUMERIC → 0) |
| UPD-02 | Character 1 may be a sign. A `-` makes the amount negative. A `-` or `+` counts as the digit `0`. | R-SAM2-002 (TRAN-UPDATE-NUMX1) |
| UPD-03 | Characters 1 to 9, after UPD-01 and UPD-02, must all be digits. Otherwise the change is rejected with `NOT_NUMERIC`. They are read as a number with **two implied decimals**, for ORDERS too: `000000250` is 2.50 and `000012345` is 123.45. | R-SAM2-002 (TRAN-UPDATE-NUM PIC 9(7)V99) |

Only the first 9 characters are read, so a sign takes the place of the first digit:
* `000012345` is 123.45;
* `-00001050` is -10.50;
* `-000001050` (10 characters) reads `-00000105`, which is -1.05;
* `+000000099` reads `+00000009`, which is 0.09;
* `+1500` is not numeric (characters 6 and 7 are blanks);
* `0000123AB` reads cents `00`, which is 123.00;
* `ABCDEFGHI` is not numeric.

## Validation, in this order

| Rule | Statement | Legacy origin |
|---|---|---|
| UPD-04 | The amount is read (UPD-01 to UPD-03) only when the field name is `BALANCE` or `ORDERS`, whatever the action. | R-SAM2-002 |
| UPD-05 | The action must be `REPLACE` or `ADD`, else `INVALID_ACTION`. This message wins over `NOT_NUMERIC`: the legacy validates the amount first, then the action, and the later message wins. | R-SAM2-003 |
| UPD-06 | Only when the amount and the action are valid is the field applied. Field names other than `NAME`, `BALANCE` and `ORDERS` are rejected with `INVALID_FIELD`. `BALANCE` and `ORDERS` on a contact record are also rejected with `INVALID_FIELD`. | R-SAM2-004 (EVALUATE TRAN-FIELD-NAME ... WHEN OTHER); SPEC D-5 for contacts |

## Applying the change

| Rule | Statement | Legacy origin |
|---|---|---|
| UPD-07 | NAME: the name becomes the first 17 characters of `value`, without trailing blanks. Both `REPLACE` and `ADD` replace the name. It applies to profiles and contacts alike. | R-SAM2-004 (MOVE TRAN-UPDATE-DATA TO CUST-NAME, PIC X(17)) |
| UPD-08 | BALANCE (profile only): `REPLACE` sets `accountBalance` to the amount. `ADD` sets it to `accountBalance + amount`. The field is unsigned with 7 integer digits, so the stored value is the result's **absolute value**, with integer digits above the 7th dropped. | R-SAM2-004 (MOVE / COMPUTE into PIC 9(7)V99) |
| UPD-09 | ORDERS (profile only): `REPLACE` sets `ordersYtd` from the amount. `ADD` sets it from `ordersYtd + amount`. The field is an unsigned 5-digit integer, so the stored value is the result's absolute value, **truncated** to a whole number, with digits above the 5th dropped. Adding `000000010` (0.10) to 44 gives 44; adding `-00000250` (-2.50) to 10 gives 7; replacing with `-00000007` (-0.07) gives 0. | R-SAM2-004 (MOVE / COMPUTE into PIC 9(5)) |

## The 12-month average

| Rule | Statement | Legacy origin |
|---|---|---|
| AVG-01 | The 12-month average of a profile is `floor(sum / 12)`, where `sum` adds the **whole part** of each of the 12 monthly amounts. The legacy sum is an integer field, so each addition drops the cents. The result is a `BigDecimal` with scale 0. | R-SAM2-005, -006, -007 (310-CRUNCH-LOOP: WORK-SUM PIC 9(16), MONTH-AVERAGE PIC 9(16)) |

## Not carried forward

* The CRUNCH loop count: the average is computed once, on demand.
* The `REQUEST TYPE IS INVALID` path (R-SAM2-001): SAM1 only calls SAM2 for UPDATE, so it cannot occur.
* Quirk Q-01 (by-reference writes: SAM2 overwrote the sign character in the caller's change record, which SAM1 then
  printed): it affects only the legacy report's bytes.
