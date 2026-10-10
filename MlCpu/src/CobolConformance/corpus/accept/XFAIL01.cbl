       IDENTIFICATION DIVISION.
       PROGRAM-ID. XFAIL01.
      * Expected failure: the procedure division names an undeclared item.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01  WS-PRESENT          PIC 9.
       PROCEDURE DIVISION.
       MAIN-PARA.
           MOVE 1 TO WS-MISSING
           GOBACK.
