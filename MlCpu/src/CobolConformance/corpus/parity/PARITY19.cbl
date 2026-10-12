       IDENTIFICATION DIVISION.
       PROGRAM-ID. PARITY19.
      * SORT and MERGE as GnuCOBOL runs them: USING files read in turn,
      * a stable sort on the KEYs (numeric keys by value, a DESCENDING
      * key reversed), GIVING files written; INPUT PROCEDURE with
      * RELEASE [FROM], OUTPUT PROCEDURE with RETURN; MERGE is the same
      * stable sort of its files taken in order (they need not be in
      * sequence); SORT-RETURN; SORT of a table with distinct keys.
       ENVIRONMENT DIVISION.
       INPUT-OUTPUT SECTION.
       FILE-CONTROL.
           SELECT SD1 ASSIGN TO 'sortwk1'.
           SELECT IN1 ASSIGN TO 's1in1.dat'
               ORGANIZATION LINE SEQUENTIAL.
           SELECT IN2 ASSIGN TO 's1in2.dat'
               ORGANIZATION LINE SEQUENTIAL.
           SELECT OUT1 ASSIGN TO 's1out.dat'
               ORGANIZATION LINE SEQUENTIAL.
       DATA DIVISION.
       FILE SECTION.
       SD SD1.
       01 SR.
          05 SK              PIC X(2).
          05 SN              PIC S9(3).
          05 SDD             PIC X(3).
       FD IN1.
       01 I1R                PIC X(8).
       FD IN2.
       01 I2R                PIC X(8).
       FD OUT1.
       01 O1R                PIC X(8).
       WORKING-STORAGE SECTION.
       01 WS-EOF             PIC X.
       01 WS-I               PIC 9.
       01 WS-TAB             PIC X(12) VALUE 'C3A1D4B2E5F0'.
       01 WS-T REDEFINES WS-TAB.
          05 T-E             OCCURS 6.
             10 T-K          PIC X.
             10 T-N          PIC 9.
       PROCEDURE DIVISION.
       MAIN-PARA.
           OPEN OUTPUT IN1 IN2
           MOVE 'BB005a01' TO I1R WRITE I1R
           MOVE 'AA00Ja02' TO I1R WRITE I1R
           MOVE 'BB005a03' TO I1R WRITE I1R
           MOVE 'AA003a04' TO I1R WRITE I1R
           MOVE 'BB001a05' TO I1R WRITE I1R
           MOVE 'AA003b01' TO I2R WRITE I2R
           MOVE 'CC001b02' TO I2R WRITE I2R
           MOVE 'AA001b03' TO I2R WRITE I2R
           CLOSE IN1 IN2
           SORT SD1 ON ASCENDING KEY SK DESCENDING KEY SN
               USING IN1 IN2 GIVING OUT1
           DISPLAY 'SORT-RETURN ' SORT-RETURN
           PERFORM SHOW-OUT
           MERGE SD1 ON ASCENDING KEY SK
               USING IN1 IN2 GIVING OUT1
           PERFORM SHOW-OUT
           SORT SD1 ON DESCENDING KEY SDD
               INPUT PROCEDURE GEN-IN OUTPUT PROCEDURE SHOW-SORTED
           SORT T-E ON ASCENDING KEY T-K
           DISPLAY 'TABLE ' WS-TAB
           SORT T-E ON DESCENDING KEY T-N
           DISPLAY 'TABLE ' WS-TAB
           GOBACK.
       SHOW-OUT.
           OPEN INPUT OUT1
           MOVE 'N' TO WS-EOF
           PERFORM UNTIL WS-EOF = 'Y'
              READ OUT1 AT END MOVE 'Y' TO WS-EOF
                 NOT AT END DISPLAY 'OUT ' O1R
              END-READ
           END-PERFORM
           CLOSE OUT1.
       GEN-IN.
           PERFORM VARYING WS-I FROM 1 BY 1 UNTIL WS-I > 4
              MOVE 'ZZ' TO SK
              MOVE WS-I TO SN
              MOVE 'Q' TO SDD
              MOVE WS-I TO SDD (2:1)
              RELEASE SR
           END-PERFORM
           RELEASE SR FROM 'YY001Q9 '.
       SHOW-SORTED.
           MOVE 'N' TO WS-EOF
           PERFORM UNTIL WS-EOF = 'Y'
              RETURN SD1 AT END MOVE 'Y' TO WS-EOF
                 NOT AT END DISPLAY 'RET ' SR
              END-RETURN
           END-PERFORM.
