# Worked examples: CBTRN03C (traced from the faithful program)

Runs of the faithful translation of CBTRN03C on an illustrative input (not one of the acceptance scenarios): 33
transactions of 5 cards, whole card groups taken from the middle of the CardDemo transaction file (already sorted
by card number, as the TRANREPT job's SORT step leaves it). Lookup files are the CardDemo ones. DATEPARM is
«2022-01-01 2022-07-06»: reporting period 2022-01-01 to 2022-07-06. The storage default byte (ctx.storageDefault()) is x'00'.

Raw bytes are shown between « and » (ISO-8859-1). Report lines are 133 bytes; each is shown whole, trailing spaces
included.

## Run 1: every transaction inside the period

Return code: 0

Transactions, in input order, with the lookups the program makes (cross-reference only when the card number changes):

- 1. TRAN-ID «0000000085824369», TRAN-CARD-NUM «3999169246375885» (new card: XREF-ACCT-ID 00000000003), TRAN-TYPE-CD «01» -> TRAN-TYPE-DESC «Purchase», TRAN-CAT-CD «0001» -> TRAN-CAT-TYPE-DESC «Regular Sales Draft», TRAN-SOURCE «POS TERM  », TRAN-AMT «0000009997G» = 999.77, TRAN-PROC-TS «2022-07-05-10.11.12.000000»
- 2. TRAN-ID «0000000375247552», TRAN-CARD-NUM «3999169246375885», TRAN-TYPE-CD «01» -> TRAN-TYPE-DESC «Purchase», TRAN-CAT-CD «0001» -> TRAN-CAT-TYPE-DESC «Regular Sales Draft», TRAN-SOURCE «POS TERM  », TRAN-AMT «0000001151A» = 115.11, TRAN-PROC-TS «2022-07-05-10.11.12.000000»
- 3. TRAN-ID «0000000540260014», TRAN-CARD-NUM «3999169246375885», TRAN-TYPE-CD «03» -> TRAN-TYPE-DESC «Credit», TRAN-CAT-CD «0001» -> TRAN-CAT-TYPE-DESC «Credit to Account», TRAN-SOURCE «OPERATOR  », TRAN-AMT «0000008802K» = -880.22, TRAN-PROC-TS «2022-07-05-10.11.12.000000»
- 4. TRAN-ID «0000000641694180», TRAN-CARD-NUM «3999169246375885», TRAN-TYPE-CD «01» -> TRAN-TYPE-DESC «Purchase», TRAN-CAT-CD «0001» -> TRAN-CAT-TYPE-DESC «Regular Sales Draft», TRAN-SOURCE «POS TERM  », TRAN-AMT «0000005542B» = 554.22, TRAN-PROC-TS «2022-07-05-10.11.12.000000»
- 5. TRAN-ID «0000000784621975», TRAN-CARD-NUM «3999169246375885», TRAN-TYPE-CD «01» -> TRAN-TYPE-DESC «Purchase», TRAN-CAT-CD «0001» -> TRAN-CAT-TYPE-DESC «Regular Sales Draft», TRAN-SOURCE «POS TERM  », TRAN-AMT «0000002272B» = 227.22, TRAN-PROC-TS «2022-07-05-10.11.12.000000»
- 6. TRAN-ID «0000000961714986», TRAN-CARD-NUM «3999169246375885», TRAN-TYPE-CD «01» -> TRAN-TYPE-DESC «Purchase», TRAN-CAT-CD «0001» -> TRAN-CAT-TYPE-DESC «Regular Sales Draft», TRAN-SOURCE «POS TERM  », TRAN-AMT «0000006945E» = 694.55, TRAN-PROC-TS «2022-07-05-10.11.12.000000»
- 7. TRAN-ID «2022071800000003», TRAN-CARD-NUM «3999169246375885», TRAN-TYPE-CD «01» -> TRAN-TYPE-DESC «Purchase», TRAN-CAT-CD «0005» -> TRAN-CAT-TYPE-DESC «Interest Amount», TRAN-SOURCE «System    », TRAN-AMT «0000000323H» = 32.38, TRAN-PROC-TS «2022-07-05-10.11.12.000000»
- 8. TRAN-ID «0000000016259484», TRAN-CARD-NUM «4011500891777367» (new card: XREF-ACCT-ID 00000000013), TRAN-TYPE-CD «03» -> TRAN-TYPE-DESC «Credit», TRAN-CAT-CD «0001» -> TRAN-CAT-TYPE-DESC «Credit to Account», TRAN-SOURCE «OPERATOR  », TRAN-AMT «0000000567P» = -56.77, TRAN-PROC-TS «2022-07-05-10.11.12.000000»
- 9. TRAN-ID «0000000201032783», TRAN-CARD-NUM «4011500891777367», TRAN-TYPE-CD «01» -> TRAN-TYPE-DESC «Purchase», TRAN-CAT-CD «0001» -> TRAN-CAT-TYPE-DESC «Regular Sales Draft», TRAN-SOURCE «POS TERM  », TRAN-AMT «0000003264D» = 326.44, TRAN-PROC-TS «2022-07-05-10.11.12.000000»
- 10. TRAN-ID «0000000547488622», TRAN-CARD-NUM «4011500891777367», TRAN-TYPE-CD «01» -> TRAN-TYPE-DESC «Purchase», TRAN-CAT-CD «0001» -> TRAN-CAT-TYPE-DESC «Regular Sales Draft», TRAN-SOURCE «POS TERM  », TRAN-AMT «0000004036F» = 403.66, TRAN-PROC-TS «2022-07-05-10.11.12.000000»
- 11. TRAN-ID «0000000840146978», TRAN-CARD-NUM «4011500891777367», TRAN-TYPE-CD «01» -> TRAN-TYPE-DESC «Purchase», TRAN-CAT-CD «0001» -> TRAN-CAT-TYPE-DESC «Regular Sales Draft», TRAN-SOURCE «POS TERM  », TRAN-AMT «0000007022B» = 702.22, TRAN-PROC-TS «2022-07-05-10.11.12.000000»
- 12. TRAN-ID «0000000909315074», TRAN-CARD-NUM «4011500891777367», TRAN-TYPE-CD «01» -> TRAN-TYPE-DESC «Purchase», TRAN-CAT-CD «0001» -> TRAN-CAT-TYPE-DESC «Regular Sales Draft», TRAN-SOURCE «POS TERM  », TRAN-AMT «0000007592B» = 759.22, TRAN-PROC-TS «2022-07-05-10.11.12.000000»
- 13. TRAN-ID «0000000926624843», TRAN-CARD-NUM «4011500891777367», TRAN-TYPE-CD «01» -> TRAN-TYPE-DESC «Purchase», TRAN-CAT-CD «0001» -> TRAN-CAT-TYPE-DESC «Regular Sales Draft», TRAN-SOURCE «POS TERM  », TRAN-AMT «0000006843C» = 684.33, TRAN-PROC-TS «2022-07-05-10.11.12.000000»
- 14. TRAN-ID «2022071800000013», TRAN-CARD-NUM «4011500891777367», TRAN-TYPE-CD «01» -> TRAN-TYPE-DESC «Purchase», TRAN-CAT-CD «0005» -> TRAN-CAT-TYPE-DESC «Interest Amount», TRAN-SOURCE «System    », TRAN-AMT «0000000359D» = 35.94, TRAN-PROC-TS «2022-07-05-10.11.12.000000»
- 15. TRAN-ID «0000000202217428», TRAN-CARD-NUM «4385271476627819» (new card: XREF-ACCT-ID 00000000034), TRAN-TYPE-CD «01» -> TRAN-TYPE-DESC «Purchase», TRAN-CAT-CD «0001» -> TRAN-CAT-TYPE-DESC «Regular Sales Draft», TRAN-SOURCE «POS TERM  », TRAN-AMT «0000002993C» = 299.33, TRAN-PROC-TS «2022-07-05-10.11.12.000000»
- 16. TRAN-ID «0000000392772234», TRAN-CARD-NUM «4385271476627819», TRAN-TYPE-CD «01» -> TRAN-TYPE-DESC «Purchase», TRAN-CAT-CD «0001» -> TRAN-CAT-TYPE-DESC «Regular Sales Draft», TRAN-SOURCE «POS TERM  », TRAN-AMT «0000003516F» = 351.66, TRAN-PROC-TS «2022-07-05-10.11.12.000000»
- 17. TRAN-ID «0000000482116790», TRAN-CARD-NUM «4385271476627819», TRAN-TYPE-CD «01» -> TRAN-TYPE-DESC «Purchase», TRAN-CAT-CD «0001» -> TRAN-CAT-TYPE-DESC «Regular Sales Draft», TRAN-SOURCE «POS TERM  », TRAN-AMT «0000008201A» = 820.11, TRAN-PROC-TS «2022-07-05-10.11.12.000000»
- 18. TRAN-ID «0000000676149118», TRAN-CARD-NUM «4385271476627819», TRAN-TYPE-CD «01» -> TRAN-TYPE-DESC «Purchase», TRAN-CAT-CD «0001» -> TRAN-CAT-TYPE-DESC «Regular Sales Draft», TRAN-SOURCE «POS TERM  », TRAN-AMT «0000006974D» = 697.44, TRAN-PROC-TS «2022-07-05-10.11.12.000000»
- 19. TRAN-ID «0000000772421231», TRAN-CARD-NUM «4385271476627819», TRAN-TYPE-CD «01» -> TRAN-TYPE-DESC «Purchase», TRAN-CAT-CD «0001» -> TRAN-CAT-TYPE-DESC «Regular Sales Draft», TRAN-SOURCE «POS TERM  », TRAN-AMT «0000004053C» = 405.33, TRAN-PROC-TS «2022-07-05-10.11.12.000000»
- 20. TRAN-ID «0000000838796166», TRAN-CARD-NUM «4385271476627819», TRAN-TYPE-CD «03» -> TRAN-TYPE-DESC «Credit», TRAN-CAT-CD «0001» -> TRAN-CAT-TYPE-DESC «Credit to Account», TRAN-SOURCE «OPERATOR  », TRAN-AMT «0000004575N» = -457.55, TRAN-PROC-TS «2022-07-05-10.11.12.000000»
- 21. TRAN-ID «2022071800000034», TRAN-CARD-NUM «4385271476627819», TRAN-TYPE-CD «01» -> TRAN-TYPE-DESC «Purchase», TRAN-CAT-CD «0005» -> TRAN-CAT-TYPE-DESC «Interest Amount», TRAN-SOURCE «System    », TRAN-AMT «0000000321G» = 32.17, TRAN-PROC-TS «2022-07-05-10.11.12.000000»
- 22. TRAN-ID «0000000054288996», TRAN-CARD-NUM «4534784102713951» (new card: XREF-ACCT-ID 00000000036), TRAN-TYPE-CD «01» -> TRAN-TYPE-DESC «Purchase», TRAN-CAT-CD «0001» -> TRAN-CAT-TYPE-DESC «Regular Sales Draft», TRAN-SOURCE «POS TERM  », TRAN-AMT «0000005026F» = 502.66, TRAN-PROC-TS «2022-07-05-10.11.12.000000»
- 23. TRAN-ID «0000000253685514», TRAN-CARD-NUM «4534784102713951», TRAN-TYPE-CD «01» -> TRAN-TYPE-DESC «Purchase», TRAN-CAT-CD «0001» -> TRAN-CAT-TYPE-DESC «Regular Sales Draft», TRAN-SOURCE «POS TERM  », TRAN-AMT «0000004963C» = 496.33, TRAN-PROC-TS «2022-07-05-10.11.12.000000»
- 24. TRAN-ID «0000000499424514», TRAN-CARD-NUM «4534784102713951», TRAN-TYPE-CD «01» -> TRAN-TYPE-DESC «Purchase», TRAN-CAT-CD «0001» -> TRAN-CAT-TYPE-DESC «Regular Sales Draft», TRAN-SOURCE «POS TERM  », TRAN-AMT «0000009525E» = 952.55, TRAN-PROC-TS «2022-07-05-10.11.12.000000»
- 25. TRAN-ID «0000000734452614», TRAN-CARD-NUM «4534784102713951», TRAN-TYPE-CD «01» -> TRAN-TYPE-DESC «Purchase», TRAN-CAT-CD «0001» -> TRAN-CAT-TYPE-DESC «Regular Sales Draft», TRAN-SOURCE «POS TERM  », TRAN-AMT «0000003582B» = 358.22, TRAN-PROC-TS «2022-07-05-10.11.12.000000»
- 26. TRAN-ID «0000000806255008», TRAN-CARD-NUM «4534784102713951», TRAN-TYPE-CD «03» -> TRAN-TYPE-DESC «Credit», TRAN-CAT-CD «0001» -> TRAN-CAT-TYPE-DESC «Credit to Account», TRAN-SOURCE «OPERATOR  », TRAN-AMT «0000004393L» = -439.33, TRAN-PROC-TS «2022-07-05-10.11.12.000000»
- 27. TRAN-ID «2022071800000036», TRAN-CARD-NUM «4534784102713951», TRAN-TYPE-CD «01» -> TRAN-TYPE-DESC «Purchase», TRAN-CAT-CD «0005» -> TRAN-CAT-TYPE-DESC «Interest Amount», TRAN-SOURCE «System    », TRAN-AMT «0000000288G» = 28.87, TRAN-PROC-TS «2022-07-05-10.11.12.000000»
- 28. TRAN-ID «0000000000683580», TRAN-CARD-NUM «4859452612877065» (new card: XREF-ACCT-ID 00000000007), TRAN-TYPE-CD «01» -> TRAN-TYPE-DESC «Purchase», TRAN-CAT-CD «0001» -> TRAN-CAT-TYPE-DESC «Regular Sales Draft», TRAN-SOURCE «POS TERM  », TRAN-AMT «0000005047G» = 504.77, TRAN-PROC-TS «2022-07-05-10.11.12.000000»
- 29. TRAN-ID «0000000380632461», TRAN-CARD-NUM «4859452612877065», TRAN-TYPE-CD «01» -> TRAN-TYPE-DESC «Purchase», TRAN-CAT-CD «0001» -> TRAN-CAT-TYPE-DESC «Regular Sales Draft», TRAN-SOURCE «POS TERM  », TRAN-AMT «0000004283C» = 428.33, TRAN-PROC-TS «2022-07-05-10.11.12.000000»
- 30. TRAN-ID «0000000500885895», TRAN-CARD-NUM «4859452612877065», TRAN-TYPE-CD «03» -> TRAN-TYPE-DESC «Credit», TRAN-CAT-CD «0001» -> TRAN-CAT-TYPE-DESC «Credit to Account», TRAN-SOURCE «OPERATOR  », TRAN-AMT «0000005798Q» = -579.88, TRAN-PROC-TS «2022-07-05-10.11.12.000000»
- 31. TRAN-ID «0000000781512834», TRAN-CARD-NUM «4859452612877065», TRAN-TYPE-CD «01» -> TRAN-TYPE-DESC «Purchase», TRAN-CAT-CD «0001» -> TRAN-CAT-TYPE-DESC «Regular Sales Draft», TRAN-SOURCE «POS TERM  », TRAN-AMT «0000003190{» = 319.00, TRAN-PROC-TS «2022-07-05-10.11.12.000000»
- 32. TRAN-ID «0000000821287727», TRAN-CARD-NUM «4859452612877065», TRAN-TYPE-CD «01» -> TRAN-TYPE-DESC «Purchase», TRAN-CAT-CD «0001» -> TRAN-CAT-TYPE-DESC «Regular Sales Draft», TRAN-SOURCE «POS TERM  », TRAN-AMT «0000000349I» = 34.99, TRAN-PROC-TS «2022-07-05-10.11.12.000000»
- 33. TRAN-ID «2022071800000007», TRAN-CARD-NUM «4859452612877065», TRAN-TYPE-CD «01» -> TRAN-TYPE-DESC «Purchase», TRAN-CAT-CD «0005» -> TRAN-CAT-TYPE-DESC «Interest Amount», TRAN-SOURCE «System    », TRAN-AMT «0000000160H» = 16.08, TRAN-PROC-TS «2022-07-05-10.11.12.000000»

TRANREPT of run 1 (54 lines). Columns: file line, kind, WS-LINE-COUNTER after the line, the 133-byte line:

```
  1 title           1 DALYREPT                              Daily Transaction Report                 Date Range: 2022-01-01 to 2022-07-06                  
  2 blank           2                                                                                                                                      
  3 headings        3 Transaction ID   Account ID  Transaction Type   Tran Category                      Tran Source            Amount                     
  4 rule            4 -------------------------------------------------------------------------------------------------------------------------------------
  5 detail          5 0000000085824369 00000000003 01-Purchase        0001-Regular Sales Draft           POS TERM               999.77                     
  6 detail          6 0000000375247552 00000000003 01-Purchase        0001-Regular Sales Draft           POS TERM               115.11                     
  7 detail          7 0000000540260014 00000000003 03-Credit          0001-Credit to Account             OPERATOR      -        880.22                     
  8 detail          8 0000000641694180 00000000003 01-Purchase        0001-Regular Sales Draft           POS TERM               554.22                     
  9 detail          9 0000000784621975 00000000003 01-Purchase        0001-Regular Sales Draft           POS TERM               227.22                     
 10 detail         10 0000000961714986 00000000003 01-Purchase        0001-Regular Sales Draft           POS TERM               694.55                     
 11 detail         11 2022071800000003 00000000003 01-Purchase        0005-Interest Amount               System                  32.38                     
 12 account total  12 Account Total....................................................................................+      1,743.03                     
 13 rule           13 -------------------------------------------------------------------------------------------------------------------------------------
 14 detail         14 0000000016259484 00000000013 03-Credit          0001-Credit to Account             OPERATOR      -         56.77                     
 15 detail         15 0000000201032783 00000000013 01-Purchase        0001-Regular Sales Draft           POS TERM               326.44                     
 16 detail         16 0000000547488622 00000000013 01-Purchase        0001-Regular Sales Draft           POS TERM               403.66                     
 17 detail         17 0000000840146978 00000000013 01-Purchase        0001-Regular Sales Draft           POS TERM               702.22                     
 18 detail         18 0000000909315074 00000000013 01-Purchase        0001-Regular Sales Draft           POS TERM               759.22                     
 19 detail         19 0000000926624843 00000000013 01-Purchase        0001-Regular Sales Draft           POS TERM               684.33                     
 20 detail         20 2022071800000013 00000000013 01-Purchase        0005-Interest Amount               System                  35.94                     
 21 account total  21 Account Total....................................................................................+      2,855.04                     
 22 rule           22 -------------------------------------------------------------------------------------------------------------------------------------
 23 detail         23 0000000202217428 00000000034 01-Purchase        0001-Regular Sales Draft           POS TERM               299.33                     
 24 detail         24 0000000392772234 00000000034 01-Purchase        0001-Regular Sales Draft           POS TERM               351.66                     
 25 detail         25 0000000482116790 00000000034 01-Purchase        0001-Regular Sales Draft           POS TERM               820.11                     
 26 detail         26 0000000676149118 00000000034 01-Purchase        0001-Regular Sales Draft           POS TERM               697.44                     
 27 detail         27 0000000772421231 00000000034 01-Purchase        0001-Regular Sales Draft           POS TERM               405.33                     
 28 detail         28 0000000838796166 00000000034 03-Credit          0001-Credit to Account             OPERATOR      -        457.55                     
 29 detail         29 2022071800000034 00000000034 01-Purchase        0005-Interest Amount               System                  32.17                     
 30 account total  30 Account Total....................................................................................+      2,148.49                     
 31 rule           31 -------------------------------------------------------------------------------------------------------------------------------------
 32 detail         32 0000000054288996 00000000036 01-Purchase        0001-Regular Sales Draft           POS TERM               502.66                     
 33 detail         33 0000000253685514 00000000036 01-Purchase        0001-Regular Sales Draft           POS TERM               496.33                     
 34 detail         34 0000000499424514 00000000036 01-Purchase        0001-Regular Sales Draft           POS TERM               952.55                     
 35 detail         35 0000000734452614 00000000036 01-Purchase        0001-Regular Sales Draft           POS TERM               358.22                     
 36 detail         36 0000000806255008 00000000036 03-Credit          0001-Credit to Account             OPERATOR      -        439.33                     
 37 detail         37 2022071800000036 00000000036 01-Purchase        0005-Interest Amount               System                  28.87                     
 38 account total  38 Account Total....................................................................................+      1,899.30                     
 39 rule           39 -------------------------------------------------------------------------------------------------------------------------------------
 40 detail         40 0000000000683580 00000000007 01-Purchase        0001-Regular Sales Draft           POS TERM               504.77                     
 41 page total     41 Page Total ......................................................................................+      9,150.63                     
 42 rule           42 -------------------------------------------------------------------------------------------------------------------------------------
 43 title          43 DALYREPT                              Daily Transaction Report                 Date Range: 2022-01-01 to 2022-07-06                  
 44 blank          44                                                                                                                                      
 45 headings       45 Transaction ID   Account ID  Transaction Type   Tran Category                      Tran Source            Amount                     
 46 rule           46 -------------------------------------------------------------------------------------------------------------------------------------
 47 detail         47 0000000380632461 00000000007 01-Purchase        0001-Regular Sales Draft           POS TERM               428.33                     
 48 detail         48 0000000500885895 00000000007 03-Credit          0001-Credit to Account             OPERATOR      -        579.88                     
 49 detail         49 0000000781512834 00000000007 01-Purchase        0001-Regular Sales Draft           POS TERM               319.00                     
 50 detail         50 0000000821287727 00000000007 01-Purchase        0001-Regular Sales Draft           POS TERM                34.99                     
 51 detail         51 2022071800000007 00000000007 01-Purchase        0005-Interest Amount               System                  16.08                     
 52 page total     52 Page Total ......................................................................................+        234.60                     
 53 rule           53 -------------------------------------------------------------------------------------------------------------------------------------
 54 grand total     - Grand Total......................................................................................+      9,385.23                     
```

How to read it (each point checked against this output):

- The title, blank line, column headings and rule are written before the first detail line; every report line except the grand total adds 1 to WS-LINE-COUNTER.
- The page check runs only before a detail line: when WS-LINE-COUNTER is then a multiple of 20, a page total and a rule are written, then the four heading lines again, and the page total restarts at 0 (file line 41: page total 9150.63 = the details since the previous page break). When the counter reaches a multiple of 20 on a line that is followed by an account total or rule instead of a detail, it has moved past the multiple by the time the next detail is checked, so no page break happens there (file line 20).
- When the card number changes (not before the first card), an account total and a rule are written before the next detail line, and the account total restarts at 0.
- At end of input (quirk Q-02) the program displays «TRAN-AMT » and «WS-PAGE-TOTAL» with the last record's amount and the running page total, adds the last amount (16.08) to the page total a second time, writes the page total (234.60 = the last page's details + 16.08 again) and a rule, then the grand total (9385.23 = the sum of every page total written). The last card's account total is never written.

SYSOUT of run 1 (each transaction is displayed as its 350-byte record as it is read; the two lines before the end come from the end-of-file branch):

```
START OF EXECUTION OF PROGRAM CBTRN03C
Reporting from 2022-01-01 to 2022-07-06
0000000085824369010001POS TERM  Purchase at Pollich-Mosciski                                                                        0000009997G800000000Pollich-Mosciski                                  Georgettemouth                                    85890     39991692463758852022-06-10 19:27:53.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
0000000375247552010001POS TERM  Purchase at Guann Group                                                                             0000001151A800000000Guann Group                                       Port Grant                                        76360-645739991692463758852022-06-10 19:27:53.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
0000000540260014030001OPERATOR  Return item at McCullough-Gottlieb                                                                  0000008802K800000000McCullough-Gottlieb                               Clarissaside                                      80982-407239991692463758852022-06-10 19:27:53.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
0000000641694180010001POS TERM  Purchase at Schumm-Reinger                                                                          0000005542B800000000Schumm-Reinger                                    Antoniatown                                       52581     39991692463758852022-06-10 19:27:53.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
0000000784621975010001POS TERM  Purchase at Kunde-Howe                                                                              0000002272B800000000Kunde-Howe                                        New Darylberg                                     34409     39991692463758852022-06-10 19:27:53.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
0000000961714986010001POS TERM  Purchase at West and Sons                                                                           0000006945E800000000West and Sons                                     Lawrencefort                                      06664-609039991692463758852022-06-10 19:27:53.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
2022071800000003010005System    Int. for a/c 00000000003\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x000000000323H000000000                                                                                                              39991692463758852022-07-05-10.11.12.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
0000000016259484030001OPERATOR  Return item at Sipes Inc                                                                            0000000567P800000000Sipes Inc                                         Emilioside                                        93329     40115008917773672022-06-10 19:27:53.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
0000000201032783010001POS TERM  Purchase at Heidenreich-Feil                                                                        0000003264D800000000Heidenreich-Feil                                  North Christybury                                 32759     40115008917773672022-06-10 19:27:53.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
0000000547488622010001POS TERM  Purchase at Reichert, Kemmer and Funk                                                               0000004036F800000000Reichert, Kemmer and Funk                         North Destinibury                                 84879     40115008917773672022-06-10 19:27:53.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
0000000840146978010001POS TERM  Purchase at Gutkowski-Bayer                                                                         0000007022B800000000Gutkowski-Bayer                                   Baileyville                                       48332-191340115008917773672022-06-10 19:27:53.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
0000000909315074010001POS TERM  Purchase at Abbott and Sons                                                                         0000007592B800000000Abbott and Sons                                   East Cydney                                       03808-746840115008917773672022-06-10 19:27:53.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
0000000926624843010001POS TERM  Purchase at Schowalter, Pagac and Welch                                                             0000006843C800000000Schowalter, Pagac and Welch                       West Isacton                                      46573-235540115008917773672022-06-10 19:27:53.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
2022071800000013010005System    Int. for a/c 00000000013\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x000000000359D000000000                                                                                                              40115008917773672022-07-05-10.11.12.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
0000000202217428010001POS TERM  Purchase at Simonis and Sons                                                                        0000002993C800000000Simonis and Sons                                  Joanieview                                        81755-548943852714766278192022-06-10 19:27:53.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
0000000392772234010001POS TERM  Purchase at Williamson LLC                                                                          0000003516F800000000Williamson LLC                                    Runteville                                        18400-684543852714766278192022-06-10 19:27:53.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
0000000482116790010001POS TERM  Purchase at Erdman-Cartwright                                                                       0000008201A800000000Erdman-Cartwright                                 Lake Lavonne                                      06930     43852714766278192022-06-10 19:27:53.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
0000000676149118010001POS TERM  Purchase at Sauer-Ruecker                                                                           0000006974D800000000Sauer-Ruecker                                     Port Nestor                                       24148-989443852714766278192022-06-10 19:27:53.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
0000000772421231010001POS TERM  Purchase at Pfeffer, Rogahn and Hessel                                                              0000004053C800000000Pfeffer, Rogahn and Hessel                        Christborough                                     21176-442043852714766278192022-06-10 19:27:53.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
0000000838796166030001OPERATOR  Return item at Bauch-Crooks                                                                         0000004575N800000000Bauch-Crooks                                      Stokesberg                                        30306     43852714766278192022-06-10 19:27:53.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
2022071800000034010005System    Int. for a/c 00000000034\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x000000000321G000000000                                                                                                              43852714766278192022-07-05-10.11.12.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
0000000054288996010001POS TERM  Purchase at Cremin and Sons                                                                         0000005026F800000000Cremin and Sons                                   Bartonside                                        08677     45347841027139512022-06-10 19:27:53.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
0000000253685514010001POS TERM  Purchase at Von-Schmeler                                                                            0000004963C800000000Von-Schmeler                                      Lake Maximillian                                  85711     45347841027139512022-06-10 19:27:53.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
0000000499424514010001POS TERM  Purchase at Schumm-Stamm                                                                            0000009525E800000000Schumm-Stamm                                      Imogeneburgh                                      12605     45347841027139512022-06-10 19:27:53.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
0000000734452614010001POS TERM  Purchase at Stokes-Mueller                                                                          0000003582B800000000Stokes-Mueller                                    Ambroseland                                       19819-929845347841027139512022-06-10 19:27:53.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
0000000806255008030001OPERATOR  Return item at Wisoky, Jacobs and Sanford                                                           0000004393L800000000Wisoky, Jacobs and Sanford                        New Alanaview                                     05488-319545347841027139512022-06-10 19:27:53.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
2022071800000036010005System    Int. for a/c 00000000036\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x000000000288G000000000                                                                                                              45347841027139512022-07-05-10.11.12.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
0000000000683580010001POS TERM  Purchase at Abshire-Lowe                                                                            0000005047G800000000Abshire-Lowe                                      North Enoshaven                                   72112     48594526128770652022-06-10 19:27:53.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
0000000380632461010001POS TERM  Purchase at Beahan, Little and Sanford                                                              0000004283C800000000Beahan, Little and Sanford                        East Ebonyville                                   17826-099948594526128770652022-06-10 19:27:53.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
0000000500885895030001OPERATOR  Return item at Klein-Stark                                                                          0000005798Q800000000Klein-Stark                                       West Arlo                                         35478     48594526128770652022-06-10 19:27:53.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
0000000781512834010001POS TERM  Purchase at Koch-Pouros                                                                             0000003190{800000000Koch-Pouros                                       Daytonstad                                        13199-246348594526128770652022-06-10 19:27:53.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
0000000821287727010001POS TERM  Purchase at Russel LLC                                                                              0000000349I800000000Russel LLC                                        New Sarah                                         49041     48594526128770652022-06-10 19:27:53.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
2022071800000007010005System    Int. for a/c 00000000007\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x000000000160H000000000                                                                                                              48594526128770652022-07-05-10.11.12.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
TRAN-AMT 00000001608+
WS-PAGE-TOTAL00000021852+
END OF EXECUTION OF PROGRAM CBTRN03C
```

## Run 2: transaction 5 processed outside the period (quirk Q-01)

Same input, except TRAN-PROC-TS of transaction 5 is «2099-01-01-00.00.00.000000», after the period end 2022-07-06. The program's NEXT SENTENCE leaves the read loop at that record: it is not displayed, and the report ends after the 4 earlier detail lines with no page total, account total or grand total. Return code: 0.

TRANREPT of run 2 (8 lines):

```
DALYREPT                              Daily Transaction Report                 Date Range: 2022-01-01 to 2022-07-06                  
                                                                                                                                     
Transaction ID   Account ID  Transaction Type   Tran Category                      Tran Source            Amount                     
-------------------------------------------------------------------------------------------------------------------------------------
0000000085824369 00000000003 01-Purchase        0001-Regular Sales Draft           POS TERM               999.77                     
0000000375247552 00000000003 01-Purchase        0001-Regular Sales Draft           POS TERM               115.11                     
0000000540260014 00000000003 03-Credit          0001-Credit to Account             OPERATOR      -        880.22                     
0000000641694180 00000000003 01-Purchase        0001-Regular Sales Draft           POS TERM               554.22                     
```

SYSOUT of run 2:

```
START OF EXECUTION OF PROGRAM CBTRN03C
Reporting from 2022-01-01 to 2022-07-06
0000000085824369010001POS TERM  Purchase at Pollich-Mosciski                                                                        0000009997G800000000Pollich-Mosciski                                  Georgettemouth                                    85890     39991692463758852022-06-10 19:27:53.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
0000000375247552010001POS TERM  Purchase at Guann Group                                                                             0000001151A800000000Guann Group                                       Port Grant                                        76360-645739991692463758852022-06-10 19:27:53.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
0000000540260014030001OPERATOR  Return item at McCullough-Gottlieb                                                                  0000008802K800000000McCullough-Gottlieb                               Clarissaside                                      80982-407239991692463758852022-06-10 19:27:53.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
0000000641694180010001POS TERM  Purchase at Schumm-Reinger                                                                          0000005542B800000000Schumm-Reinger                                    Antoniatown                                       52581     39991692463758852022-06-10 19:27:53.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
END OF EXECUTION OF PROGRAM CBTRN03C
```

## Run 3: transaction 3 has a category that is not in TRANCATG

Same input, except TRAN-CAT-CD of transaction 3 is «9999». The lookup fails (INVALID KEY) and the program abends. Return code: 16 (ABEND 999). TRANREPT is empty (0 bytes), although the WRITE statements for the headings and the first 2 detail lines had already run: the abend ends the program before its CLOSE statements, so report records still in the output buffer are never written. Do not close or flush output files on the abend path. SYSOUT of run 3:

```
START OF EXECUTION OF PROGRAM CBTRN03C
Reporting from 2022-01-01 to 2022-07-06
0000000085824369010001POS TERM  Purchase at Pollich-Mosciski                                                                        0000009997G800000000Pollich-Mosciski                                  Georgettemouth                                    85890     39991692463758852022-06-10 19:27:53.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
0000000375247552010001POS TERM  Purchase at Guann Group                                                                             0000001151A800000000Guann Group                                       Port Grant                                        76360-645739991692463758852022-06-10 19:27:53.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
0000000540260014039999OPERATOR  Return item at McCullough-Gottlieb                                                                  0000008802K800000000McCullough-Gottlieb                               Clarissaside                                      80982-407239991692463758852022-06-10 19:27:53.0000002022-07-05-10.11.12.000000\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00
INVALID TRAN CATG KEY : 039999
FILE STATUS IS: NNNN0023
ABENDING PROGRAM
ABEND U0999
```

## Storage default

Run 1 with storage default x'20' (a space) produces exactly the same bytes: every report line is built from items with VALUE clauses or moved values.

## Record layout with VALUE clauses: copybook CVTRA07Y (verbatim)

The requirements packet lists these records' fields but not their VALUE clauses or the level-01 items without
subordinate fields; this is the copybook as compiled. Report lines are built from these records.

```cobol
      *****************************************************************
      *    Reporting data structure for transaction report
      *****************************************************************
       01  REPORT-NAME-HEADER.
           05  REPT-SHORT-NAME                  PIC X(38) VALUE
           'DALYREPT'.
           05  REPT-LONG-NAME                   PIC X(41) VALUE
           'Daily Transaction Report'.
           05  REPT-DATE-HEADER                 PIC X(12) VALUE
           'Date Range: '.
           05  REPT-START-DATE                  PIC X(10) VALUE SPACES.
           05  FILLER                           PIC X(04) VALUE ' to '.
           05  REPT-END-DATE                    PIC X(10) VALUE SPACES.

       01  TRANSACTION-DETAIL-REPORT.
           05  TRAN-REPORT-TRANS-ID             PIC X(16).
           05  FILLER                           PIC X(01) VALUE SPACES.
           05  TRAN-REPORT-ACCOUNT-ID           PIC X(11).
           05  FILLER                           PIC X(01) VALUE SPACES.
           05  TRAN-REPORT-TYPE-CD              PIC X(02).
           05  FILLER                           PIC X(01) VALUE '-'.
           05  TRAN-REPORT-TYPE-DESC            PIC X(15).
           05  FILLER                           PIC X(01) VALUE SPACES.
           05  TRAN-REPORT-CAT-CD               PIC 9(04).
           05  FILLER                           PIC X(01) VALUE '-'.
           05  TRAN-REPORT-CAT-DESC             PIC X(29).
           05  FILLER                           PIC X(01) VALUE SPACES.
           05  TRAN-REPORT-SOURCE               PIC X(10).
           05  FILLER                           PIC X(04) VALUE SPACES.
           05  TRAN-REPORT-AMT                  PIC -ZZZ,ZZZ,ZZZ.ZZ.
           05  FILLER                           PIC X(02) VALUE SPACES.

       01  TRANSACTION-HEADER-1.
           05  FILLER                           PIC X(17) VALUE
           'Transaction ID'.
           05  FILLER                           PIC X(12) VALUE
           'Account ID'.
           05  FILLER                           PIC X(19) VALUE
           'Transaction Type'.
           05  FILLER                           PIC X(35) VALUE
           'Tran Category'.
           05  FILLER                           PIC X(14) VALUE
           'Tran Source'.
           05  FILLER                           PIC X VALUE SPACES.
           05  FILLER                           PIC X(16) VALUE
           '        Amount'.

       01  TRANSACTION-HEADER-2  PIC X(133) VALUE ALL '-'.

       01  REPORT-PAGE-TOTALS.
           05  FILLER                           PIC X(11) VALUE
           'Page Total'.
           05  FILLER                           PIC X(86) VALUE ALL '.'.
           05  REPT-PAGE-TOTAL                  PIC +ZZZ,ZZZ,ZZZ.ZZ.

       01  REPORT-ACCOUNT-TOTALS.
           05  FILLER                           PIC X(13) VALUE
           'Account Total'.
           05  FILLER                           PIC X(84) VALUE ALL '.'.
           05  REPT-ACCOUNT-TOTAL               PIC +ZZZ,ZZZ,ZZZ.ZZ.

       01  REPORT-GRAND-TOTALS.
           05  FILLER                           PIC X(11) VALUE
           'Grand Total'.
           05  FILLER                           PIC X(86) VALUE ALL '.'.
           05  REPT-GRAND-TOTAL                 PIC +ZZZ,ZZZ,ZZZ.ZZ.




      *
      * Ver: CardDemo_v1.0-15-g27d6c6f-68 Date: 2022-07-19 23:16:01 CDT
      *
```
