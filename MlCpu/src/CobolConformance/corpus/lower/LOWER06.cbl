       IDENTIFICATION DIVISION.
       PROGRAM-ID. LOWER06.
      * OCCURS DEPENDING ON forms that are abstained, never guessed:
      * an item after the ODO table (its position slides with the
      * count), an ODO table inside another table (complex ODO),
      * (start:) of an ODO group (GnuCOBOL 3.3 takes the whole current
      * length from the start) and INITIALIZE of an elementary ODO table
      * that is not uniform (GnuCOBOL sizes its first occurrence as the
      * whole table). The standard case beside them is lowered.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01 WS-CNT             PIC 9(2) VALUE 2.
       01 WS-SLIDE.
          05 S-EL            PIC X(2) OCCURS 1 TO 5 DEPENDING ON WS-CNT.
          05 S-TRAILER       PIC X(3).
       01 WS-COMPLEX.
          05 C-OUTER         OCCURS 3.
             10 C-EL         PIC X OCCURS 1 TO 4 DEPENDING ON WS-CNT.
       01 WS-STD.
          05 STD-EL          PIC X(2) OCCURS 1 TO 5 DEPENDING ON WS-CNT.
       01 WS-NUMS.
          05 N-EL            PIC S9(3) COMP-3 OCCURS 1 TO 5
                             DEPENDING ON WS-CNT.
       PROCEDURE DIVISION.
       MAIN-PARA.
           DISPLAY S-TRAILER
           MOVE 'X' TO C-EL (1, 1)
           IF WS-STD (2:) = SPACES CONTINUE END-IF
           INITIALIZE WS-NUMS
           MOVE WS-STD TO WS-SLIDE
           GOBACK.
