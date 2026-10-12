       IDENTIFICATION DIVISION.
       PROGRAM-ID. AST02.
      * COBOL-85 grammar (AST) regression: EXIT PERFORM and EXIT
      * PERFORM CYCLE (COBOL 2002 / IBM Enterprise COBOL) in inline
      * PERFORMs, inside IF and EVALUATE, as in FNEM0900 (Tier 1).
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01  WS-I                        PIC 9(4) COMP VALUE 0.
       01  WS-N                        PIC 9(4) COMP VALUE 0.
       01  WS-SUM                      PIC 9(6) VALUE 0.
       01  WS-T.
           05  WS-V                    PIC 9(3) OCCURS 10.
       PROCEDURE DIVISION.
       0000-MAIN.
           PERFORM VARYING WS-I FROM 1 BY 1 UNTIL WS-I > 10
               MOVE WS-I TO WS-V (WS-I)
           END-PERFORM
           PERFORM VARYING WS-I FROM 1 BY 1 UNTIL WS-I > 10
               IF WS-V (WS-I) = 3 OR WS-V (WS-I) = 5
                   EXIT PERFORM CYCLE
               END-IF
               ADD WS-V (WS-I) TO WS-SUM
                   ON SIZE ERROR DISPLAY 'SIZE'
               END-ADD
           END-PERFORM
           MOVE 1 TO WS-N
           PERFORM UNTIL WS-N > 10
               IF WS-V (WS-N) NOT < 7
                  AND WS-SUM > 0
                   EXIT PERFORM
               END-IF
               EVALUATE TRUE
                   WHEN WS-V (WS-N) = 2
                       ADD 1 TO WS-N
                       EXIT PERFORM CYCLE
                   WHEN OTHER
                       CONTINUE
               END-EVALUATE
               ADD 1 TO WS-N
           END-PERFORM
           DISPLAY WS-SUM ' ' WS-N
           GOBACK.
