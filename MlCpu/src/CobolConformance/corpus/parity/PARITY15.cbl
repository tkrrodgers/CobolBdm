       IDENTIFICATION DIVISION.
       PROGRAM-ID. PARITY15.
      * COMP-5 is native binary: little-endian on x86-64 GnuCOBOL (the
      * stored bytes, seen through REDEFINES and group moves), with the
      * full binary capacity; COMP (binary-byteorder big-endian) beside.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01 G5.
          05 H5              PIC S9(4) COMP-5.
          05 F5              PIC S9(9) COMP-5.
          05 U5              PIC 9(4) COMP-5.
       01 G5X REDEFINES G5   PIC X(8).
       01 GB.
          05 HB              PIC S9(4) COMP.
       01 GBX REDEFINES GB   PIC X(2).
       PROCEDURE DIVISION.
           MOVE 258 TO H5
           MOVE -2 TO F5
           MOVE 65535 TO U5
           MOVE 258 TO HB
           IF G5X (1:2) = X'0201' DISPLAY 'H5 LITTLE-ENDIAN' END-IF
           IF G5X (3:4) = X'FEFFFFFF' DISPLAY 'F5 LITTLE-ENDIAN' END-IF
           IF G5X (7:2) = X'FFFF' DISPLAY 'U5 FULL CAPACITY' END-IF
           IF GBX = X'0102' DISPLAY 'HB BIG-ENDIAN' END-IF
           DISPLAY H5 ' ' F5 ' ' U5 ' ' HB
           MOVE X'0300' TO G5X (7:2)
           DISPLAY 'FROM BYTES ' U5
           MOVE G5 TO GB
           DISPLAY 'GROUP MOVE ' HB
           MOVE 65535 TO U5
           ADD 1 TO U5
           DISPLAY 'WRAP ' U5
           GOBACK.
