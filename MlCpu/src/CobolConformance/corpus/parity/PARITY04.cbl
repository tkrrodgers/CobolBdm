       IDENTIFICATION DIVISION.
       PROGRAM-ID. PARITY04.
      * INTEGER-OF-DATE and DATE-OF-INTEGER outside their domain return
      * 0 (no stop): an invalid month or day, a date before 1601-01-01,
      * day numbers 0 and past 9999-12-31. Found by the Fineract estate
      * (FNDTKRN characterization seeds), where the Java runtime threw.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01 WS-DATE            PIC 9(8).
       01 WS-DAYS            PIC S9(9).
       01 WS-BACK            PIC 9(8).
       01 WS-I               PIC 9.
       01 WS-DATES.
          05 FILLER          PIC 9(8) VALUE 20241399.
          05 FILLER          PIC 9(8) VALUE 20240230.
          05 FILLER          PIC 9(8) VALUE 20230229.
          05 FILLER          PIC 9(8) VALUE 00000000.
          05 FILLER          PIC 9(8) VALUE 16001231.
          05 FILLER          PIC 9(8) VALUE 16010101.
          05 FILLER          PIC 9(8) VALUE 20240229.
          05 FILLER          PIC 9(8) VALUE 99991231.
       01 WS-DATE-TABLE REDEFINES WS-DATES.
          05 WS-D            PIC 9(8) OCCURS 8.
       PROCEDURE DIVISION.
           PERFORM VARYING WS-I FROM 1 BY 1 UNTIL WS-I > 8
               MOVE WS-D (WS-I) TO WS-DATE
               COMPUTE WS-DAYS = FUNCTION INTEGER-OF-DATE (WS-DATE)
               DISPLAY 'IOD ' WS-DATE ' ' WS-DAYS
           END-PERFORM
           MOVE 0 TO WS-DAYS
           COMPUTE WS-BACK = FUNCTION DATE-OF-INTEGER (WS-DAYS)
           DISPLAY 'DOI ' WS-DAYS ' ' WS-BACK
           MOVE 1 TO WS-DAYS
           COMPUTE WS-BACK = FUNCTION DATE-OF-INTEGER (WS-DAYS)
           DISPLAY 'DOI ' WS-DAYS ' ' WS-BACK
           MOVE 3067671 TO WS-DAYS
           COMPUTE WS-BACK = FUNCTION DATE-OF-INTEGER (WS-DAYS)
           DISPLAY 'DOI ' WS-DAYS ' ' WS-BACK
           MOVE 3067672 TO WS-DAYS
           COMPUTE WS-BACK = FUNCTION DATE-OF-INTEGER (WS-DAYS)
           DISPLAY 'DOI ' WS-DAYS ' ' WS-BACK
           MOVE -5 TO WS-DAYS
           COMPUTE WS-BACK = FUNCTION DATE-OF-INTEGER (WS-DAYS)
           DISPLAY 'DOI ' WS-DAYS ' ' WS-BACK
           GOBACK.
