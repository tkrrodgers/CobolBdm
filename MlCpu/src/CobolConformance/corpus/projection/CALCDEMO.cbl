       IDENTIFICATION DIVISION.
       PROGRAM-ID. CALCDEMO.
      * Projection fixture: an orchestration entry of PERFORMs only, a pure setup
      * paragraph (domain cluster), a closed calculation paragraph, a console paragraph.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01  WS-BALANCE          PIC S9(9)V99 VALUE 0.
       01  WS-RATE             PIC S9(3)V99 VALUE 0.
       01  WS-INTEREST         PIC S9(9)V99 VALUE 0.
       01  WS-TOTAL            PIC S9(11)V99 VALUE 0.
       01  WS-LABEL            PIC X(20) VALUE SPACES.
       PROCEDURE DIVISION.
       MAIN-PARA.
           PERFORM SETUP-PARA
           PERFORM CALC-PARA
           PERFORM CALC-PARA
           PERFORM REPORT-PARA
           GOBACK.
       SETUP-PARA.
           MOVE 'MONTHLY INTEREST' TO WS-LABEL
           MOVE 1234.56 TO WS-BALANCE
           MOVE 18.5 TO WS-RATE.
       CALC-PARA.
           COMPUTE WS-INTEREST ROUNDED = WS-BALANCE * WS-RATE / 1200
           ADD WS-INTEREST TO WS-TOTAL.
       REPORT-PARA.
           DISPLAY WS-LABEL ' ' WS-INTEREST ' ' WS-TOTAL.
