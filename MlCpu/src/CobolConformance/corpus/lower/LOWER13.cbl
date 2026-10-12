       IDENTIFICATION DIVISION.
       PROGRAM-ID. LOWER13.
      * arithmetic-osvs (GnuCOBOL -std=ibm). ADD CB DB TO R2: cobc
      * aligns the integer sum to 2 decimals the wrong way (3.3 defect:
      * 123 + 456 + 1.50 gives 501.50); lowered with CobRt.alignUp,
      * which stops the run where the defect would change the value.
      * An aligned intermediate whose scale is not known at compile time
      * (a function result) is a frontier.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01 CB                PIC S9(4) COMP VALUE 123.
       01 DB                PIC S9(4) COMP VALUE 456.
       01 T                 PIC 9(4) VALUE 9.
       01 R2                PIC S9(5)V99 VALUE 1.50.
       PROCEDURE DIVISION.
           ADD CB DB TO R2
           COMPUTE R2 = 1 + FUNCTION SQRT (T) * 2
           GOBACK.
