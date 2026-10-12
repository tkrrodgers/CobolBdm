       IDENTIFICATION DIVISION.
       PROGRAM-ID. PARITY13.
      * ALTER: the altered paragraph's lone GO TO jumps to the target
      * the last ALTER set, also within PERFORM ... THRU ranges; a
      * GO TO with no target of its own; two pairs in one ALTER; the
      * first-time switch pattern; a SECTION as the new target.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01 N                  PIC 9 VALUE 0.
       PROCEDURE DIVISION.
       MAIN SECTION.
       MAIN-PARA.
           PERFORM SW-PARA THRU SW-EXIT
           ALTER SW-PARA TO PROCEED TO P2
           PERFORM SW-PARA THRU SW-EXIT
           ALTER SW-PARA TO P3 SW2-PARA TO PROCEED TO Q2
           PERFORM SW-PARA THRU SW-EXIT
           PERFORM SW2-PARA THRU SW2-EXIT
           PERFORM 3 TIMES
              PERFORM ONCE-PARA THRU ONCE-EXIT
           END-PERFORM
           ALTER SW-PARA TO PROCEED TO OTHER-SECTION
           GO TO SW-PARA.
       SW-PARA.
           GO TO P1.
       P1.
           DISPLAY 'P1'
           GO TO SW-EXIT.
       P2.
           DISPLAY 'P2'
           GO TO SW-EXIT.
       P3.
           DISPLAY 'P3'.
       SW-EXIT.
           EXIT.
       SW2-PARA.
           GO TO.
       Q1.
           DISPLAY 'Q1 NOT REACHED'.
       Q2.
           DISPLAY 'Q2'.
       SW2-EXIT.
           EXIT.
       ONCE-PARA.
           GO TO FIRST-TIME.
       FIRST-TIME.
           ALTER ONCE-PARA TO PROCEED TO NOT-FIRST
           DISPLAY 'FIRST TIME'
           GO TO ONCE-EXIT.
       NOT-FIRST.
           ADD 1 TO N
           DISPLAY 'AGAIN ' N.
       ONCE-EXIT.
           EXIT.
       OTHER-SECTION SECTION.
       OTHER-PARA.
           DISPLAY 'OTHER SECTION'
           STOP RUN.
