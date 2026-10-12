       IDENTIFICATION DIVISION.
       PROGRAM-ID. PARITY06.
      * Group-level USAGE and SIGN apply to every subordinate elementary
      * item without its own clause: USAGE COMP-3 / COMP / COMP-5 on a
      * group packs or binarizes its PIC 9 items (lengths, bytes,
      * arithmetic); SIGN LEADING SEPARATE on a group places the sign
      * of its signed items; an item's own clause wins.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01 G-PACKED USAGE COMP-3.
          05 P-A             PIC S9(5).
          05 P-B             PIC 9(3).
          05 P-SUB.
             10 P-C          PIC S9(3)V99.
          05 P-D             PIC 9(4) USAGE DISPLAY.
       01 G-PACKED-X REDEFINES G-PACKED PIC X(14).
       01 G-BIN COMP.
          05 B-A             PIC S9(4).
          05 B-B             PIC 9(9).
       01 G-BIN-X REDEFINES G-BIN PIC X(6).
       01 G-SIGN SIGN IS LEADING SEPARATE CHARACTER.
          05 S-A             PIC S9(3).
          05 S-B             PIC S9(2)V9.
          05 S-C             PIC 9(2).
          05 S-D             PIC S9(2) SIGN TRAILING SEPARATE.
       01 G-COMP5 USAGE IS COMPUTATIONAL-5.
          05 C5-A            PIC 9(4).
       01 WS-LEN             PIC 9(4).
       PROCEDURE DIVISION.
           MOVE LENGTH OF G-PACKED TO WS-LEN
           DISPLAY 'LEN PACKED ' WS-LEN
           MOVE LENGTH OF G-BIN TO WS-LEN
           DISPLAY 'LEN BIN ' WS-LEN
           MOVE LENGTH OF G-SIGN TO WS-LEN
           DISPLAY 'LEN SIGN ' WS-LEN
           MOVE LENGTH OF G-COMP5 TO WS-LEN
           DISPLAY 'LEN COMP5 ' WS-LEN
           MOVE -1234 TO P-A
           MOVE 56 TO P-B
           MOVE 12.5 TO P-C
           MOVE 7 TO P-D
           IF G-PACKED-X (1:3) = X'01234D' DISPLAY 'P-A PACKED' END-IF
           IF G-PACKED-X (4:2) = X'056F' DISPLAY 'P-B PACKED' END-IF
           IF G-PACKED-X (6:3) = X'01250C' DISPLAY 'P-C PACKED' END-IF
           DISPLAY 'P-D [' G-PACKED-X (9:4) ']'
           ADD 1 TO P-A
           COMPUTE P-C = P-C * 2
           DISPLAY 'P ' P-A ' ' P-B ' ' P-C ' ' P-D
           MOVE -2 TO B-A
           MOVE 65536 TO B-B
           IF G-BIN-X = X'FFFE00010000' DISPLAY 'BIN BYTES' END-IF
           DISPLAY 'B ' B-A ' ' B-B
           MOVE -12 TO S-A
           MOVE 4.5 TO S-B
           MOVE 7 TO S-C
           MOVE -3 TO S-D
           DISPLAY 'SIGN [' G-SIGN ']'
           GOBACK.
