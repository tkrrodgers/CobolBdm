       IDENTIFICATION DIVISION.
       PROGRAM-ID. TRUTH04.
      * STRING with DELIMITED BY SPACE / SIZE, then INSPECT TALLYING.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01  WS-FIRST            PIC X(10).
       01  WS-LAST             PIC X(10).
       01  WS-FULL             PIC X(25).
       01  WS-CNT              PIC 9(2).
       PROCEDURE DIVISION.
       MAIN-PARA.
           MOVE SPACES TO WS-FULL
           MOVE 0 TO WS-CNT
           STRING WS-FIRST DELIMITED BY SPACE
                  ', ' DELIMITED BY SIZE
                  WS-LAST DELIMITED BY SPACE
               INTO WS-FULL
           END-STRING
           INSPECT WS-FULL TALLYING WS-CNT FOR ALL 'A'
           GOBACK.
