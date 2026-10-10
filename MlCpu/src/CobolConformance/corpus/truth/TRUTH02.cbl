       IDENTIFICATION DIVISION.
       PROGRAM-ID. TRUTH02.
      * DIVIDE ... GIVING ... REMAINDER (D4): remainder uses the stored quotient.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01  WS-DIVIDEND         PIC 9(4).
       01  WS-DIVISOR          PIC 9(2).
       01  WS-Q                PIC 9(3).
       01  WS-R                PIC 9(2).
       PROCEDURE DIVISION.
       MAIN-PARA.
           DIVIDE WS-DIVIDEND BY WS-DIVISOR GIVING WS-Q REMAINDER WS-R
           GOBACK.
