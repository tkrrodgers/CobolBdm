       IDENTIFICATION DIVISION.
       PROGRAM-ID. PARITY01.
      * DISPLAY of zoned, packed, binary and edited items; arithmetic; INSPECT.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01  WS-Z                PIC S9(5)V99 VALUE -123.45.
       01  WS-P                PIC S9(5)V99 COMP-3 VALUE 678.9.
       01  WS-B                PIC S9(9) COMP VALUE 16.
       01  WS-E                PIC ZZ,ZZ9.99-.
       01  WS-I                PIC 9(3) VALUE 998.
       01  WS-T                PIC X(12) VALUE 'HELLO WORLD'.
       PROCEDURE DIVISION.
       MAIN-PARA.
           DISPLAY 'Z=' WS-Z
           DISPLAY 'P=' WS-P
           DISPLAY 'B=' WS-B
           MOVE WS-Z TO WS-E
           DISPLAY 'E=' WS-E
           ADD 1 TO WS-I
           DISPLAY 'I=' WS-I
           ADD 1 TO WS-I
           DISPLAY 'I2=' WS-I
           INSPECT WS-T REPLACING ALL 'O' BY '0'
           DISPLAY 'T=' WS-T
           COMPUTE WS-Z ROUNDED = WS-P / 7
           DISPLAY 'Z2=' WS-Z
           GOBACK.
