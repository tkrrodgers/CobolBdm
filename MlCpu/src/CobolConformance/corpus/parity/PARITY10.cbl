       IDENTIFICATION DIVISION.
       PROGRAM-ID. PARITY10.
      * SET condition-name TO FALSE moves the 88's WHEN SET TO FALSE
      * (or FALSE) literal, figuratives included, to its variable, also
      * subscripted; the FALSE phrase is not one of the 88's values.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01 F1                 PIC X VALUE 'Y'.
          88 F1-ON           VALUE 'Y' WHEN SET TO FALSE IS 'N'.
       01 F2                 PIC 9 VALUE 1.
          88 F2-ON           VALUES 1 THRU 3 FALSE 0.
       01 F3                 PIC X(3) VALUE 'ABC'.
          88 F3-ON           VALUE 'X' 'Y' WHEN SET TO FALSE SPACES.
       01 T.
          05 TF              PIC X OCCURS 3 VALUE 'Y'.
             88 TF-ON        VALUE 'Y' FALSE 'N'.
       PROCEDURE DIVISION.
           SET F1-ON TO FALSE
           SET F2-ON TO FALSE
           SET F3-ON TO FALSE
           SET TF-ON (2) TO FALSE
           DISPLAY F1 ' ' F2 ' [' F3 '] ' TF (1) TF (2) TF (3)
           SET F1-ON F2-ON TO TRUE
           DISPLAY F1 ' ' F2
           IF F1-ON DISPLAY 'ON' END-IF
           SET F3-ON TO FALSE
           IF F3-ON DISPLAY 'F3 ON' ELSE DISPLAY 'F3 OFF' END-IF
           MOVE 'N' TO F1
           IF F1-ON DISPLAY 'F1 ON' ELSE DISPLAY 'F1 OFF' END-IF
           GOBACK.
