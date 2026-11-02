      * Overlapping MOVE shapes whose result does not depend on copy order:
      * identical alias, justified alias, truncation past the bytes read, disjoint views.
       IDENTIFICATION DIVISION.
       PROGRAM-ID. MOVESAFE.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01 PATTERN.
          05 FILLER PIC X(26) VALUE 'ABCDEFGHIJKLMNOPQRSTUVWXYZ'.
          05 FILLER PIC X(26) VALUE 'abcdefghijklmnopqrstuvwxyz'.
          05 FILLER PIC X(20) VALUE '0123456789<>?;:[]{}='.
       01 BUF PIC X(72).
       01 I1-S-V REDEFINES BUF.
          05 I1-S PIC X(4).
       01 I1-D-V REDEFINES BUF.
          05 I1-D PIC X(4).
       01 I2-S-V REDEFINES BUF.
          05 I2-S PIC X(4).
       01 I2-D-V REDEFINES BUF.
          05 I2-D PIC X(6).
       01 I3-S-V REDEFINES BUF.
          05 I3-S PIC X(6).
       01 I3-D-V REDEFINES BUF.
          05 I3-D PIC X(4).
       01 I4-S-V REDEFINES BUF.
          05 I4-S PIC X(4).
       01 I4-D-V REDEFINES BUF.
          05 FILLER PIC X(2).
          05 I4-D PIC X(2) JUSTIFIED RIGHT.
       01 I5-S-V REDEFINES BUF.
          05 I5-S.
             10 FILLER PIC X(4).
       01 I5-D-V REDEFINES BUF.
          05 I5-D.
             10 FILLER PIC X(4).
       01 O1-S-V REDEFINES BUF.
          05 O1-S PIC X(8).
       01 O1-D-V REDEFINES BUF.
          05 FILLER PIC X(6).
          05 O1-D PIC X(3).
       01 O2-S-V REDEFINES BUF.
          05 FILLER PIC X(6).
          05 O2-S PIC X(6).
       01 O2-D-V REDEFINES BUF.
          05 O2-D PIC X(3).
       01 D1-S-V REDEFINES BUF.
          05 D1-S PIC X(4).
       01 D1-D-V REDEFINES BUF.
          05 FILLER PIC X(4).
          05 D1-D PIC X(4).
       01 I PIC 99 VALUE 1.
       01 J PIC 99 VALUE 2.
       PROCEDURE DIVISION.
           MOVE PATTERN TO BUF
           MOVE I1-S TO I1-D
           DISPLAY 'I1:' BUF(1:6)
           MOVE PATTERN TO BUF
           MOVE I2-S TO I2-D
           DISPLAY 'I2:' BUF(1:8)
           MOVE PATTERN TO BUF
           MOVE I3-S TO I3-D
           DISPLAY 'I3:' BUF(1:8)
           MOVE PATTERN TO BUF
           MOVE I4-S TO I4-D
           DISPLAY 'I4:' BUF(1:6)
           MOVE PATTERN TO BUF
           MOVE I5-S TO I5-D
           DISPLAY 'I5:' BUF(1:6)
           MOVE PATTERN TO BUF
           MOVE O1-S TO O1-D
           DISPLAY 'O1:' BUF(1:11)
           MOVE PATTERN TO BUF
           MOVE O2-S TO O2-D
           DISPLAY 'O2:' BUF(1:14)
           MOVE PATTERN TO BUF
           MOVE D1-S TO D1-D
           DISPLAY 'D1:' BUF(1:10)
           MOVE PATTERN TO BUF
           MOVE BUF(1:4) TO BUF(1:4)
           DISPLAY 'R5:' BUF(1:6)
           MOVE PATTERN TO BUF
           MOVE BUF(5:4) TO BUF(1:3)
           DISPLAY 'R6:' BUF(1:9)
           GOBACK.
