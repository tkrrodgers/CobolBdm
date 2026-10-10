       IDENTIFICATION DIVISION.
       PROGRAM-ID. TRUTH05.
      * A second ELSE belongs to the outer IF when the inner IF has no END-IF.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01  WS-A                PIC S9.
       01  WS-B                PIC S9.
       01  WS-R                PIC XX.
       PROCEDURE DIVISION.
       MAIN-PARA.
           IF WS-A > 0
               IF WS-B > 0
                   MOVE 'PP' TO WS-R
               ELSE
                   MOVE 'PN' TO WS-R
           ELSE
               MOVE 'NX' TO WS-R
           END-IF
           GOBACK.
