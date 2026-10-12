       IDENTIFICATION DIVISION.
       PROGRAM-ID. AST03.
      * COBOL-85 grammar (AST) regression: EXIT PARAGRAPH and EXIT
      * SECTION (COBOL 2002 / IBM Enterprise COBOL) inside IF, in
      * sections PERFORMed alone and THRU, as in FNDKDFO (Tier 1).
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01  WS-D                        PIC 9(2) VALUE 0.
       01  WS-RC                       PIC 9(2) VALUE 0.
       PROCEDURE DIVISION.
       0000-MAIN SECTION.
       0000-START.
           MOVE 31 TO WS-D
           PERFORM 1000-CHECK
           PERFORM 2000-SCAN THRU 2000-EXIT
           PERFORM 3000-PARAS
           DISPLAY WS-RC
           GOBACK.
       1000-CHECK SECTION.
       1000-START.
           IF WS-D > 30
               MOVE 20 TO WS-RC
               EXIT SECTION
           END-IF
           MOVE 0 TO WS-RC.
       1000-NOT-REACHED.
           MOVE 99 TO WS-RC.
       2000-SCAN SECTION.
       2000-START.
           IF WS-RC = 20
               ADD 1 TO WS-RC
               EXIT PARAGRAPH
           END-IF
           MOVE 98 TO WS-RC.
       2000-EXIT.
           EXIT.
       3000-PARAS SECTION.
       3000-A.
           IF WS-RC > 0
               EXIT PARAGRAPH
           END-IF.
       3000-B.
           ADD 1 TO WS-RC.
