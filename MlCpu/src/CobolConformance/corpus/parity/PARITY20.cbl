       IDENTIFICATION DIVISION.
       PROGRAM-ID. PARITY20.
      * The decimal kernel's fast paths (P4): 31-digit COMP-3 stores and
      * decodes (two-long codecs), divisions computed at the receiver's
      * precision (COMPUTE / ROUNDED), compares, and a CALL per round
      * (the cached CALL target). work/perf4/BENCH02 is the same program
      * with 3,000,000 rounds, the speed benchmark.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01 WS-I               PIC S9(9) COMP-5.
       01 WS-N               PIC S9(9) COMP-5 VALUE 20000.
       01 WS-ACC             PIC S9(13)V9(18) COMP-3 VALUE 0.
       01 WS-RATE            PIC S9(13)V9(18) COMP-3
                             VALUE 0.000123456789012345.
       01 WS-BAL             PIC S9(13)V9(18) COMP-3 VALUE 1000000.
       01 WS-INT             PIC S9(13)V9(18) COMP-3.
       01 WS-TMP             PIC S9(13)V9(18) COMP-3.
       01 WS-SMALL           PIC S9(9)V99 COMP-3.
       01 WS-CNT             PIC S9(9) COMP-3 VALUE 0.
       01 WS-DISP            PIC -(13)9.9(18).
       PROCEDURE DIVISION.
           PERFORM VARYING WS-I FROM 1 BY 1 UNTIL WS-I > WS-N
              COMPUTE WS-INT ROUNDED = WS-BAL * WS-RATE / 12
              ADD WS-INT TO WS-ACC
              MOVE WS-ACC TO WS-TMP
              COMPUTE WS-SMALL ROUNDED = WS-TMP / 7
              IF WS-TMP > WS-BAL
                 ADD 1 TO WS-CNT
              END-IF
              CALL 'P20SUB' USING WS-BAL WS-INT WS-TMP
           END-PERFORM
           MOVE WS-ACC TO WS-DISP
           DISPLAY 'ACC ' WS-DISP
           MOVE WS-BAL TO WS-DISP
           DISPLAY 'BAL ' WS-DISP
           DISPLAY 'SMALL ' WS-SMALL ' CNT ' WS-CNT
           GOBACK.
       END PROGRAM PARITY20.
       IDENTIFICATION DIVISION.
       PROGRAM-ID. P20SUB.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01 WS-X               PIC S9(13)V9(18) COMP-3.
       LINKAGE SECTION.
       01 LK-BAL             PIC S9(13)V9(18) COMP-3.
       01 LK-INT             PIC S9(13)V9(18) COMP-3.
       01 LK-TMP             PIC S9(13)V9(18) COMP-3.
       PROCEDURE DIVISION USING LK-BAL LK-INT LK-TMP.
           COMPUTE WS-X = LK-INT / 3
           SUBTRACT WS-X FROM LK-BAL
           IF LK-BAL < 0
              MOVE 0 TO LK-BAL
           END-IF
           GOBACK.
       END PROGRAM P20SUB.
