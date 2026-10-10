       IDENTIFICATION DIVISION.
       PROGRAM-ID. ACCEPT03.
      * Sequential file I/O with FILE STATUS and an 88-level end flag.
       ENVIRONMENT DIVISION.
       INPUT-OUTPUT SECTION.
       FILE-CONTROL.
           SELECT IN-FILE ASSIGN TO INFILE
               ORGANIZATION IS SEQUENTIAL
               FILE STATUS IS WS-IN-STAT.
           SELECT OUT-FILE ASSIGN TO OUTFILE
               FILE STATUS IS WS-OUT-STAT.
       DATA DIVISION.
       FILE SECTION.
       FD  IN-FILE RECORDING MODE F.
       01  IN-REC              PIC X(80).
       FD  OUT-FILE RECORDING MODE F.
       01  OUT-REC             PIC X(80).
       WORKING-STORAGE SECTION.
       01  WS-IN-STAT          PIC XX.
       01  WS-OUT-STAT         PIC XX.
       01  WS-EOF              PIC X VALUE 'N'.
           88  AT-EOF          VALUE 'Y'.
       PROCEDURE DIVISION.
       MAIN-PARA.
           OPEN INPUT IN-FILE OUTPUT OUT-FILE
           PERFORM UNTIL AT-EOF
               READ IN-FILE
                   AT END SET AT-EOF TO TRUE
                   NOT AT END
                       MOVE IN-REC TO OUT-REC
                       WRITE OUT-REC
               END-READ
           END-PERFORM
           CLOSE IN-FILE OUT-FILE
           GOBACK.
