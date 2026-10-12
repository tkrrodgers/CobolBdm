       IDENTIFICATION DIVISION.
       PROGRAM-ID. PARITY24.
      * LINKAGE records own no storage: they are the caller's bytes.
      * An INITIAL program called twice with the same argument must not
      * write the caller's record when it is initialized again; a large
      * record passed BY REFERENCE down two levels of CALL (as the J3S
      * heap is, 477 MB) is one storage, written at every level.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01 ARG-REC.
          05 ARG-A            PIC X(5) VALUE 'HELLO'.
          05 ARG-N            PIC 9(3) VALUE 7.
       01 BIG-REC.
          05 BIG-T            PIC X(1000) OCCURS 2000.
       PROCEDURE DIVISION.
           CALL 'P24INI' USING ARG-REC
           DISPLAY 'AFTER 1 ' ARG-A ARG-N
           CALL 'P24INI' USING ARG-REC
           DISPLAY 'AFTER 2 ' ARG-A ARG-N
           MOVE ALL '.' TO BIG-REC
           MOVE 'X' TO BIG-T (2000) (1:1)
           CALL 'P24MID' USING BIG-REC
           DISPLAY 'BIG [' BIG-T (1) (1:4) '] [' BIG-T (2000) (1:4) ']'
           CALL 'P24MID' USING BIG-REC
           DISPLAY 'BIG [' BIG-T (1) (1:4) '] [' BIG-T (2000) (1:4) ']'
           GOBACK.
       END PROGRAM PARITY24.
       IDENTIFICATION DIVISION.
       PROGRAM-ID. P24INI IS INITIAL.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01 CNT                 PIC 9 VALUE 0.
       LINKAGE SECTION.
       01 LK-REC.
          05 LK-A             PIC X(5).
          05 LK-N             PIC 9(3).
       PROCEDURE DIVISION USING LK-REC.
           ADD 1 TO CNT
           DISPLAY 'INI ' CNT ' SEES ' LK-A LK-N
           ADD 1 TO LK-N
           GOBACK.
       END PROGRAM P24INI.
       IDENTIFICATION DIVISION.
       PROGRAM-ID. P24MID.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01 CALLS               PIC 9 VALUE 0.
       LINKAGE SECTION.
       01 LK-BIG.
          05 LK-T             PIC X(1000) OCCURS 2000.
       PROCEDURE DIVISION USING LK-BIG.
           ADD 1 TO CALLS
           MOVE CALLS TO LK-T (1) (CALLS:1)
           CALL 'P24LOW' USING LK-BIG
           GOBACK.
       END PROGRAM P24MID.
       IDENTIFICATION DIVISION.
       PROGRAM-ID. P24LOW.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01 CALLS               PIC 9 VALUE 0.
       LINKAGE SECTION.
       01 LK-BIG.
          05 LK-T             PIC X(1000) OCCURS 2000.
       PROCEDURE DIVISION USING LK-BIG.
           ADD 1 TO CALLS
           MOVE 'Z' TO LK-T (2000) (CALLS + 1:1)
           GOBACK.
       END PROGRAM P24LOW.
