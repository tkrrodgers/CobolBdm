# Worked examples: CBTRN02C (traced from the faithful program)

One run of the faithful translation of CBTRN02C on a small input built for illustration: nine daily transactions
from the CardDemo day, each edited to show one behaviour, processed in this order. This input is not one of the
acceptance scenarios. Before the run: the nine accounts got credit limit 50000.00 and expiration 2099-12-31 unless an
example says otherwise; every category-balance record (TRAN-CAT-BAL-RECORD) got its own FILLER letter (record 0
'a' x 22, record 1 'b' x 22, ...) so you can see which record's bytes end up where; the transaction file starts
empty; the batch clock is 2022-07-05T10:11:12-07:00; the storage default byte (ctx.storageDefault()) is x'00'.

Raw bytes are shown between « and » (ISO-8859-1; \x00 is byte 0). Signed numeric fields are zoned decimal with the
sign overpunched in the last byte: positive { A B C D E F G H I = 0..9, negative } J K L M N O P Q R = 0..9; a positive
value may also arrive with a plain digit (unsigned zone, "F-sign").

Return code: 4

## Example 1: first posting of the run; its category balance does not exist yet (record created)

Daily transaction (DALYTRAN-RECORD): DALYTRAN-ID «0000000001774260», DALYTRAN-TYPE-CD «03», DALYTRAN-CAT-CD «0090», DALYTRAN-AMT «0000009190{» = 919.00, DALYTRAN-CARD-NUM «0927987108636232», DALYTRAN-ORIG-TS «2022-06-10 19:27:53.000000», DALYTRAN-PROC-TS «                          ».

Cross-reference: card 0927987108636232 -> XREF-ACCT-ID 00000000020.

Account before: ACCT-CURR-BAL «00000003690{» = 369.00, ACCT-CREDIT-LIMIT «00000500000{» = 50000.00, ACCT-CASH-CREDIT-LIMIT «00000010400{», ACCT-EXPIRAION-DATE «2099-12-31», ACCT-CURR-CYC-CREDIT «00000000000{» = 0.00, ACCT-CURR-CYC-DEBIT «00000000000{» = 0.00.

Checks: WS-TEMP-BAL = ACCT-CURR-CYC-CREDIT - ACCT-CURR-CYC-DEBIT + DALYTRAN-AMT = 0.00 - 0.00 + 919.00 = 919.00; ACCT-CREDIT-LIMIT 50000.00 >= WS-TEMP-BAL; ACCT-EXPIRAION-DATE 2099-12-31 >= DALYTRAN-ORIG-TS(1:10) 2022-06-10.

Outcome: POSTED.

Account after: ACCT-CURR-BAL «00000012880{» = 1288.00, ACCT-CREDIT-LIMIT «00000500000{» = 50000.00, ACCT-CASH-CREDIT-LIMIT «00000010400{», ACCT-EXPIRAION-DATE «2099-12-31», ACCT-CURR-CYC-CREDIT «00000009190{» = 919.00, ACCT-CURR-CYC-DEBIT «00000000000{» = 0.00. Bytes that differ from before: 18-21, 85-87.

Category balance (TRAN-CAT-BAL-RECORD) key «00000000020030090»: READ did not find it (INVALID KEY; console line «TCATBAL record not found for key : 00000000020030090.. Creating.»); the INTO area TRAN-CAT-BAL-RECORD is not changed by the failed READ. The program then runs INITIALIZE TRAN-CAT-BAL-RECORD, moves the key fields and the amount, and writes «000000000200300900000009190{\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00» (TRAN-CAT-BAL 919.00). FILLER@28 «\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00» was not written by the program (INITIALIZE skips FILLER): it is what the record area still held, the storage default: no category-balance record had been read or written yet in this run.

Posted transaction (TRAN-RECORD, 350 bytes): TRAN-ID «0000000001774260», TRAN-AMT «0000009190{», TRAN-ORIG-TS «2022-06-10 19:27:53.000000», TRAN-PROC-TS «2022-07-05-10.11.12.000000», FILLER@330 «\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00»; bytes 0-303 equal the daily record.

## Example 2: purchase posted to an existing category balance

Daily transaction (DALYTRAN-RECORD): DALYTRAN-ID «0000000000683580», DALYTRAN-TYPE-CD «01», DALYTRAN-CAT-CD «0001», DALYTRAN-AMT «0000005047G» = 504.77, DALYTRAN-CARD-NUM «4859452612877065», DALYTRAN-ORIG-TS «2022-06-10 19:27:53.000000», DALYTRAN-PROC-TS «                          ».

Cross-reference: card 4859452612877065 -> XREF-ACCT-ID 00000000007.

Account before: ACCT-CURR-BAL «00000001930{» = 193.00, ACCT-CREDIT-LIMIT «00000500000{» = 50000.00, ACCT-CASH-CREDIT-LIMIT «00000002640{», ACCT-EXPIRAION-DATE «2099-12-31», ACCT-CURR-CYC-CREDIT «00000000000{» = 0.00, ACCT-CURR-CYC-DEBIT «00000000000{» = 0.00.

Checks: WS-TEMP-BAL = ACCT-CURR-CYC-CREDIT - ACCT-CURR-CYC-DEBIT + DALYTRAN-AMT = 0.00 - 0.00 + 504.77 = 504.77; ACCT-CREDIT-LIMIT 50000.00 >= WS-TEMP-BAL; ACCT-EXPIRAION-DATE 2099-12-31 >= DALYTRAN-ORIG-TS(1:10) 2022-06-10.

Outcome: POSTED.

Account after: ACCT-CURR-BAL «00000006977G» = 697.77, ACCT-CREDIT-LIMIT «00000500000{» = 50000.00, ACCT-CASH-CREDIT-LIMIT «00000002640{», ACCT-EXPIRAION-DATE «2099-12-31», ACCT-CURR-CYC-CREDIT «00000005047G» = 504.77, ACCT-CURR-CYC-DEBIT «00000000000{» = 0.00. Bytes that differ from before: 19-19, 21-23, 85-85, 87-89.

Category balance (TRAN-CAT-BAL-RECORD) key «00000000007010001»: READ found «00000000007010001» = «000000000070100010000000000{gggggggggggggggggggggg» (TRAN-CAT-BAL 0.00); rewritten as «000000000070100010000005047Ggggggggggggggggggggggg» (TRAN-CAT-BAL 504.77). Bytes that differ: 23-23, 25-27. The record area TRAN-CAT-BAL-RECORD now holds this record (FILLER «gggggggggggggggggggggg»).

Posted transaction (TRAN-RECORD, 350 bytes): TRAN-ID «0000000000683580», TRAN-AMT «0000005047G», TRAN-ORIG-TS «2022-06-10 19:27:53.000000», TRAN-PROC-TS «2022-07-05-10.11.12.000000», FILLER@330 «\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00»; bytes 0-303 equal the daily record.

## Example 3: credit (negative amount) posted; its category balance does not exist yet (record created)

Daily transaction (DALYTRAN-RECORD): DALYTRAN-ID «0000000009101861», DALYTRAN-TYPE-CD «01», DALYTRAN-CAT-CD «0090», DALYTRAN-AMT «0000002817P» = -281.77, DALYTRAN-CARD-NUM «8040580410348680», DALYTRAN-ORIG-TS «2022-06-10 19:27:53.000000», DALYTRAN-PROC-TS «                          ».

Cross-reference: card 8040580410348680 -> XREF-ACCT-ID 00000000026.

Account before: ACCT-CURR-BAL «00000000460{» = 46.00, ACCT-CREDIT-LIMIT «00000500000{» = 50000.00, ACCT-CASH-CREDIT-LIMIT «00000013750{», ACCT-EXPIRAION-DATE «2099-12-31», ACCT-CURR-CYC-CREDIT «00000000000{» = 0.00, ACCT-CURR-CYC-DEBIT «00000000000{» = 0.00.

Checks: WS-TEMP-BAL = ACCT-CURR-CYC-CREDIT - ACCT-CURR-CYC-DEBIT + DALYTRAN-AMT = 0.00 - 0.00 + -281.77 = -281.77; ACCT-CREDIT-LIMIT 50000.00 >= WS-TEMP-BAL; ACCT-EXPIRAION-DATE 2099-12-31 >= DALYTRAN-ORIG-TS(1:10) 2022-06-10.

Outcome: POSTED.

Account after: ACCT-CURR-BAL «00000002357P» = -235.77, ACCT-CREDIT-LIMIT «00000500000{» = 50000.00, ACCT-CASH-CREDIT-LIMIT «00000013750{», ACCT-EXPIRAION-DATE «2099-12-31», ACCT-CURR-CYC-CREDIT «00000000000{» = 0.00, ACCT-CURR-CYC-DEBIT «00000002817P» = -281.77. Bytes that differ from before: 19-23, 97-101.

Category balance (TRAN-CAT-BAL-RECORD) key «00000000026010090»: READ did not find it (INVALID KEY; console line «TCATBAL record not found for key : 00000000026010090.. Creating.»); the INTO area TRAN-CAT-BAL-RECORD is not changed by the failed READ. The program then runs INITIALIZE TRAN-CAT-BAL-RECORD, moves the key fields and the amount, and writes «000000000260100900000002817Pgggggggggggggggggggggg» (TRAN-CAT-BAL -281.77). FILLER@28 «gggggggggggggggggggggg» was not written by the program (INITIALIZE skips FILLER): it is what the record area still held, the record read and rewritten in example 2 (key 00000000007010001).

Posted transaction (TRAN-RECORD, 350 bytes): TRAN-ID «0000000009101861», TRAN-AMT «0000002817P», TRAN-ORIG-TS «2022-06-10 19:27:53.000000», TRAN-PROC-TS «2022-07-05-10.11.12.000000», FILLER@330 «\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00»; bytes 0-303 equal the daily record.

## Example 4: card number not in the cross-reference file (reject 100)

Daily transaction (DALYTRAN-RECORD): DALYTRAN-ID «0000000010142252», DALYTRAN-TYPE-CD «01», DALYTRAN-CAT-CD «0001», DALYTRAN-AMT «0000004546F» = 454.66, DALYTRAN-CARD-NUM «9999999999999999», DALYTRAN-ORIG-TS «2022-06-10 19:27:53.000000», DALYTRAN-PROC-TS «                          ».

Cross-reference: card 9999999999999999 is not in CARDXREF.

Outcome: REJECTED. Reject record (DALYREJS, 430 bytes): bytes 0-349 are the daily record exactly as read; WS-VALIDATION-FAIL-REASON «0100»; WS-VALIDATION-FAIL-REASON-DESC «INVALID CARD NUMBER FOUND                                                   ».

Category balances: not read or written for a rejected transaction (the record area keeps «gggggggggggggggggggggg» in FILLER).

## Example 5: account record missing (reject 101)

Daily transaction (DALYTRAN-RECORD): DALYTRAN-ID «0000000010229018», DALYTRAN-TYPE-CD «01», DALYTRAN-CAT-CD «0001», DALYTRAN-AMT «0000008499I» = 849.99, DALYTRAN-CARD-NUM «7379335634661142», DALYTRAN-ORIG-TS «2022-06-10 19:27:53.000000», DALYTRAN-PROC-TS «                          ».

Cross-reference: card 7379335634661142 -> XREF-ACCT-ID 00000000045.

Account 00000000045 was removed from ACCTDATA for this example.

Outcome: REJECTED. Reject record (DALYREJS, 430 bytes): bytes 0-349 are the daily record exactly as read; WS-VALIDATION-FAIL-REASON «0101»; WS-VALIDATION-FAIL-REASON-DESC «ACCOUNT RECORD NOT FOUND                                                    ».

Category balances: not read or written for a rejected transaction (the record area keeps «gggggggggggggggggggggg» in FILLER).

## Example 6: over the credit limit (reject 102)

Daily transaction (DALYTRAN-RECORD): DALYTRAN-ID «0000000016259484», DALYTRAN-TYPE-CD «03», DALYTRAN-CAT-CD «0001», DALYTRAN-AMT «0000001000{» = 100.00, DALYTRAN-CARD-NUM «4011500891777367», DALYTRAN-ORIG-TS «2022-06-10 19:27:53.000000», DALYTRAN-PROC-TS «                          ».

Cross-reference: card 4011500891777367 -> XREF-ACCT-ID 00000000013.

Account before: ACCT-CURR-BAL «00000000410{» = 41.00, ACCT-CREDIT-LIMIT «00000000010{» = 1.00, ACCT-CASH-CREDIT-LIMIT «00000049220{», ACCT-EXPIRAION-DATE «2099-12-31», ACCT-CURR-CYC-CREDIT «00000000000{» = 0.00, ACCT-CURR-CYC-DEBIT «00000000000{» = 0.00.

Checks: WS-TEMP-BAL = ACCT-CURR-CYC-CREDIT - ACCT-CURR-CYC-DEBIT + DALYTRAN-AMT = 0.00 - 0.00 + 100.00 = 100.00; ACCT-CREDIT-LIMIT 1.00 < WS-TEMP-BAL; ACCT-EXPIRAION-DATE 2099-12-31 >= DALYTRAN-ORIG-TS(1:10) 2022-06-10.

Outcome: REJECTED. Reject record (DALYREJS, 430 bytes): bytes 0-349 are the daily record exactly as read; WS-VALIDATION-FAIL-REASON «0102»; WS-VALIDATION-FAIL-REASON-DESC «OVERLIMIT TRANSACTION                                                       ».

Account after: unchanged (every byte).

Category balances: not read or written for a rejected transaction (the record area keeps «gggggggggggggggggggggg» in FILLER).

## Example 7: account expired before the transaction date (reject 103)

Daily transaction (DALYTRAN-RECORD): DALYTRAN-ID «0000000019065428», DALYTRAN-TYPE-CD «03», DALYTRAN-CAT-CD «0001», DALYTRAN-AMT «0000005358H» = 535.88, DALYTRAN-CARD-NUM «6503535181795992», DALYTRAN-ORIG-TS «2022-06-10 19:27:53.000000», DALYTRAN-PROC-TS «                          ».

Cross-reference: card 6503535181795992 -> XREF-ACCT-ID 00000000048.

Account before: ACCT-CURR-BAL «00000002260{» = 226.00, ACCT-CREDIT-LIMIT «00000500000{» = 50000.00, ACCT-CASH-CREDIT-LIMIT «00000006120{», ACCT-EXPIRAION-DATE «2000-01-01», ACCT-CURR-CYC-CREDIT «00000000000{» = 0.00, ACCT-CURR-CYC-DEBIT «00000000000{» = 0.00.

Checks: WS-TEMP-BAL = ACCT-CURR-CYC-CREDIT - ACCT-CURR-CYC-DEBIT + DALYTRAN-AMT = 0.00 - 0.00 + 535.88 = 535.88; ACCT-CREDIT-LIMIT 50000.00 >= WS-TEMP-BAL; ACCT-EXPIRAION-DATE 2000-01-01 < DALYTRAN-ORIG-TS(1:10) 2022-06-10.

Outcome: REJECTED. Reject record (DALYREJS, 430 bytes): bytes 0-349 are the daily record exactly as read; WS-VALIDATION-FAIL-REASON «0103»; WS-VALIDATION-FAIL-REASON-DESC «TRANSACTION RECEIVED AFTER ACCT EXPIRATION                                  ».

Account after: unchanged (every byte).

Category balances: not read or written for a rejected transaction (the record area keeps «gggggggggggggggggggggg» in FILLER).

## Example 8: purchase posted to another existing category balance

Daily transaction (DALYTRAN-RECORD): DALYTRAN-ID «0000000006292564», DALYTRAN-TYPE-CD «01», DALYTRAN-CAT-CD «0001», DALYTRAN-AMT «0000000678H» = 67.88, DALYTRAN-CARD-NUM «6009619150674526», DALYTRAN-ORIG-TS «2022-06-10 19:27:53.000000», DALYTRAN-PROC-TS «                          ».

Cross-reference: card 6009619150674526 -> XREF-ACCT-ID 00000000005.

Account before: ACCT-CURR-BAL «00000003450{» = 345.00, ACCT-CREDIT-LIMIT «00000500000{» = 50000.00, ACCT-CASH-CREDIT-LIMIT «00000024300{», ACCT-EXPIRAION-DATE «2099-12-31», ACCT-CURR-CYC-CREDIT «00000000000{» = 0.00, ACCT-CURR-CYC-DEBIT «00000000000{» = 0.00.

Checks: WS-TEMP-BAL = ACCT-CURR-CYC-CREDIT - ACCT-CURR-CYC-DEBIT + DALYTRAN-AMT = 0.00 - 0.00 + 67.88 = 67.88; ACCT-CREDIT-LIMIT 50000.00 >= WS-TEMP-BAL; ACCT-EXPIRAION-DATE 2099-12-31 >= DALYTRAN-ORIG-TS(1:10) 2022-06-10.

Outcome: POSTED.

Account after: ACCT-CURR-BAL «00000004128H» = 412.88, ACCT-CREDIT-LIMIT «00000500000{» = 50000.00, ACCT-CASH-CREDIT-LIMIT «00000024300{», ACCT-EXPIRAION-DATE «2099-12-31», ACCT-CURR-CYC-CREDIT «00000000678H» = 67.88, ACCT-CURR-CYC-DEBIT «00000000000{» = 0.00. Bytes that differ from before: 19-23, 86-89.

Category balance (TRAN-CAT-BAL-RECORD) key «00000000005010001»: READ found «00000000005010001» = «000000000050100010000000000{eeeeeeeeeeeeeeeeeeeeee» (TRAN-CAT-BAL 0.00); rewritten as «000000000050100010000000678Heeeeeeeeeeeeeeeeeeeeee» (TRAN-CAT-BAL 67.88). Bytes that differ: 24-27. The record area TRAN-CAT-BAL-RECORD now holds this record (FILLER «eeeeeeeeeeeeeeeeeeeeee»).

Posted transaction (TRAN-RECORD, 350 bytes): TRAN-ID «0000000006292564», TRAN-AMT «0000000678H», TRAN-ORIG-TS «2022-06-10 19:27:53.000000», TRAN-PROC-TS «2022-07-05-10.11.12.000000», FILLER@330 «\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00»; bytes 0-303 equal the daily record.

## Example 9: zero amount posted to an account whose positive amounts are stored with an unsigned zone (F-sign); its category balance does not exist yet (record created)

Daily transaction (DALYTRAN-RECORD): DALYTRAN-ID «0000000021711604», DALYTRAN-TYPE-CD «01», DALYTRAN-CAT-CD «0090», DALYTRAN-AMT «0000000000{» = 0.00, DALYTRAN-CARD-NUM «9501733721429893», DALYTRAN-ORIG-TS «2022-06-10 19:27:53.000000», DALYTRAN-PROC-TS «                          ».

Cross-reference: card 9501733721429893 -> XREF-ACCT-ID 00000000009.

Account before: ACCT-CURR-BAL «000000056000» = 560.00, ACCT-CREDIT-LIMIT «000005000000» = 50000.00, ACCT-CASH-CREDIT-LIMIT «000000206500», ACCT-EXPIRAION-DATE «2099-12-31», ACCT-CURR-CYC-CREDIT «000000000000» = 0.00, ACCT-CURR-CYC-DEBIT «000000000000» = 0.00.

Checks: WS-TEMP-BAL = ACCT-CURR-CYC-CREDIT - ACCT-CURR-CYC-DEBIT + DALYTRAN-AMT = 0.00 - 0.00 + 0.00 = 0.00; ACCT-CREDIT-LIMIT 50000.00 >= WS-TEMP-BAL; ACCT-EXPIRAION-DATE 2099-12-31 >= DALYTRAN-ORIG-TS(1:10) 2022-06-10.

Outcome: POSTED.

Account after: ACCT-CURR-BAL «00000005600{» = 560.00, ACCT-CREDIT-LIMIT «000005000000» = 50000.00, ACCT-CASH-CREDIT-LIMIT «000000206500», ACCT-EXPIRAION-DATE «2099-12-31», ACCT-CURR-CYC-CREDIT «00000000000{» = 0.00, ACCT-CURR-CYC-DEBIT «000000000000» = 0.00. Bytes that differ from before: 23-23, 89-89.

Category balance (TRAN-CAT-BAL-RECORD) key «00000000009010090»: READ did not find it (INVALID KEY; console line «TCATBAL record not found for key : 00000000009010090.. Creating.»); the INTO area TRAN-CAT-BAL-RECORD is not changed by the failed READ. The program then runs INITIALIZE TRAN-CAT-BAL-RECORD, moves the key fields and the amount, and writes «000000000090100900000000000{eeeeeeeeeeeeeeeeeeeeee» (TRAN-CAT-BAL 0.00). FILLER@28 «eeeeeeeeeeeeeeeeeeeeee» was not written by the program (INITIALIZE skips FILLER): it is what the record area still held, the record read and rewritten in example 8 (key 00000000005010001).

Posted transaction (TRAN-RECORD, 350 bytes): TRAN-ID «0000000021711604», TRAN-AMT «0000000000{», TRAN-ORIG-TS «2022-06-10 19:27:53.000000», TRAN-PROC-TS «2022-07-05-10.11.12.000000», FILLER@330 «\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00»; bytes 0-303 equal the daily record.

## Complete SYSOUT of the run

```
START OF EXECUTION OF PROGRAM CBTRN02C
TCATBAL record not found for key : 00000000020030090.. Creating.
TCATBAL record not found for key : 00000000026010090.. Creating.
TCATBAL record not found for key : 00000000009010090.. Creating.
TRANSACTIONS PROCESSED :000000009
TRANSACTIONS REJECTED  :000000004
END OF EXECUTION OF PROGRAM CBTRN02C
```

## Same input, storage default x'20' (a space)

When ctx.storageDefault() is a space, the record area starts as spaces: example 1 then creates «000000000200300900000009190{                      » (FILLER@28 «                      »), and its posted transaction ends with FILLER@330 «                    ». Compared with the run above, the only bytes that differ are bytes that hold the storage default (x'00' above, a space here):

- AWS.M2.CARDDEMO.TCATBALF.VSAM.KSDS: bytes 28-49 of 1 record(s)
- AWS.M2.CARDDEMO.TRANSACT.VSAM.KSDS: bytes 330-349 of 5 record(s)

Never hard-code x'00' or spaces: use ctx.storageDefault().
