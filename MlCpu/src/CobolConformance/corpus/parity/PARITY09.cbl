       IDENTIFICATION DIVISION.
       PROGRAM-ID. PARITY09.
      * INITIALIZE with options: REPLACING category [DATA] BY value
      * (only that category changes), WITH FILLER, ALL TO VALUE, THEN
      * TO DEFAULT, an elementary target; a table whose entries are
      * written whole. And GnuCOBOL's default INITIALIZE of a signed
      * zoned item: plain '0' digits, no sign overpunch ("00").
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01 G.
          05 A               PIC X(3) VALUE 'aaa'.
          05 N               PIC 9(3) VALUE 123.
          05 FILLER          PIC X(2) VALUE 'ff'.
          05 E               PIC ZZ9 VALUE ' 45'.
          05 B               PIC A(2) VALUE 'bb'.
          05 S               PIC S9(2) VALUE -12.
          05 T OCCURS 3.
             10 TN           PIC 9 VALUE 7.
             10 TP           PIC S9 COMP-3 VALUE -1.
          05 TX              PIC X OCCURS 2 VALUE 'x'.
       01 GX REDEFINES G     PIC X(26).
       01 V.
          05 VA              PIC X(3) VALUE 'vvv'.
          05 VN              PIC 9(3) VALUE 9.
          05 FILLER          PIC X(2) VALUE 'ff'.
          05 VS              PIC S9(2) SIGN LEADING SEPARATE.
       01 W-REP              PIC X(2) VALUE 'RR'.
       01 W-NUM              PIC S9(4) VALUE -77.
       01 W-NUMX REDEFINES W-NUM PIC X(4).
       PROCEDURE DIVISION.
           DISPLAY '0 [' GX (1:16) ']' TX (1) TX (2)
           INITIALIZE G REPLACING NUMERIC DATA BY 42
           DISPLAY '1 [' GX (1:16) '] ' TN (1) TN (2) TN (3)
                   ' ' TP (1) ' ' TP (3)
           INITIALIZE G REPLACING ALPHANUMERIC BY W-REP
           DISPLAY '2 [' GX (1:16) '] ' TX (1) TX (2)
           INITIALIZE G REPLACING ALPHABETIC BY 'R'
                                  NUMERIC-EDITED BY 7
           DISPLAY '3 [' GX (1:16) ']'
           INITIALIZE G WITH FILLER REPLACING ALPHANUMERIC BY 'W'
           DISPLAY '4 [' GX (1:16) ']'
           INITIALIZE G ALL TO VALUE
           DISPLAY '5 [' GX (1:16) '] ' TN (2) ' ' TP (2) TX (2)
           MOVE ALL '*' TO G
           INITIALIZE G REPLACING NUMERIC BY 5 THEN TO DEFAULT
           DISPLAY '6 [' GX (1:16) '] ' TN (2) ' ' TP (2) TX (2)
           MOVE ALL '*' TO V
           INITIALIZE V WITH FILLER ALL TO VALUE THEN TO DEFAULT
           DISPLAY '7 [' V ']'
           MOVE ALL '*' TO V
           INITIALIZE V ALL TO VALUE
           DISPLAY '8 [' V ']'
           INITIALIZE W-NUM REPLACING ALPHANUMERIC BY 'Z'
           DISPLAY '9 [' W-NUMX ']'
           INITIALIZE W-NUM REPLACING NUMERIC BY -3
           DISPLAY '10 [' W-NUMX ']'
           INITIALIZE W-NUM
           DISPLAY '11 [' W-NUMX ']'
           MOVE -5 TO S
           INITIALIZE G
           DISPLAY '12 [' GX (1:16) ']'
           GOBACK.
