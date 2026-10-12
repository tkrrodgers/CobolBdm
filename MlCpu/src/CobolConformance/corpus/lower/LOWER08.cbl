       IDENTIFICATION DIVISION.
       PROGRAM-ID. LOWER08.
      * INITIALIZE forms that are abstained because GnuCOBOL departs
      * from the standard: a category TO VALUE (GnuCOBOL applies every
      * VALUE) and a table entry written in part (GnuCOBOL copies the
      * first occurrence, unwritten bytes included, over the others);
      * SET TO FALSE without a WHEN SET TO FALSE value.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01 G.
          05 A               PIC X(3) VALUE 'aaa'.
          05 N               PIC 9(3) VALUE 123.
          05 T OCCURS 3.
             10 TA           PIC X VALUE 't'.
             10 TN           PIC 9 VALUE 7.
       01 F                  PIC X.
          88 F-ON            VALUE 'Y'.
       PROCEDURE DIVISION.
       MAIN-PARA.
           INITIALIZE G NUMERIC TO VALUE
           INITIALIZE G REPLACING NUMERIC BY 5
           SET F-ON TO FALSE
           INITIALIZE G REPLACING ALPHANUMERIC BY 'X' NUMERIC BY 1
           GOBACK.
