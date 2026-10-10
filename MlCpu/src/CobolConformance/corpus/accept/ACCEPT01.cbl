       IDENTIFICATION DIVISION.
       PROGRAM-ID. ACCEPT01.
      * Control flow: PERFORM VARYING, EVALUATE, IF/ELSE, packed arithmetic.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01  WS-COUNT            PIC 9(3) VALUE 0.
       01  WS-TOTAL            PIC S9(7)V99 COMP-3 VALUE 0.
       01  WS-CODE             PIC X(2) VALUE 'AB'.
       PROCEDURE DIVISION.
       MAIN-PARA.
           PERFORM VARYING WS-COUNT FROM 1 BY 1 UNTIL WS-COUNT > 5
               ADD WS-COUNT TO WS-TOTAL
           END-PERFORM
           EVALUATE WS-CODE
               WHEN 'AB'
                   DISPLAY 'CODE AB'
               WHEN OTHER
                   DISPLAY 'OTHER'
           END-EVALUATE
           IF WS-TOTAL > 10
               DISPLAY 'TOTAL ' WS-TOTAL
           ELSE
               DISPLAY 'SMALL'
           END-IF
           GOBACK.
