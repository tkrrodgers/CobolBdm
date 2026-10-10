       IDENTIFICATION DIVISION.
       PROGRAM-ID. TRUTH01.
      * COMPUTE: ROUNDED, high-order truncation, unsigned receiver, division truncation.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01  WS-A                PIC S9(5)V99.
       01  WS-B                PIC S9(3)V9.
       01  WS-PROD             PIC S9(5)V99.
       01  WS-SUM              PIC 9(3).
       01  WS-QUOT             PIC S9(3)V9.
       PROCEDURE DIVISION.
       MAIN-PARA.
           COMPUTE WS-PROD ROUNDED = WS-A * WS-B
           COMPUTE WS-SUM = WS-A + WS-B
           COMPUTE WS-QUOT = WS-A / WS-B
           GOBACK.
