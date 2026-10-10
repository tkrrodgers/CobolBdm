       IDENTIFICATION DIVISION.
       PROGRAM-ID. ACCEPT04.
      * Tables with INDEXED BY, UNSTRING, STRING, INSPECT TALLYING.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01  WS-TABLE.
           05  WS-ENTRY OCCURS 5 TIMES INDEXED BY WS-IX.
               10  WS-KEY      PIC X(3).
               10  WS-VAL      PIC 9(3).
       01  WS-TEXT             PIC X(30) VALUE 'ALPHA,BETA,GAMMA'.
       01  WS-PART1            PIC X(10).
       01  WS-PART2            PIC X(10).
       01  WS-OUT              PIC X(30).
       01  WS-CNT              PIC 9(3) VALUE 0.
       PROCEDURE DIVISION.
       MAIN-PARA.
           UNSTRING WS-TEXT DELIMITED BY ','
               INTO WS-PART1 WS-PART2
           END-UNSTRING
           STRING WS-PART2 DELIMITED BY SPACE
                  '-' DELIMITED BY SIZE
                  WS-PART1 DELIMITED BY SPACE
               INTO WS-OUT
           END-STRING
           INSPECT WS-TEXT TALLYING WS-CNT FOR ALL ','
           SET WS-IX TO 1
           MOVE 'K01' TO WS-KEY (1)
           DISPLAY WS-OUT WS-CNT
           GOBACK.
