       IDENTIFICATION DIVISION.
       PROGRAM-ID. TRUTH03.
      * MOVE to numeric-edited: fixed $, comma suppression, trailing sign, all-Z zero.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01  WS-AMT              PIC S9(5)V99.
       01  WS-EDIT             PIC $ZZ,ZZ9.99-.
       01  WS-ZERO             PIC ZZZZ.
       PROCEDURE DIVISION.
       MAIN-PARA.
           MOVE WS-AMT TO WS-EDIT
           MOVE WS-AMT TO WS-ZERO
           GOBACK.
