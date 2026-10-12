       IDENTIFICATION DIVISION.
       PROGRAM-ID. AST01.
      * COBOL-85 grammar (AST) regression: the 88-level FALSE value of
      * IBM Enterprise COBOL / COBOL 2002, as the J8 legacy estate
      * writes it (VALUE 'Y' WHEN SET TO FALSE 'N'), with IS, after
      * VALUES ... THRU, with a figurative, without WHEN SET TO, in a
      * table and in a copybook; SET condition-name TO FALSE.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01  WS-SWITCHES.
           05  WS-STOP-SW              PIC X.
               88  WS-STOP             VALUE 'Y' WHEN SET TO FALSE 'N'.
           05  WS-COPY-SW              PIC X.
               88  WS-COPY-MADE        VALUE 'Y'
                                       WHEN SET TO FALSE IS 'N'.
           05  WS-RANGE                PIC 9.
               88  WS-LOW              VALUES ARE 1 THRU 3
                                       WHEN SET TO FALSE ZERO.
           05  WS-CODE                 PIC X(3).
               88  WS-CODE-OK          VALUE 'AAA' 'BBB' FALSE SPACES.
           COPY ASTSWS REPLACING ==:P:== BY ==QY==.
       01  WS-TABLE.
           05  WS-ENTRY                PIC X OCCURS 3.
               88  WS-ENTRY-ON         VALUE 'Y' WHEN SET TO FALSE 'N'.
       PROCEDURE DIVISION.
       0000-MAIN.
           SET WS-STOP TO FALSE
           SET WS-COPY-MADE TO FALSE
           SET WS-LOW TO FALSE
           SET WS-CODE-OK TO FALSE
           SET QY-FOUND QY-DONE TO FALSE
           SET WS-ENTRY-ON (2) TO FALSE
           IF NOT WS-STOP AND NOT QY-FOUND
               SET WS-STOP TO TRUE
           END-IF
           DISPLAY WS-STOP-SW WS-COPY-SW WS-RANGE '[' WS-CODE ']'
                   QY-FOUND-SW QY-DONE-SW WS-ENTRY (2)
           GOBACK.
