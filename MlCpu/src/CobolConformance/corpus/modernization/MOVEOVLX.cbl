      * Raw overlapping MOVE: shifted shapes cobc -std=ibm routes through cob_move
      * (padding, JUSTIFIED). Oracle evidence only: no profile covers them.
       IDENTIFICATION DIVISION.
       PROGRAM-ID. MOVEOVLX.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01 PATTERN.
          05 FILLER PIC X(26) VALUE 'ABCDEFGHIJKLMNOPQRSTUVWXYZ'.
          05 FILLER PIC X(26) VALUE 'abcdefghijklmnopqrstuvwxyz'.
          05 FILLER PIC X(20) VALUE '0123456789<>?;:[]{}='.
       01 BUF PIC X(72).
       01 N6-S-V REDEFINES BUF.
          05 N6-S PIC X(3).
       01 N6-D-V REDEFINES BUF.
          05 FILLER PIC X(1).
          05 N6-D PIC X(4).
       01 N7-S-V REDEFINES BUF.
          05 FILLER PIC X(1).
          05 N7-S PIC X(3).
       01 N7-D-V REDEFINES BUF.
          05 N7-D PIC X(4).
       01 P1-S-V REDEFINES BUF.
          05 FILLER PIC X(5).
          05 P1-S PIC X(3).
       01 P1-D-V REDEFINES BUF.
          05 P1-D PIC X(10).
       01 J1-S-V REDEFINES BUF.
          05 J1-S PIC X(4).
       01 J1-D-V REDEFINES BUF.
          05 FILLER PIC X(2).
          05 J1-D PIC X(4) JUSTIFIED RIGHT.
       01 J2-S-V REDEFINES BUF.
          05 J2-S PIC X(4).
       01 J2-D-V REDEFINES BUF.
          05 FILLER PIC X(1).
          05 J2-D PIC X(5) JUSTIFIED RIGHT.
       01 J3-S-V REDEFINES BUF.
          05 J3-S PIC X(6).
       01 J3-D-V REDEFINES BUF.
          05 FILLER PIC X(1).
          05 J3-D PIC X(3) JUSTIFIED RIGHT.
       PROCEDURE DIVISION.
           MOVE PATTERN TO BUF
           MOVE N6-S TO N6-D
           DISPLAY 'N6:' BUF(1:7)
           MOVE PATTERN TO BUF
           MOVE N7-S TO N7-D
           DISPLAY 'N7:' BUF(1:6)
           MOVE PATTERN TO BUF
           MOVE P1-S TO P1-D
           DISPLAY 'P1:' BUF(1:12)
           MOVE PATTERN TO BUF
           MOVE J1-S TO J1-D
           DISPLAY 'J1:' BUF(1:8)
           MOVE PATTERN TO BUF
           MOVE J2-S TO J2-D
           DISPLAY 'J2:' BUF(1:8)
           MOVE PATTERN TO BUF
           MOVE J3-S TO J3-D
           DISPLAY 'J3:' BUF(1:8)
           GOBACK.
