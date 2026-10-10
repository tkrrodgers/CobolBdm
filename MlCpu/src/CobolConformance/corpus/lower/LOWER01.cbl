       IDENTIFICATION DIVISION.
       PROGRAM-ID. LOWER01.
      * SEARCH is abstained whole (D1): its bodies must never run unguarded.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01  WS-TABLE.
           05  WS-ENTRY OCCURS 3 TIMES INDEXED BY WS-IX.
               10  WS-KEY      PIC X(3).
       01  WS-FOUND            PIC X VALUE 'N'.
       PROCEDURE DIVISION.
       MAIN-PARA.
           MOVE 'AAA' TO WS-KEY (1)
           SET WS-IX TO 1
           SEARCH WS-ENTRY
               AT END MOVE 'N' TO WS-FOUND
               WHEN WS-KEY (WS-IX) = 'AAA'
                   MOVE 'Y' TO WS-FOUND
           END-SEARCH
           DISPLAY WS-FOUND
           GOBACK.
