       IDENTIFICATION DIVISION.
       PROGRAM-ID. AST07.
       AUTHOR. J8 LEGACY STYLE.
       DATE-WRITTEN. 1989-03-14.
      * COBOL-85 grammar (AST) regression: the constructs of the J8
      * legacy estate (fineract-legacy) the grammar already parsed:
      * 77-levels, sections, PERFORM THRU, GO TO, GO TO DEPENDING ON,
      * ALTER, SEARCH and SEARCH ALL, IF closed by a period, NEXT
      * SENTENCE, ON SIZE ERROR, COPY REPLACING.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       77  WS-OP                       PIC 9 VALUE 2.
       77  WS-K                        PIC S9(4) COMP VALUE 0.
       77  WS-R                        PIC S9(7)V99 COMP-3 VALUE 0.
       01  WS-TBL.
           05  WS-ENT OCCURS 4 ASCENDING KEY WS-KEY INDEXED BY WS-IX.
               10  WS-KEY              PIC 9(2).
               10  WS-VAL              PIC X(3).
       01  WS-SW                       PIC X VALUE 'N'.
           88  WS-FOUND                VALUE 'Y' WHEN SET TO FALSE 'N'.
           COPY ACPYREC REPLACING ==PFX-ID== BY ==C-ID==
                                  ==PFX-NAME== BY ==C-NAME==.
       PROCEDURE DIVISION.
       0000-MAIN SECTION.
       0000-START.
           PERFORM 1000-LOAD THRU 1000-EXIT.
           PERFORM 2000-DISPATCH THRU 2000-EXIT.
           ALTER 3000-SWITCH TO PROCEED TO 3000-ALT.
           PERFORM 3000-SWITCH THRU 3000-EXIT.
           DISPLAY C-ID ' ' C-NAME ' ' WS-SW ' ' WS-R.
           GOBACK.
       1000-LOAD.
           PERFORM VARYING WS-K FROM 1 BY 1 UNTIL WS-K > 4
               COMPUTE WS-KEY (WS-K) = WS-K * 10
                   ON SIZE ERROR GO TO 1000-EXIT
               END-COMPUTE
               MOVE 'V' TO WS-VAL (WS-K)
           END-PERFORM.
           IF WS-KEY (1) = 10
               NEXT SENTENCE
           ELSE
               MOVE 'X' TO WS-VAL (1).
       1000-EXIT.
           EXIT.
       2000-DISPATCH.
           GO TO 2000-ADD 2000-SEARCH DEPENDING ON WS-OP.
           GO TO 2000-EXIT.
       2000-ADD.
           ADD 1 TO WS-R ON SIZE ERROR MOVE 0 TO WS-R.
           GO TO 2000-EXIT.
       2000-SEARCH.
           SET WS-IX TO 1.
           SEARCH WS-ENT
               AT END SET WS-FOUND TO FALSE
               WHEN WS-KEY (WS-IX) = 30
                   SET WS-FOUND TO TRUE.
           SEARCH ALL WS-ENT
               AT END SET WS-FOUND TO FALSE
               WHEN WS-KEY (WS-IX) = 40
                   MOVE WS-VAL (WS-IX) TO C-NAME.
       2000-EXIT.
           EXIT.
       3000-SWITCH.
           GO TO 3000-EXIT.
       3000-ALT.
           MOVE 1 TO C-ID.
       3000-EXIT.
           EXIT.
