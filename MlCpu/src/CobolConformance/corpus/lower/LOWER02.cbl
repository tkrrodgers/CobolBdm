       IDENTIFICATION DIVISION.
       PROGRAM-ID. LOWER02.
      * String verbs lower to runtime calls, never to frontiers.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01  WS-TEXT             PIC X(30) VALUE 'RED,GREEN,BLUE'.
       01  WS-A                PIC X(10).
       01  WS-B                PIC X(10).
       01  WS-C                PIC X(10).
       01  WS-OUT              PIC X(40).
       01  WS-PTR              PIC 9(3) VALUE 1.
       01  WS-CNT              PIC 9(3) VALUE 0.
       PROCEDURE DIVISION.
       MAIN-PARA.
           UNSTRING WS-TEXT DELIMITED BY ','
               INTO WS-A WS-B WS-C
           END-UNSTRING
           STRING WS-C DELIMITED BY SPACE
                  '/' DELIMITED BY SIZE
                  WS-A DELIMITED BY SPACE
               INTO WS-OUT WITH POINTER WS-PTR
           END-STRING
           INSPECT WS-OUT TALLYING WS-CNT FOR ALL 'E'
           INSPECT WS-OUT REPLACING ALL 'E' BY 'e'
           DISPLAY WS-OUT WS-CNT WS-PTR
           GOBACK.
