# Worked examples: CBACT04C (traced from the faithful program)

One run of the faithful translation of CBACT04C on a small input built for illustration (not one of the acceptance
scenarios): PARM «2023063000»; a category-balance file (TCATBALF) of five records for three accounts, in key order; a
disclosure-group file (DISCGRP) of five rate records; the three accounts' ACCT-GROUP-ID set to GROUPA, GROUPB and
GROUPC. Cross-reference and account files are the CardDemo ones. The batch clock is 2022-07-05T10:11:12-07:00; the
storage default byte (ctx.storageDefault()) is x'00'.

Raw bytes are shown between « and » (ISO-8859-1; \x00 is byte 0). Signed numeric fields are zoned decimal with the
sign overpunched in the last byte: positive { A B C D E F G H I = 0..9, negative } J K L M N O P Q R = 0..9.

Return code: 0

## Input: disclosure-group rates (DIS-GROUP-RECORD, key DIS-ACCT-GROUP-ID + DIS-TRAN-TYPE-CD + DIS-TRAN-CAT-CD)

- «DEFAULT   010002» DIS-INT-RATE «00249I» = 24.99
- «GROUPA    010001» DIS-INT-RATE «00180{» = 18.00
- «GROUPA    020001» DIS-INT-RATE «00150{» = 15.00
- «GROUPB    010001» DIS-INT-RATE «00000{» = 0.00
- «GROUPC    010001» DIS-INT-RATE «00123D» = 12.34

## Input: category balances (TRAN-CAT-BAL-RECORD), read in this order

- record 1: «000000000010100010000010000{0000000000000000000000» -> account 00000000001, type 01, category 0001, TRAN-CAT-BAL 1000.00 (group rate found)
- record 2: «000000000010100020000002505E0000000000000000000000» -> account 00000000001, type 01, category 0002, TRAN-CAT-BAL 250.55 (no rate for the account's group: the DEFAULT group's rate is used)
- record 3: «000000000010200010000003000J0000000000000000000000» -> account 00000000001, type 02, category 0001, TRAN-CAT-BAL -300.01 (negative balance)
- record 4: «000000000020100010000005000{0000000000000000000000» -> account 00000000002, type 01, category 0001, TRAN-CAT-BAL 500.00 (rate 0.00: no interest, no transaction)
- record 5: «000000000030100010000009999I0000000000000000000000» -> account 00000000003, type 01, category 0001, TRAN-CAT-BAL 999.99 (last category balance of the file)

## Interest per category balance

### Record 1: account 00000000001, type 01, category 0001

Rate lookup: key «GROUPA    010001» is found: DIS-INT-RATE 18.00.

WS-MONTHLY-INT = (TRAN-CAT-BAL * DIS-INT-RATE) / 1200 = (1000.00 * 18.00) / 1200 = 15 exactly; stored in PIC S9(09)V99 as 15.00. WS-TOTAL-INT for the account is now 15.00.

Interest transaction written to SYSTRAN (TRAN-RECORD, 350 bytes, transaction 1 of the run):

- TRAN-ID «2023063000000001» (PARM date «2023063000» + WS-TRANID-SUFFIX 000001)
- TRAN-TYPE-CD «01», TRAN-CAT-CD «0005», TRAN-SOURCE «System    »
- TRAN-DESC «Int. for a/c 00000000001\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00»
- TRAN-AMT «0000000150{» = 15.00, TRAN-MERCHANT-ID «000000000»
- TRAN-MERCHANT-NAME «                                                  », TRAN-MERCHANT-CITY «                                                  », TRAN-MERCHANT-ZIP «          »
- TRAN-CARD-NUM «9680294154603697» (XREF-CARD-NUM of the account's first cross-reference: «9680294154603697»)
- TRAN-ORIG-TS «2022-07-05-10.11.12.000000», TRAN-PROC-TS «2022-07-05-10.11.12.000000», FILLER@330 «\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00»

### Record 2: account 00000000001, type 01, category 0002

Rate lookup: key «GROUPA    010002» is not in DISCGRP (console lines «DISCLOSURE GROUP RECORD MISSING» and «TRY WITH DEFAULT GROUP CODE»); key «DEFAULT   010002» is read instead: DIS-INT-RATE 24.99.

WS-MONTHLY-INT = (TRAN-CAT-BAL * DIS-INT-RATE) / 1200 = (250.55 * 24.99) / 1200 = 5.21770375 exactly; stored in PIC S9(09)V99 as 5.21 (digits beyond the second decimal are dropped, not rounded). WS-TOTAL-INT for the account is now 20.21.

Interest transaction written to SYSTRAN (TRAN-RECORD, 350 bytes, transaction 2 of the run):

- TRAN-ID «2023063000000002» (PARM date «2023063000» + WS-TRANID-SUFFIX 000002)
- TRAN-TYPE-CD «01», TRAN-CAT-CD «0005», TRAN-SOURCE «System    »
- TRAN-DESC «Int. for a/c 00000000001\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00»
- TRAN-AMT «0000000052A» = 5.21, TRAN-MERCHANT-ID «000000000»
- TRAN-MERCHANT-NAME «                                                  », TRAN-MERCHANT-CITY «                                                  », TRAN-MERCHANT-ZIP «          »
- TRAN-CARD-NUM «9680294154603697» (XREF-CARD-NUM of the account's first cross-reference: «9680294154603697»)
- TRAN-ORIG-TS «2022-07-05-10.11.12.000000», TRAN-PROC-TS «2022-07-05-10.11.12.000000», FILLER@330 «\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00»

### Record 3: account 00000000001, type 02, category 0001

Rate lookup: key «GROUPA    020001» is found: DIS-INT-RATE 15.00.

WS-MONTHLY-INT = (TRAN-CAT-BAL * DIS-INT-RATE) / 1200 = (-300.01 * 15.00) / 1200 = -3.750125 exactly; stored in PIC S9(09)V99 as -3.75 (digits beyond the second decimal are dropped, not rounded). WS-TOTAL-INT for the account is now 16.46.

Interest transaction written to SYSTRAN (TRAN-RECORD, 350 bytes, transaction 3 of the run):

- TRAN-ID «2023063000000003» (PARM date «2023063000» + WS-TRANID-SUFFIX 000003)
- TRAN-TYPE-CD «01», TRAN-CAT-CD «0005», TRAN-SOURCE «System    »
- TRAN-DESC «Int. for a/c 00000000001\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00»
- TRAN-AMT «0000000037N» = -3.75, TRAN-MERCHANT-ID «000000000»
- TRAN-MERCHANT-NAME «                                                  », TRAN-MERCHANT-CITY «                                                  », TRAN-MERCHANT-ZIP «          »
- TRAN-CARD-NUM «9680294154603697» (XREF-CARD-NUM of the account's first cross-reference: «9680294154603697»)
- TRAN-ORIG-TS «2022-07-05-10.11.12.000000», TRAN-PROC-TS «2022-07-05-10.11.12.000000», FILLER@330 «\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00»

### Record 4: account 00000000002, type 01, category 0001

Rate lookup: key «GROUPB    010001» is found: DIS-INT-RATE 0.00.

DIS-INT-RATE = 0: no interest is computed and no transaction is written.

### Record 5: account 00000000003, type 01, category 0001

Rate lookup: key «GROUPC    010001» is found: DIS-INT-RATE 12.34.

WS-MONTHLY-INT = (TRAN-CAT-BAL * DIS-INT-RATE) / 1200 = (999.99 * 12.34) / 1200 = 10.2832305 exactly; stored in PIC S9(09)V99 as 10.28 (digits beyond the second decimal are dropped, not rounded). WS-TOTAL-INT for the account is now 10.28.

Interest transaction written to SYSTRAN (TRAN-RECORD, 350 bytes, transaction 4 of the run):

- TRAN-ID «2023063000000004» (PARM date «2023063000» + WS-TRANID-SUFFIX 000004)
- TRAN-TYPE-CD «01», TRAN-CAT-CD «0005», TRAN-SOURCE «System    »
- TRAN-DESC «Int. for a/c 00000000003\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00»
- TRAN-AMT «0000000102H» = 10.28, TRAN-MERCHANT-ID «000000000»
- TRAN-MERCHANT-NAME «                                                  », TRAN-MERCHANT-CITY «                                                  », TRAN-MERCHANT-ZIP «          »
- TRAN-CARD-NUM «3999169246375885» (XREF-CARD-NUM of the account's first cross-reference: «3999169246375885»)
- TRAN-ORIG-TS «2022-07-05-10.11.12.000000», TRAN-PROC-TS «2022-07-05-10.11.12.000000», FILLER@330 «\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00»

## Accounts (ACCOUNT-RECORD)

### Account 00000000001 (ACCT-GROUP-ID GROUPA, interest total 16.46)

Before: ACCT-CURR-BAL «00000012881{» = 1288.10, ACCT-CREDIT-LIMIT «00000020200{», ACCT-CASH-CREDIT-LIMIT «00000010200{», ACCT-CURR-CYC-CREDIT «00000011648G» = 1164.87, ACCT-CURR-CYC-DEBIT «00000000707P» = -70.77, ACCT-GROUP-ID «GROUPA    ».

After (rewritten when the first record of the next account was read): ACCT-CURR-BAL «00000013045F» = 1304.56, ACCT-CREDIT-LIMIT «00000020200{», ACCT-CASH-CREDIT-LIMIT «00000010200{», ACCT-CURR-CYC-CREDIT «00000000000{» = 0.00, ACCT-CURR-CYC-DEBIT «00000000000{» = 0.00, ACCT-GROUP-ID «GROUPA    ». ACCT-CURR-BAL = 1288.10 + 16.46 = 1304.56; ACCT-CURR-CYC-CREDIT and ACCT-CURR-CYC-DEBIT are set to 0. Bytes that differ from before: 19-23, 84-89, 98-98, 100-101; every other byte (credit limits, FILLER) is as it was read.

### Account 00000000002 (ACCT-GROUP-ID GROUPB, interest total 0)

Before: ACCT-CURR-BAL «00000017349G» = 1734.97, ACCT-CREDIT-LIMIT «00000061300{», ACCT-CASH-CREDIT-LIMIT «00000054480{», ACCT-CURR-CYC-CREDIT «00000023399G» = 2339.97, ACCT-CURR-CYC-DEBIT «00000007630}» = -763.00, ACCT-GROUP-ID «GROUPB    ».

After (rewritten when the first record of the next account was read): ACCT-CURR-BAL «00000017349G» = 1734.97, ACCT-CREDIT-LIMIT «00000061300{», ACCT-CASH-CREDIT-LIMIT «00000054480{», ACCT-CURR-CYC-CREDIT «00000000000{» = 0.00, ACCT-CURR-CYC-DEBIT «00000000000{» = 0.00, ACCT-GROUP-ID «GROUPB    ». ACCT-CURR-BAL = 1734.97 + 0 = 1734.97; ACCT-CURR-CYC-CREDIT and ACCT-CURR-CYC-DEBIT are set to 0. Bytes that differ from before: 84-89, 97-99, 101-101; every other byte (credit limits, FILLER) is as it was read.

### Account 00000000003 (ACCT-GROUP-ID GROUPC, interest total 10.28)

Before: ACCT-CURR-BAL «00000018576E» = 1857.65, ACCT-CREDIT-LIMIT «00000049090{», ACCT-CASH-CREDIT-LIMIT «00000005380{», ACCT-CURR-CYC-CREDIT «00000025908G» = 2590.87, ACCT-CURR-CYC-DEBIT «00000008802K» = -880.22, ACCT-GROUP-ID «GROUPC    ».

After: unchanged (every byte). This is the last account in TCATBALF: the program updates an account only when the next record belongs to a different account, and the end-of-file branch that would update the last one never runs (quirk Q-01), although its interest transaction was written.

## Complete SYSOUT of the run

```
START OF EXECUTION OF PROGRAM CBACT04C
000000000010100010000010000{0000000000000000000000
000000000010100020000002505E0000000000000000000000
DISCLOSURE GROUP RECORD MISSING
TRY WITH DEFAULT GROUP CODE
000000000010200010000003000J0000000000000000000000
000000000020100010000005000{0000000000000000000000
000000000030100010000009999I0000000000000000000000
END OF EXECUTION OF PROGRAM CBACT04C
```

## Same input, storage default x'20' (a space)

Compared with the run above, the only bytes that differ are bytes that hold the storage default (x'00' above, a space here):

- AWS.M2.CARDDEMO.SYSTRAN: bytes 330-349 of 4 record(s), bytes 56-131 of 4 record(s)

Never hard-code x'00' or spaces: use ctx.storageDefault().
