       IDENTIFICATION DIVISION.
       PROGRAM-ID. AST05.
      * COBOL-85 grammar (AST) regression: RELEASE ... FROM a literal
      * (COBOL 2002 / IBM; as WRITE ... FROM) and FROM an identifier.
       ENVIRONMENT DIVISION.
       INPUT-OUTPUT SECTION.
       FILE-CONTROL.
           SELECT SD1 ASSIGN TO 'SORTWK1'.
       DATA DIVISION.
       FILE SECTION.
       SD  SD1.
       01  SR                          PIC X(4).
       WORKING-STORAGE SECTION.
       01  WS-REC                      PIC X(4) VALUE 'BBBB'.
       01  WS-EOF                      PIC X VALUE 'N'.
       PROCEDURE DIVISION.
       0000-MAIN.
           SORT SD1 ON ASCENDING KEY SR
               INPUT PROCEDURE 1000-IN OUTPUT PROCEDURE 2000-OUT
           GOBACK.
       1000-IN.
           RELEASE SR FROM 'CCCC'
           RELEASE SR FROM WS-REC
           RELEASE SR FROM 'AAAA'.
       2000-OUT.
           PERFORM UNTIL WS-EOF = 'Y'
               RETURN SD1 AT END MOVE 'Y' TO WS-EOF
                   NOT AT END DISPLAY SR
               END-RETURN
           END-PERFORM.
