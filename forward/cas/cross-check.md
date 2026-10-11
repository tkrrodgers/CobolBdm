# Cross-check: reference implementation against the faithful programs

Totals: {agrees=200, explained=44}

Every legacy-derived scenario's reference result compared with the faithful program's decoded outcome. A difference
must be explained by a decision D-n of SPEC.md section 8; the SME's verdict follows each.

## cust-001: SAM1 'oracle scenario base'

* explained by D-11 (an added profile's occupation)
* customer 00004A.occupation: legacy "000000000", reference ""

## cust-002: SAM1 'oracle scenario all-transactions'

* explained by D-4 (a comment line sets the legacy's previous key), D-7 (a record added earlier in the batch), D-11 (an added profile's occupation)
* errors: legacy [00001A:OUT_OF_SEQUENCE, 00002A:NOT_NUMERIC, 00002A:INVALID_ACTION, 00002A:INVALID_FIELD, 00002A:DUPLICATE_KEY, 00003A:NO_MATCHING_KEY, 00004A:NO_MATCHING_KEY, 00004B:NO_MATCHING_KEY, 00005A:INVALID_CODE, 00001A:OUT_OF_SEQUENCE, 00005A:NOT_NUMERIC, 00008A:INVALID_CODE], reference [00002A:NOT_NUMERIC, 00002A:INVALID_ACTION, 00002A:INVALID_FIELD, 00002A:DUPLICATE_KEY, 00003A:NO_MATCHING_KEY, 00004B:NO_MATCHING_KEY, 00005A:INVALID_CODE, 00001A:OUT_OF_SEQUENCE, 00005A:NOT_NUMERIC, 00008A:INVALID_CODE]
* totals: legacy [{"changeType":"ADD","requested":2,"processed":1,"inError":1},{"changeType":"DELETE","requested":2,"processed":1,"inError":1},{"changeType":"UPDATE","requested":13,"processed":7,"inError":6}], reference [{"changeType":"ADD","requested":2,"processed":1,"inError":1},{"changeType":"DELETE","requested":2,"processed":1,"inError":1},{"changeType":"UPDATE","requested":14,"processed":9,"inError":5}]
* customer 00001A.accountBalance: legacy "4455.99", reference "12.34"
* customer 00004A.name: legacy "", reference "NEWCO"
* customer 00004A.occupation: legacy "000000000", reference ""

## cust-003: SAM1 'CUSTFILE: empty'

* explained by D-11 (an added profile's occupation)
* customer 00004A.occupation: legacy "000000000", reference ""

## cust-004: SAM1 'CUSTFILE: first record only'

* explained by D-11 (an added profile's occupation)
* customer 00004A.occupation: legacy "000000000", reference ""

## cust-008: SAM1 'TRANFILE: every record twice'

* explained by D-7 (a record added earlier in the batch), D-11 (an added profile's occupation)
* errors: legacy [00003A:NO_MATCHING_KEY], reference [00003A:NO_MATCHING_KEY, 00004A:DUPLICATE_KEY]
* totals: legacy [{"changeType":"ADD","requested":2,"processed":2,"inError":0},{"changeType":"DELETE","requested":2,"processed":1,"inError":1},{"changeType":"UPDATE","requested":4,"processed":4,"inError":0}], reference [{"changeType":"ADD","requested":2,"processed":1,"inError":1},{"changeType":"DELETE","requested":2,"processed":1,"inError":1},{"changeType":"UPDATE","requested":4,"processed":4,"inError":0}]
* customer 00004A.occupation: legacy "000000000", reference ""
* customer 00004A: only in the legacy

## cust-020: SAM1 'all-transactions / change 12 alone'

* explained by D-11 (an added profile's occupation)
* customer 00004A.occupation: legacy "000000000", reference ""

## cust-052: SAM1 'authored change 22: UPDATE 99999C        REPLACE  BALANCE    01 +000000100'

* explained by D-5 (a BALANCE or ORDERS update of a contact)
* errors: legacy [], reference [99999C:INVALID_FIELD]
* totals: legacy [{"changeType":"ADD","requested":0,"processed":0,"inError":0},{"changeType":"DELETE","requested":0,"processed":0,"inError":0},{"changeType":"UPDATE","requested":1,"processed":1,"inError":0}], reference [{"changeType":"ADD","requested":0,"processed":0,"inError":0},{"changeType":"DELETE","requested":0,"processed":0,"inError":0},{"changeType":"UPDATE","requested":1,"processed":0,"inError":1}]
* customer 99999C.description: legacy "000000010|", reference "CNCT DESC|"

## cust-053: SAM1 'authored change 23: UPDATE 99999C        ADD      ORDERS     01 +000000100'

* explained by D-5 (a BALANCE or ORDERS update of a contact)
* errors: legacy [], reference [99999C:INVALID_FIELD]
* totals: legacy [{"changeType":"ADD","requested":0,"processed":0,"inError":0},{"changeType":"DELETE","requested":0,"processed":0,"inError":0},{"changeType":"UPDATE","requested":1,"processed":1,"inError":0}], reference [{"changeType":"ADD","requested":0,"processed":0,"inError":0},{"changeType":"DELETE","requested":0,"processed":0,"inError":0},{"changeType":"UPDATE","requested":1,"processed":0,"inError":1}]
* customer 99999C.description: legacy "CNCT DESC0", reference "CNCT DESC|"
* customer 99999C.contactInfo: legacy "3004ACT INFO-------|", reference "CONTACT INFO-------|"

## cust-056: SAM1 'authored change 26: ADD    00006A'

* explained by D-11 (an added profile's occupation)
* customer 00006A.occupation: legacy "000000000", reference ""

## cust-057: SAM1 'authored change 27: ADD    99998C'

* explained by D-6 (an added contact)
* customer 99998C.description: legacy "0000000000", reference ""
* customer 99998C.contactInfo: legacy "0000", reference ""

## cust-064: SAM1 'authored batch: added then changed in the same batch (D-7)'

* explained by D-7 (a record added earlier in the batch), D-11 (an added profile's occupation)
* errors: legacy [00004A:NO_MATCHING_KEY, 00004A:NO_MATCHING_KEY], reference []
* totals: legacy [{"changeType":"ADD","requested":2,"processed":2,"inError":0},{"changeType":"DELETE","requested":1,"processed":1,"inError":0},{"changeType":"UPDATE","requested":2,"processed":0,"inError":2}], reference [{"changeType":"ADD","requested":2,"processed":2,"inError":0},{"changeType":"DELETE","requested":1,"processed":1,"inError":0},{"changeType":"UPDATE","requested":2,"processed":2,"inError":0}]
* customer 00004A.name: legacy "", reference "NEWCO"
* customer 00004A.accountBalance: legacy "0.00", reference "10.00"
* customer 00004A.occupation: legacy "000000000", reference ""
* customer 00005A.occupation: legacy "000000000", reference ""

## cust-065: SAM1 'authored batch: comment before a lower key (D-4)'

* explained by D-4 (a comment line sets the legacy's previous key)
* errors: legacy [00001A:OUT_OF_SEQUENCE], reference []
* totals: legacy [{"changeType":"ADD","requested":0,"processed":0,"inError":0},{"changeType":"DELETE","requested":0,"processed":0,"inError":0},{"changeType":"UPDATE","requested":1,"processed":1,"inError":0}], reference [{"changeType":"ADD","requested":0,"processed":0,"inError":0},{"changeType":"DELETE","requested":0,"processed":0,"inError":0},{"changeType":"UPDATE","requested":2,"processed":2,"inError":0}]
* customer 00001A.name: legacy "IBM", reference "AFTER COMMENT"

## cust-066: SAM1 'authored batch: contact added and named (D-6)'

* explained by D-5 (a BALANCE or ORDERS update of a contact), D-6 (an added contact), D-7 (a record added earlier in the batch)
* errors: legacy [00001B:NO_MATCHING_KEY, 00001B:NO_MATCHING_KEY], reference [00001B:INVALID_FIELD]
* totals: legacy [{"changeType":"ADD","requested":1,"processed":1,"inError":0},{"changeType":"DELETE","requested":0,"processed":0,"inError":0},{"changeType":"UPDATE","requested":2,"processed":0,"inError":2}], reference [{"changeType":"ADD","requested":1,"processed":1,"inError":0},{"changeType":"DELETE","requested":0,"processed":0,"inError":0},{"changeType":"UPDATE","requested":2,"processed":1,"inError":1}]
* customer 00001B.name: legacy "", reference "FIRST CONTACT"
* customer 00001B.description: legacy "0000000000", reference ""
* customer 00001B.contactInfo: legacy "0000", reference ""

## cust-067: SAM1 'authored batch: delete then add the same key'

* explained by D-7 (a record added earlier in the batch), D-11 (an added profile's occupation)
* errors: legacy [00002A:NO_MATCHING_KEY], reference []
* totals: legacy [{"changeType":"ADD","requested":1,"processed":1,"inError":0},{"changeType":"DELETE","requested":1,"processed":1,"inError":0},{"changeType":"UPDATE","requested":1,"processed":0,"inError":1}], reference [{"changeType":"ADD","requested":1,"processed":1,"inError":0},{"changeType":"DELETE","requested":1,"processed":1,"inError":0},{"changeType":"UPDATE","requested":1,"processed":1,"inError":0}]
* customer 00002A.name: legacy "", reference "REBORN"
* customer 00002A.occupation: legacy "000000000", reference ""

## interest-01: CBACT04C 'CardDemo month'

* explained by D-1 (the last account 50 is closed by the service, never by the legacy)
* account 50.currentBalance: legacy "1945.87", reference "1964.64"
* account 50.currentCycleCredit: legacy "1501.75", reference "0.00"
* account 50.currentCycleDebit: legacy "-47.88", reference "0.00"

## interest-02: CBACT04C 'group rates missing: DEFAULT used'

* explained by D-1 (the last account 50 is closed by the service, never by the legacy)
* account 50.currentBalance: legacy "1945.87", reference "1964.64"
* account 50.currentCycleCredit: legacy "1501.75", reference "0.00"
* account 50.currentCycleDebit: legacy "-47.88", reference "0.00"

## interest-03: CBACT04C 'only DEFAULT rates'

* explained by D-1 (the last account 50 is closed by the service, never by the legacy)
* account 50.currentBalance: legacy "1945.87", reference "1964.64"
* account 50.currentCycleCredit: legacy "1501.75", reference "0.00"
* account 50.currentCycleDebit: legacy "-47.88", reference "0.00"

## interest-04: CBACT04C 'no rates at all (abend)'

* explained by D-9 (all-or-nothing)
* legacy abended (ABEND 999, return code 16); the reference throws {"type":"ReferenceDataMissing","what":"disclosure group","key":"DEFAULT010001"} and changes nothing

## interest-05: CBACT04C 'zero rates'

* explained by D-1 (the last account 50 is closed by the service, never by the legacy)
* account 50.currentCycleCredit: legacy "1501.75", reference "0.00"
* account 50.currentCycleDebit: legacy "-47.88", reference "0.00"

## interest-06: CBACT04C 'negative rates'

* explained by D-1 (the last account 50 is closed by the service, never by the legacy)
* account 50.currentBalance: legacy "1945.87", reference "1930.42"
* account 50.currentCycleCredit: legacy "1501.75", reference "0.00"
* account 50.currentCycleDebit: legacy "-47.88", reference "0.00"

## interest-07: CBACT04C 'negative category balances'

* explained by D-1 (the last account 50 is closed by the service, never by the legacy)
* account 50.currentBalance: legacy "1945.87", reference "1927.10"
* account 50.currentCycleCredit: legacy "1501.75", reference "0.00"
* account 50.currentCycleDebit: legacy "-47.88", reference "0.00"

## interest-08: CBACT04C 'one account only (Q-01)'

* explained by D-1 (the last account 1 is closed by the service, never by the legacy)
* account 1.currentBalance: legacy "1288.10", reference "1302.66"
* account 1.currentCycleCredit: legacy "1164.87", reference "0.00"
* account 1.currentCycleDebit: legacy "-70.77", reference "0.00"

## interest-10: CBACT04C 'account missing (abend)'

* explained by D-9 (all-or-nothing)
* legacy abended (ABEND 999, return code 16); the reference throws {"type":"ReferenceDataMissing","what":"account","key":"00000000001"} and changes nothing

## interest-11: CBACT04C 'cross-reference missing (abend)'

* explained by D-9 (all-or-nothing)
* legacy abended (ABEND 999, return code 16); the reference throws {"type":"ReferenceDataMissing","what":"card","key":"00000000001"} and changes nothing

## interest-12: CBACT04C 'another run date'

* explained by D-1 (the last account 50 is closed by the service, never by the legacy)
* account 50.currentBalance: legacy "1945.87", reference "1964.64"
* account 50.currentCycleCredit: legacy "1501.75", reference "0.00"
* account 50.currentCycleDebit: legacy "-47.88", reference "0.00"

## post-445: CBTRN02C 'transaction id already posted (abend) / transaction 1'

* explained by D-8 (the legacy's transaction file holds only the day, so it posts the id again; the service rejects it with 104)
* transaction 1 0000000000683580: legacy unknown, reference 104
* account 7.currentBalance: legacy "697.77", reference "193.00"
* account 7.currentCycleCredit: legacy "504.77", reference "0.00"
* category balance 7|01|1.balance: legacy "504.77", reference "0.00"
* ledger 0000000000683580.processedTimestamp: legacy "2022-07-05T10:11:12.000000", reference null
* rejections: legacy [], reference [0000000000683580:104]

## post-447: CBTRN02C 'transaction id already posted (abend) / transaction 3'

* explained by D-8 (the legacy's transaction file holds only the day, so it posts the id again; the service rejects it with 104)
* ledger 0000000000683580: only in the reference ledger

## post-453: CBTRN02C 'transaction id already posted (abend) / transaction 9'

* explained by D-8 (the legacy's transaction file holds only the day, so it posts the id again; the service rejects it with 104)
* ledger 0000000000683580: only in the reference ledger

## post-455: CBTRN02C 'transaction id already posted (abend) / transaction 11'

* explained by D-8 (the legacy's transaction file holds only the day, so it posts the id again; the service rejects it with 104)
* ledger 0000000000683580: only in the reference ledger

## post-batch-15: CBTRN02C 'transaction id already posted (abend)'

* explained by D-8 (the legacy's transaction file holds only the day, so it posts the id again; the service rejects it with 104)
* transaction 1 0000000000683580: legacy unknown, reference 104
* account 7.currentBalance: legacy "697.77", reference "193.00"
* account 7.currentCycleCredit: legacy "504.77", reference "0.00"
* category balance 7|01|1.balance: legacy "504.77", reference "0.00"
* ledger 0000000000683580.processedTimestamp: legacy "2022-07-05T10:11:12.000000", reference null
* rejections: legacy [0000000040455859:102, 0000000108402349:102, 0000000111054243:102], reference [0000000000683580:104, 0000000040455859:102, 0000000108402349:102, 0000000111054243:102]

## report-01: CBTRN03C 'CardDemo report'

* explained by D-2 (the legacy omits the last account total and adds the last amount twice)
* account totals: legacy [1472.64, 3256.45, 1606.21, 1564.76, 1624.11, 1822.02, -132.88, 855.95, 2005.51, 1549.14, 2388.57, 2424.81, 1646.98, 1010.87, 1672.86, 2600.51, 1743.03, 2855.04, 2148.49, 1899.30, 723.29, 857.98, 3187.12, 2581.92, 1032.63, 1690.36, 853.69, 3774.56, 995.79, -900.53, 743.89, 1092.71, 2986.80, 310.40, 2731.91, 445.22, 2358.27, 2092.86, 1353.89, 2486.87, 1166.86, 1069.49, 1058.26, 1488.13, 1562.68, 702.63, -650.27, 2683.89, 1108.66], reference [1472.64, 3256.45, 1606.21, 1564.76, 1624.11, 1822.02, -132.88, 855.95, 2005.51, 1549.14, 2388.57, 2424.81, 1646.98, 1010.87, 1672.86, 2600.51, 1743.03, 2855.04, 2148.49, 1899.30, 723.29, 857.98, 3187.12, 2581.92, 1032.63, 1690.36, 853.69, 3774.56, 995.79, -900.53, 743.89, 1092.71, 2986.80, 310.40, 2731.91, 445.22, 2358.27, 2092.86, 1353.89, 2486.87, 1166.86, 1069.49, 1058.26, 1488.13, 1562.68, 702.63, -650.27, 2683.89, 1108.66, 1629.53]
* grand total: legacy 79254.29, reference 79233.86 (reference + last amount = 79254.29)

## report-02: CBTRN03C 'one transaction'

* explained by D-2 (the legacy omits the last account total and adds the last amount twice)
* account totals: legacy [], reference [183.88]
* grand total: legacy 367.76, reference 183.88 (reference + last amount = 367.76)

## report-03: CBTRN03C 'seven transactions'

* explained by D-2 (the legacy omits the last account total and adds the last amount twice)
* account totals: legacy [], reference [1472.64]
* grand total: legacy 1491.41, reference 1472.64 (reference + last amount = 1491.41)

## report-04: CBTRN03C 'nineteen transactions'

* explained by D-2 (the legacy omits the last account total and adds the last amount twice)
* account totals: legacy [1472.64, 3256.45], reference [1472.64, 3256.45, 982.20]
* grand total: legacy 5876.28, reference 5711.29 (reference + last amount = 5876.28)

## report-05: CBTRN03C 'twenty transactions'

* explained by D-2 (the legacy omits the last account total and adds the last amount twice)
* account totals: legacy [1472.64, 3256.45], reference [1472.64, 3256.45, 1576.97]
* grand total: legacy 6900.83, reference 6306.06 (reference + last amount = 6900.83)

## report-06: CBTRN03C 'twenty-one transactions'

* explained by D-2 (the legacy omits the last account total and adds the last amount twice)
* account totals: legacy [1472.64, 3256.45], reference [1472.64, 3256.45, 1606.21]
* grand total: legacy 6364.54, reference 6335.30 (reference + last amount = 6364.54)

## report-07: CBTRN03C 'forty transactions'

* explained by D-2 (the legacy omits the last account total and adds the last amount twice)
* account totals: legacy [1472.64, 3256.45, 1606.21, 1564.76, 1624.11], reference [1472.64, 3256.45, 1606.21, 1564.76, 1624.11, 1397.43]
* grand total: legacy 11190.82, reference 10921.60 (reference + last amount = 11190.82)

## report-11: CBTRN03C 'negative amounts'

* explained by D-2 (the legacy omits the last account total and adds the last amount twice)
* account totals: legacy [1163.10, 3362.67, 2802.23, 2795.22, -146.77, 1714.24, 132.88, 537.05, 818.63, -824.68, 1263.47, 84.61, 978.58, -782.45, -685.12, 778.33, 2114.37, 683.72, 90.61, -63.54, 1850.89, -333.98, -427.30, -926.74, 34.25, -219.62, 1388.13, 385.20, -532.65, -959.41, 211.67, 1216.69, 3473.02, -697.52, 1354.17, -882.76, 3728.95, 351.58, -845.21, 481.11, 1347.32, -623.81, -652.50, 1097.25, -604.06, 2425.31, 1346.39, 701.23, -251.10], reference [1163.10, 3362.67, 2802.23, 2795.22, -146.77, 1714.24, 132.88, 537.05, 818.63, -824.68, 1263.47, 84.61, 978.58, -782.45, -685.12, 778.33, 2114.37, 683.72, 90.61, -63.54, 1850.89, -333.98, -427.30, -926.74, 34.25, -219.62, 1388.13, 385.20, -532.65, -959.41, 211.67, 1216.69, 3473.02, -697.52, 1354.17, -882.76, 3728.95, 351.58, -845.21, 481.11, 1347.32, -623.81, -652.50, 1097.25, -604.06, 2425.31, 1346.39, 701.23, -251.10, 765.99]
* grand total: legacy 31040.07, reference 31019.64 (reference + last amount = 31040.07)

## report-12: CBTRN03C 'zero amounts'

* explained by D-2 (the legacy omits the last account total and adds the last amount twice)
* account totals: legacy [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], reference [0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00]
* grand total: none in the legacy report, reference 0.00

## report-13: CBTRN03C 'period boundaries are inclusive'

* explained by D-2 (the legacy omits the last account total and adds the last amount twice)
* account totals: legacy [1472.64, 3256.45, 1606.21, 1564.76, 1624.11, 1822.02, -132.88, 855.95, 2005.51, 1549.14, 2388.57, 2424.81, 1646.98, 1010.87, 1672.86, 2600.51, 1743.03, 2855.04, 2148.49, 1899.30, 723.29, 857.98, 3187.12, 2581.92, 1032.63, 1690.36, 853.69, 3774.56, 995.79, -900.53, 743.89, 1092.71, 2986.80, 310.40, 2731.91, 445.22, 2358.27, 2092.86, 1353.89, 2486.87, 1166.86, 1069.49, 1058.26, 1488.13, 1562.68, 702.63, -650.27, 2683.89, 1108.66], reference [1472.64, 3256.45, 1606.21, 1564.76, 1624.11, 1822.02, -132.88, 855.95, 2005.51, 1549.14, 2388.57, 2424.81, 1646.98, 1010.87, 1672.86, 2600.51, 1743.03, 2855.04, 2148.49, 1899.30, 723.29, 857.98, 3187.12, 2581.92, 1032.63, 1690.36, 853.69, 3774.56, 995.79, -900.53, 743.89, 1092.71, 2986.80, 310.40, 2731.91, 445.22, 2358.27, 2092.86, 1353.89, 2486.87, 1166.86, 1069.49, 1058.26, 1488.13, 1562.68, 702.63, -650.27, 2683.89, 1108.66, 1629.53]
* grand total: legacy 79254.29, reference 79233.86 (reference + last amount = 79254.29)

## report-14: CBTRN03C 'blank processed timestamp ends the report (Q-01)'

* explained by D-3 (the legacy stops at the first transaction outside the period) and D-2
* line 5: only in the reference 0000000838587312
* line 6: only in the reference 2022071800000050
* line 7: only in the reference 0000000130111733
* line 8: only in the reference 0000000187573156
* line 9: only in the reference 0000000486159054
* line 10: only in the reference 0000000802663079
* account totals: legacy [], reference [1377.87, 3256.45, 1606.21, 1564.76, 1624.11, 1822.02, -132.88, 855.95, 2005.51, 1549.14, 2388.57, 2424.81, 1646.98, 1010.87, 1672.86, 2600.51, 1743.03, 2855.04, 2148.49, 1899.30, 723.29, 857.98, 3187.12, 2581.92, 1032.63, 1690.36, 853.69, 3774.56, 995.79, -900.53, 743.89, 1092.71, 2986.80, 310.40, 2731.91, 445.22, 2358.27, 2092.86, 1353.89, 2486.87, 1166.86, 1069.49, 1058.26, 1488.13, 1562.68, 702.63, -650.27, 2683.89, 1108.66, 1629.53]
* grand total: none in the legacy report, reference 79139.09

## report-15: CBTRN03C 'unknown card (abend)'

* explained by the legacy abend on missing reference data (the service throws ReferenceDataMissing, SPEC section 4)
* legacy abended (return code 16); the reference throws {"type":"ReferenceDataMissing","what":"card","key":"9999999999999999"}

## report-16: CBTRN03C 'unknown transaction type (abend)'

* explained by the legacy abend on missing reference data (the service throws ReferenceDataMissing, SPEC section 4)
* legacy abended (return code 16); the reference throws {"type":"ReferenceDataMissing","what":"transaction type","key":"99"}

## report-17: CBTRN03C 'unknown category (abend)'

* explained by the legacy abend on missing reference data (the service throws ReferenceDataMissing, SPEC section 4)
* legacy abended (return code 16); the reference throws {"type":"ReferenceDataMissing","what":"transaction category","key":"019999"}

