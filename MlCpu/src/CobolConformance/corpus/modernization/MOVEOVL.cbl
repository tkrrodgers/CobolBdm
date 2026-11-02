      * Raw overlapping MOVE: shapes cobc -std=ibm routes through cob_move_ibm
      * (left to right, byte by byte). Oracle capture for RawMoveProfile.
       IDENTIFICATION DIVISION.
       PROGRAM-ID. MOVEOVL.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01 PATTERN.
          05 FILLER PIC X(26) VALUE 'ABCDEFGHIJKLMNOPQRSTUVWXYZ'.
          05 FILLER PIC X(26) VALUE 'abcdefghijklmnopqrstuvwxyz'.
          05 FILLER PIC X(20) VALUE '0123456789<>?;:[]{}='.
       01 BUF PIC X(1100).
       01 N1-S-V REDEFINES BUF.
          05 N1-S PIC X(4).
       01 N1-D-V REDEFINES BUF.
          05 FILLER PIC X(1).
          05 N1-D PIC X(3).
       01 N2-S-V REDEFINES BUF.
          05 N2-S PIC X(8).
       01 N2-D-V REDEFINES BUF.
          05 FILLER PIC X(1).
          05 N2-D PIC X(7).
       01 N3-S-V REDEFINES BUF.
          05 N3-S PIC X(16).
       01 N3-D-V REDEFINES BUF.
          05 FILLER PIC X(1).
          05 N3-D PIC X(15).
       01 N4-S-V REDEFINES BUF.
          05 N4-S PIC X(32).
       01 N4-D-V REDEFINES BUF.
          05 FILLER PIC X(1).
          05 N4-D PIC X(31).
       01 N5-S-V REDEFINES BUF.
          05 N5-S PIC X(64).
       01 N5-D-V REDEFINES BUF.
          05 FILLER PIC X(1).
          05 N5-D PIC X(63).
       01 N9-S-V REDEFINES BUF.
          05 N9-S PIC X(4).
       01 N9-D-V REDEFINES BUF.
          05 FILLER PIC X(2).
          05 N9-D PIC X(4).
       01 NA-S-V REDEFINES BUF.
          05 NA-S PIC X(8).
       01 NA-D-V REDEFINES BUF.
          05 FILLER PIC X(3).
          05 NA-D PIC X(8).
       01 NB-S-V REDEFINES BUF.
          05 NB-S PIC X(1024).
       01 NB-D-V REDEFINES BUF.
          05 FILLER PIC X(1).
          05 NB-D PIC X(1023).
       01 N8-S-V REDEFINES BUF.
          05 FILLER PIC X(1).
          05 N8-S PIC X(4).
       01 N8-D-V REDEFINES BUF.
          05 N8-D PIC X(3).
       01 NC-S-V REDEFINES BUF.
          05 FILLER PIC X(1).
          05 NC-S PIC X(1023).
       01 NC-D-V REDEFINES BUF.
          05 NC-D PIC X(1023).
       01 G1-S-V REDEFINES BUF.
          05 G1-S.
             10 FILLER PIC X(4).
       01 G1-D-V REDEFINES BUF.
          05 FILLER PIC X(1).
          05 G1-D.
             10 FILLER PIC X(3).
       01 G2-S-V REDEFINES BUF.
          05 G2-S.
             10 FILLER PIC X(8).
       01 G2-D-V REDEFINES BUF.
          05 FILLER PIC X(1).
          05 G2-D.
             10 FILLER PIC X(7).
       01 G3-S-V REDEFINES BUF.
          05 G3-S.
             10 FILLER PIC X(64).
       01 G3-D-V REDEFINES BUF.
          05 FILLER PIC X(1).
          05 G3-D.
             10 FILLER PIC X(63).
       01 G4-S-V REDEFINES BUF.
          05 FILLER PIC X(1).
          05 G4-S.
             10 FILLER PIC X(4).
       01 G4-D-V REDEFINES BUF.
          05 G4-D.
             10 FILLER PIC X(4).
       01 GE-S-V REDEFINES BUF.
          05 GE-S.
             10 FILLER PIC X(4).
       01 GE-D-V REDEFINES BUF.
          05 FILLER PIC X(1).
          05 GE-D PIC X(3).
       01 EG-S-V REDEFINES BUF.
          05 EG-S PIC X(4).
       01 EG-D-V REDEFINES BUF.
          05 FILLER PIC X(1).
          05 EG-D.
             10 FILLER PIC X(3).
       01 GB-S-V REDEFINES BUF.
          05 FILLER PIC X(1).
          05 GB-S.
             10 FILLER PIC X(4).
       01 GB-D-V REDEFINES BUF.
          05 GB-D PIC X(3).
       01 BG-S-V REDEFINES BUF.
          05 FILLER PIC X(1).
          05 BG-S PIC X(4).
       01 BG-D-V REDEFINES BUF.
          05 BG-D.
             10 FILLER PIC X(3).
       01 U1-S-V REDEFINES BUF.
          05 U1-S PIC 9(4).
       01 U1-D-V REDEFINES BUF.
          05 FILLER PIC X(1).
          05 U1-D PIC X(3).
       01 U2-S-V REDEFINES BUF.
          05 FILLER PIC X(1).
          05 U2-S PIC 9(4).
       01 U2-D-V REDEFINES BUF.
          05 U2-D PIC X(3).
       01 A1-S-V REDEFINES BUF.
          05 A1-S PIC A(4).
       01 A1-D-V REDEFINES BUF.
          05 FILLER PIC X(1).
          05 A1-D PIC A(3).
       01 A2-S-V REDEFINES BUF.
          05 FILLER PIC X(1).
          05 A2-S PIC A(4).
       01 A2-D-V REDEFINES BUF.
          05 A2-D PIC A(3).
       01 I PIC 99 VALUE 1.
       01 J PIC 99 VALUE 2.
       PROCEDURE DIVISION.
           MOVE PATTERN TO BUF
           MOVE N1-S TO N1-D
           DISPLAY 'N1:' BUF(1:6)
           MOVE PATTERN TO BUF
           MOVE N2-S TO N2-D
           DISPLAY 'N2:' BUF(1:10)
           MOVE PATTERN TO BUF
           MOVE N3-S TO N3-D
           DISPLAY 'N3:' BUF(1:18)
           MOVE PATTERN TO BUF
           MOVE N4-S TO N4-D
           DISPLAY 'N4:' BUF(1:34)
           MOVE PATTERN TO BUF
           MOVE N5-S TO N5-D
           DISPLAY 'N5:' BUF(1:66)
           MOVE PATTERN TO BUF
           MOVE N9-S TO N9-D
           DISPLAY 'N9:' BUF(1:8)
           MOVE PATTERN TO BUF
           MOVE NA-S TO NA-D
           DISPLAY 'NA:' BUF(1:13)
           MOVE PATTERN TO BUF
           MOVE NB-S TO NB-D
           DISPLAY 'NB:' BUF(1:8) BUF(1020:8)
           MOVE PATTERN TO BUF
           MOVE N8-S TO N8-D
           DISPLAY 'N8:' BUF(1:7)
           MOVE PATTERN TO BUF
           MOVE NC-S TO NC-D
           DISPLAY 'NC:' BUF(1:8) BUF(1020:8)
           MOVE PATTERN TO BUF
           MOVE G1-S TO G1-D
           DISPLAY 'G1:' BUF(1:6)
           MOVE PATTERN TO BUF
           MOVE G2-S TO G2-D
           DISPLAY 'G2:' BUF(1:10)
           MOVE PATTERN TO BUF
           MOVE G3-S TO G3-D
           DISPLAY 'G3:' BUF(1:66)
           MOVE PATTERN TO BUF
           MOVE G4-S TO G4-D
           DISPLAY 'G4:' BUF(1:7)
           MOVE PATTERN TO BUF
           MOVE GE-S TO GE-D
           DISPLAY 'GE:' BUF(1:6)
           MOVE PATTERN TO BUF
           MOVE EG-S TO EG-D
           DISPLAY 'EG:' BUF(1:6)
           MOVE PATTERN TO BUF
           MOVE GB-S TO GB-D
           DISPLAY 'GB:' BUF(1:7)
           MOVE PATTERN TO BUF
           MOVE BG-S TO BG-D
           DISPLAY 'BG:' BUF(1:7)
           MOVE PATTERN TO BUF
           MOVE U1-S TO U1-D
           DISPLAY 'U1:' BUF(1:6)
           MOVE PATTERN TO BUF
           MOVE U2-S TO U2-D
           DISPLAY 'U2:' BUF(1:7)
           MOVE PATTERN TO BUF
           MOVE A1-S TO A1-D
           DISPLAY 'A1:' BUF(1:6)
           MOVE PATTERN TO BUF
           MOVE A2-S TO A2-D
           DISPLAY 'A2:' BUF(1:7)
           MOVE PATTERN TO BUF
           MOVE BUF(1:4) TO BUF(2:3)
           DISPLAY 'R1:' BUF(1:6)
           MOVE PATTERN TO BUF
           MOVE BUF(1:16) TO BUF(2:15)
           DISPLAY 'R2:' BUF(1:18)
           MOVE PATTERN TO BUF
           MOVE BUF(1:64) TO BUF(2:63)
           DISPLAY 'R3:' BUF(1:66)
           MOVE PATTERN TO BUF
           MOVE BUF(2:4) TO BUF(1:4)
           DISPLAY 'R4:' BUF(1:6)
           MOVE PATTERN TO BUF
           MOVE BUF(I:4) TO BUF(J:3)
           DISPLAY 'V1:' BUF(1:6)
           MOVE PATTERN TO BUF
           MOVE BUF(I:64) TO BUF(J:63)
           DISPLAY 'V2:' BUF(1:66)
           GOBACK.
