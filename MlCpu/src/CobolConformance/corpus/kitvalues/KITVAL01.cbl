      * KITVAL01: the worked examples' value decoding (MlCpu.Kit.KitValues)
      * against GnuCOBOL. The record is written to DD KVOUT; each item is
      * DISPLAYed on its own line, in the order of the record's items.
       IDENTIFICATION DIVISION.
       PROGRAM-ID. KITVAL01.
       ENVIRONMENT DIVISION.
       INPUT-OUTPUT SECTION.
       FILE-CONTROL.
           SELECT KV-FILE ASSIGN TO KVOUT
               ORGANIZATION IS SEQUENTIAL.
       DATA DIVISION.
       FILE SECTION.
       FD  KV-FILE.
       01  KV-LINE                     PIC X(140).
       WORKING-STORAGE SECTION.
       01  KV-REC.
           05  KV-MONEY                PIC S9(13)V9(4) COMP-3.
           05  KV-MONEY-NEG            PIC S9(13)V9(4) COMP-3.
           05  KV-MONEY-ZERO           PIC S9(13)V9(4) COMP-3.
           05  KV-RATE                 PIC S9(3)V9(12) COMP-3.
           05  KV-COEF                 PIC S9(31)      COMP-3.
           05  KV-UPACK                PIC 9(5)        COMP-3.
           05  KV-SMALL                PIC S9(4)       COMP.
           05  KV-DAY                  PIC S9(9)       COMP.
           05  KV-UBIN                 PIC 9(4)        COMP-5.
           05  KV-ZONED                PIC S9(5)V99.
           05  KV-ZONED-POS            PIC S9(5)V99.
           05  KV-DATE                 PIC 9(8).
           05  KV-TEXT                 PIC X(10).
           05  KV-CODE                 PIC X(2).
               88  KV-CODE-OD          VALUE 'OD'.
       PROCEDURE DIVISION.
       0000-MAIN.
           MOVE 1000.5 TO KV-MONEY
           MOVE -17.0125 TO KV-MONEY-NEG
           MOVE 0 TO KV-MONEY-ZERO
           MOVE 1.005833333333 TO KV-RATE
           MOVE -1234567890123456789012345678901 TO KV-COEF
           MOVE 42 TO KV-UPACK
           MOVE -9 TO KV-SMALL
           MOVE 154498 TO KV-DAY
           MOVE 65535 TO KV-UBIN
           MOVE -123.45 TO KV-ZONED
           MOVE 7.1 TO KV-ZONED-POS
           MOVE 20240101 TO KV-DATE
           MOVE 'USD' TO KV-TEXT
           SET KV-CODE-OD TO TRUE
           DISPLAY KV-MONEY
           DISPLAY KV-MONEY-NEG
           DISPLAY KV-MONEY-ZERO
           DISPLAY KV-RATE
           DISPLAY KV-COEF
           DISPLAY KV-UPACK
           DISPLAY KV-SMALL
           DISPLAY KV-DAY
           DISPLAY KV-UBIN
           DISPLAY KV-ZONED
           DISPLAY KV-ZONED-POS
           DISPLAY KV-DATE
           DISPLAY KV-TEXT
           DISPLAY KV-CODE
           OPEN OUTPUT KV-FILE
           MOVE SPACES TO KV-LINE
           MOVE KV-REC TO KV-LINE
           WRITE KV-LINE
           CLOSE KV-FILE
           STOP RUN.
