       IDENTIFICATION DIVISION.
       PROGRAM-ID. PARITY12.
      * Intrinsic functions: TEST-NUMVAL, TEST-NUMVAL-C, NUMVAL and
      * NUMVAL-C over valid and invalid arguments (GnuCOBOL's lenient
      * NUMVAL: first sign, CR/DB anywhere, later points ignored);
      * ORD, CHAR, ORD-MAX, ORD-MIN, SUM, MEAN, MEDIAN, RANGE, SQRT,
      * FACTORIAL, INTEGER-OF-DAY, DAY-OF-INTEGER, TEST-DATE-YYYYMMDD,
      * YEAR-TO-YYYY, DATE-TO-YYYYMMDD and WHEN-COMPILED's shape, each
      * stored into a field (DISPLAY of a numeric function result
      * itself is a frontier: GnuCOBOL shows its own temporary).
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01 R                  PIC -(9)9.9(6).
       01 S                  PIC S9(9)V9(6).
       01 P                  PIC 9(4).
       01 I                  PIC 99.
       01 CHR                PIC X.
       01 WC                 PIC X(21).
       01 VALS.
          05 FILLER PIC X(12) VALUE ' -12.5 '.
          05 FILLER PIC X(12) VALUE '12A3'.
          05 FILLER PIC X(12) VALUE '1 2'.
          05 FILLER PIC X(12) VALUE 'ABC'.
          05 FILLER PIC X(12) VALUE SPACES.
          05 FILLER PIC X(12) VALUE '+-1'.
          05 FILLER PIC X(12) VALUE '12.5CR'.
          05 FILLER PIC X(12) VALUE '12.5cr'.
          05 FILLER PIC X(12) VALUE '$1,234.50'.
          05 FILLER PIC X(12) VALUE '- 7.25'.
          05 FILLER PIC X(12) VALUE '.5'.
          05 FILLER PIC X(12) VALUE '5.'.
          05 FILLER PIC X(12) VALUE '1.2.3'.
          05 FILLER PIC X(12) VALUE '+ 3 DB'.
          05 FILLER PIC X(12) VALUE '12-'.
          05 FILLER PIC X(12) VALUE '1,2'.
          05 FILLER PIC X(12) VALUE '12 -'.
          05 FILLER PIC X(12) VALUE '- 12'.
          05 FILLER PIC X(12) VALUE '-12-'.
          05 FILLER PIC X(12) VALUE '12CR5'.
          05 FILLER PIC X(12) VALUE '.'.
          05 FILLER PIC X(12) VALUE '+'.
          05 FILLER PIC X(12) VALUE '-'.
          05 FILLER PIC X(12) VALUE 'CR'.
          05 FILLER PIC X(12) VALUE '12 CR'.
          05 FILLER PIC X(12) VALUE '12DB '.
          05 FILLER PIC X(12) VALUE '+.5'.
          05 FILLER PIC X(12) VALUE '$12'.
          05 FILLER PIC X(12) VALUE '$ 12'.
          05 FILLER PIC X(12) VALUE '-$12'.
          05 FILLER PIC X(12) VALUE '$-12'.
          05 FILLER PIC X(12) VALUE '12$'.
          05 FILLER PIC X(12) VALUE '1,234'.
          05 FILLER PIC X(12) VALUE ',123'.
          05 FILLER PIC X(12) VALUE '12,'.
          05 FILLER PIC X(12) VALUE '1,,2'.
          05 FILLER PIC X(12) VALUE '1.234,5'.
          05 FILLER PIC X(12) VALUE '0'.
          05 FILLER PIC X(12) VALUE '00012'.
          05 FILLER PIC X(12) VALUE ' 12 34'.
          05 FILLER PIC X(12) VALUE '+12+'.
          05 FILLER PIC X(12) VALUE '12+'.
          05 FILLER PIC X(12) VALUE '1D'.
          05 FILLER PIC X(12) VALUE 'X'.
          05 FILLER PIC X(12) VALUE '12.'.
          05 FILLER PIC X(12) VALUE '  .  '.
          05 FILLER PIC X(12) VALUE '1e5'.
          05 FILLER PIC X(12) VALUE '1-2'.
          05 FILLER PIC X(12) VALUE '9999999999'.
          05 FILLER PIC X(12) VALUE '-0.0'.
       01 VT REDEFINES VALS.
          05 V PIC X(12) OCCURS 50.
       01 T.
          05 TV              PIC S9(3)V9 OCCURS 5.
       PROCEDURE DIVISION.
       MAIN-PARA.
           PERFORM VARYING I FROM 1 BY 1 UNTIL I > 50
              COMPUTE S = FUNCTION NUMVAL (V (I))
              MOVE S TO R
              MOVE FUNCTION TEST-NUMVAL (V (I)) TO P
              DISPLAY 'N  [' V (I) '] ' R ' ' P
              COMPUTE S = FUNCTION NUMVAL-C (V (I))
              MOVE S TO R
              MOVE FUNCTION TEST-NUMVAL-C (V (I)) TO P
              DISPLAY 'NC [' V (I) '] ' R ' ' P
           END-PERFORM
           COMPUTE S = FUNCTION NUMVAL-C ('EUR1.5' 'EUR')
           MOVE S TO R
           DISPLAY 'EUR ' R
           MOVE FUNCTION ORD ('A') TO P
           DISPLAY 'ORD ' P
           MOVE FUNCTION CHAR (67) TO CHR
           DISPLAY 'CHAR ' CHR
           MOVE 1.5 TO TV (1) MOVE -2 TO TV (2) MOVE 10 TO TV (3)
           MOVE 3.3 TO TV (4) MOVE 0 TO TV (5)
           MOVE FUNCTION ORD-MAX (3 9 2 9) TO P
           DISPLAY 'ORD-MAX ' P
           MOVE FUNCTION ORD-MIN (TV (1) TV (2) TV (3) TV (5)) TO P
           DISPLAY 'ORD-MIN ' P
           COMPUTE S = FUNCTION SUM (TV (1) TV (2) TV (3) TV (4) 2.25)
           MOVE S TO R DISPLAY 'SUM ' R
           COMPUTE S = FUNCTION MEAN (1 2 4)
           MOVE S TO R DISPLAY 'MEAN ' R
           COMPUTE S = FUNCTION MEDIAN (5 1 3 2)
           MOVE S TO R DISPLAY 'MEDIAN4 ' R
           COMPUTE S = FUNCTION MEDIAN (TV (1) TV (2) TV (3))
           MOVE S TO R DISPLAY 'MEDIAN3 ' R
           COMPUTE S = FUNCTION RANGE (5 -1.5 3)
           MOVE S TO R DISPLAY 'RANGE ' R
           COMPUTE S = FUNCTION SQRT (2)
           MOVE S TO R DISPLAY 'SQRT ' R
           COMPUTE S ROUNDED = FUNCTION SQRT (10)
           MOVE S TO R DISPLAY 'SQRT10 ' R
           COMPUTE S = FUNCTION FACTORIAL (10)
           MOVE S TO R DISPLAY 'FACT ' R
           COMPUTE S = FUNCTION FACTORIAL (0)
           MOVE S TO R DISPLAY 'FACT0 ' R
           COMPUTE S = FUNCTION INTEGER-OF-DAY (2024060)
           MOVE S TO R DISPLAY 'IOD ' R
           COMPUTE S = FUNCTION INTEGER-OF-DAY (2023366)
           MOVE S TO R DISPLAY 'IOD-BAD ' R
           COMPUTE S = FUNCTION DAY-OF-INTEGER (150000)
           MOVE S TO R DISPLAY 'DOI ' R
           MOVE FUNCTION TEST-DATE-YYYYMMDD (20240230) TO P
           DISPLAY 'TD1 ' P
           MOVE FUNCTION TEST-DATE-YYYYMMDD (20241301) TO P
           DISPLAY 'TD2 ' P
           MOVE FUNCTION TEST-DATE-YYYYMMDD (15001201) TO P
           DISPLAY 'TD3 ' P
           MOVE FUNCTION TEST-DATE-YYYYMMDD (20240229) TO P
           DISPLAY 'TD4 ' P
           COMPUTE S = FUNCTION YEAR-TO-YYYY (50 50 2026)
           MOVE S TO R DISPLAY 'Y2Y ' R
           COMPUTE S = FUNCTION YEAR-TO-YYYY (77 50 2026)
           MOVE S TO R DISPLAY 'Y2Y77 ' R
           COMPUTE S = FUNCTION YEAR-TO-YYYY (5 -10 2026)
           MOVE S TO R DISPLAY 'Y2YN ' R
           COMPUTE S = FUNCTION DATE-TO-YYYYMMDD (991231 30 2026)
           MOVE S TO R DISPLAY 'D2Y ' R
           MOVE FUNCTION WHEN-COMPILED TO WC
           IF WC (1:2) = '20' AND WC (9:6) IS NUMERIC
              DISPLAY 'WHEN-COMPILED SHAPE OK'
           END-IF
           GOBACK.
