       IDENTIFICATION DIVISION.
       PROGRAM-ID. LOWER04.
      * Arithmetic with ROUNDED, REMAINDER and ON SIZE ERROR is lowered.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01  WS-A                PIC S9(5)V99 VALUE 100.25.
       01  WS-B                PIC S9(3) VALUE 7.
       01  WS-Q                PIC S9(5).
       01  WS-R                PIC S9(3).
       01  WS-SMALL            PIC 9(2).
       01  WS-ERR              PIC X VALUE 'N'.
       PROCEDURE DIVISION.
       MAIN-PARA.
           DIVIDE WS-A BY WS-B GIVING WS-Q REMAINDER WS-R
           COMPUTE WS-A ROUNDED = WS-A / 3
           ADD 95 TO WS-SMALL
               ON SIZE ERROR MOVE 'Y' TO WS-ERR
           END-ADD
           MULTIPLY WS-B BY WS-Q
           SUBTRACT 1 FROM WS-R
           DISPLAY WS-Q ' ' WS-R ' ' WS-A ' ' WS-ERR
           GOBACK.
