       IDENTIFICATION DIVISION.
       PROGRAM-ID. ACCEPT05.
      * REDEFINES, 88-level SET, PERFORM THRU and GO TO.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01  WS-DATE             PIC 9(8) VALUE 20240131.
       01  WS-DATE-R REDEFINES WS-DATE.
           05  WS-YYYY         PIC 9(4).
           05  WS-MM           PIC 99.
           05  WS-DD           PIC 99.
       01  WS-FLAG             PIC X VALUE 'N'.
           88  FLAG-ON         VALUE 'Y'.
           88  FLAG-OFF        VALUE 'N'.
       PROCEDURE DIVISION.
       A-PARA.
           PERFORM B-PARA THRU C-PARA
           IF FLAG-ON
               GO TO Z-PARA
           END-IF
           DISPLAY 'NOT SET'.
       B-PARA.
           SET FLAG-ON TO TRUE.
       C-PARA.
           DISPLAY WS-YYYY '/' WS-MM '/' WS-DD.
       Z-PARA.
           GOBACK.
