       IDENTIFICATION DIVISION.
       PROGRAM-ID. PARITY05.
      * OCCURS DEPENDING ON (GnuCOBOL -std=ibm): a group holding an ODO
      * table is as long as its current occurrences wherever it is
      * referenced (MOVE either way, compare, DISPLAY, LENGTH OF,
      * reference modification with a length, WRITE / WRITE FROM,
      * INITIALIZE); a
      * receiver holding its own object uses the value before the MOVE;
      * subscripts past the count are not checked; VALUE fills the
      * maximum; an FD with an ODO record is variable-length.
       ENVIRONMENT DIVISION.
       INPUT-OUTPUT SECTION.
       FILE-CONTROL.
           SELECT VFILE ASSIGN TO 'parity05v.dat'
               ORGANIZATION IS SEQUENTIAL.
           SELECT LFILE ASSIGN TO 'parity05l.txt'
               ORGANIZATION IS LINE SEQUENTIAL.
       DATA DIVISION.
       FILE SECTION.
       FD VFILE.
       01 V-REC.
          05 V-CNT           PIC 9(2).
          05 V-EL            PIC X(3) OCCURS 1 TO 5 TIMES
                             DEPENDING ON V-CNT.
       FD LFILE.
       01 L-REC.
          05 L-CNT           PIC 9(2).
          05 L-EL            PIC X(3) OCCURS 1 TO 5 TIMES
                             DEPENDING ON L-CNT.
       WORKING-STORAGE SECTION.
       01 WS-REC.
          05 WS-CNT          PIC 9(2) VALUE 4.
          05 WS-HDR          PIC X(3) VALUE 'HDR'.
          05 WS-TAB.
             10 WS-EL        PIC X(2) OCCURS 1 TO 6 TIMES
                             DEPENDING ON WS-CNT VALUE 'ZZ'.
       01 WS-VSTR.
          05 VLEN            PIC S9(4) BINARY.
          05 VTEXT.
             10 VCHAR        PIC X OCCURS 0 TO 20 TIMES
                             DEPENDING ON VLEN OF WS-VSTR.
       01 WS-COPY.
          05 WS-CNT2         PIC 9(2).
          05 WS-EL2          PIC X(2) OCCURS 0 TO 6 TIMES
                             DEPENDING ON WS-CNT2.
       01 R3.
          05 C3              PIC 9(2).
          05 G3.
             10 H3           PIC X(2).
             10 E3           OCCURS 1 TO 6 DEPENDING ON C3.
                15 A3        PIC X.
                15 N3        PIC 9.
       01 R5.
          05 C5              PIC 9(2).
          05 E5              PIC 9(2) OCCURS 1 TO 6 DEPENDING ON C5.
       01 R6.
          05 C6              PIC 9(2).
          05 E6              PIC X(2) OCCURS 1 TO 6 DEPENDING ON C6.
       01 WS-OUT             PIC X(20).
       01 WS-LEN             PIC 9(4).
       01 WS-X               PIC X(17) VALUE '04AAABBBCCCDDDEEE'.
       PROCEDURE DIVISION.
           DISPLAY 'VALUE [' WS-REC ']'
           MOVE 6 TO WS-CNT
           DISPLAY 'VAL6  [' WS-REC ']'
           MOVE 'AABBCCDDEEFF' TO WS-TAB
           MOVE 3 TO WS-CNT
           DISPLAY 'CNT3  [' WS-REC ']'
           DISPLAY 'TAB3  [' WS-TAB ']'
           MOVE WS-REC TO WS-OUT
           DISPLAY 'OUT   [' WS-OUT ']'
           MOVE LENGTH OF WS-REC TO WS-LEN
           DISPLAY 'LENREC ' WS-LEN
           MOVE FUNCTION LENGTH (WS-TAB) TO WS-LEN
           DISPLAY 'LENTAB ' WS-LEN
           DISPLAY 'REFMOD [' WS-TAB (2:4) '][' WS-REC (4:9) ']'
           MOVE 'XXYYZZWW' TO WS-TAB
           MOVE 6 TO WS-CNT
           DISPLAY 'RECV  [' WS-REC ']'
           MOVE 2 TO WS-CNT
           MOVE 'Q' TO WS-EL (4)
           MOVE 6 TO WS-CNT
           DISPLAY 'SUB4  [' WS-REC ']'
           MOVE 2 TO WS-CNT
           IF WS-TAB = 'XXYY' DISPLAY 'EQ XXYY' ELSE DISPLAY 'NE XXYY'
           END-IF
           IF WS-TAB = 'XXYYZZ' DISPLAY 'EQ XXYYZZ'
           ELSE DISPLAY 'NE XXYYZZ' END-IF
           MOVE 5 TO VLEN
           MOVE 'HELLO WORLD' TO VTEXT
           DISPLAY 'VTEXT [' VTEXT ']'
           MOVE 11 TO VLEN
           DISPLAY 'VTX11 [' VTEXT ']'
           MOVE VTEXT TO WS-OUT
           DISPLAY 'VOUT  [' WS-OUT ']'
           MOVE 0 TO VLEN
           MOVE LENGTH OF VTEXT TO WS-LEN
           DISPLAY 'V0    [' VTEXT '] ' WS-LEN
           MOVE 4 TO WS-CNT
           MOVE 2 TO WS-CNT2
           MOVE WS-REC TO WS-COPY
           DISPLAY 'COPY  [' WS-COPY ']'
           MOVE 6 TO WS-CNT2
           DISPLAY 'COPY6 [' WS-COPY ']'
           MOVE '01ABCDEFGHIJKL' TO WS-COPY
           DISPLAY 'C2    [' WS-COPY '] ' WS-CNT2
           MOVE 6 TO WS-CNT
           MOVE ALL '*' TO WS-TAB
           MOVE 3 TO WS-CNT
           INITIALIZE WS-TAB
           MOVE 6 TO WS-CNT
           DISPLAY 'INIT1 [' WS-REC ']'
           MOVE 6 TO C3
           MOVE ALL '*' TO G3
           MOVE 3 TO C3
           INITIALIZE G3
           MOVE 6 TO C3
           DISPLAY 'INIT2 [' R3 ']'
           MOVE ALL '*' TO G3
           MOVE 3 TO C3
           INITIALIZE R3
           MOVE 6 TO C3
           DISPLAY 'INIT3 [' R3 ']'
           MOVE 6 TO C5
           MOVE ALL '7' TO R5 (3:12)
           MOVE 3 TO C5
           INITIALIZE R5
           MOVE 6 TO C5
           DISPLAY 'INIT4 [' R5 ']'
           MOVE 6 TO C6
           MOVE ALL '*' TO R6 (3:12)
           MOVE 3 TO C6
           INITIALIZE R6
           MOVE 6 TO C6
           DISPLAY 'INIT5 [' R6 ']'
           OPEN OUTPUT VFILE LFILE
           MOVE 5 TO V-CNT
           MOVE 'AAABBBCCCDDDEEE' TO V-REC (3:15)
           MOVE 2 TO V-CNT
           WRITE V-REC
           MOVE 4 TO V-CNT
           WRITE V-REC
           MOVE 1 TO V-CNT
           WRITE V-REC FROM WS-X
           MOVE 5 TO L-CNT
           MOVE 'AAABBBCCC  DDDEEE' TO L-REC (3:15)
           MOVE 1 TO L-CNT
           WRITE L-REC
           MOVE 3 TO L-CNT
           WRITE L-REC
           MOVE 5 TO L-CNT
           WRITE L-REC
           CLOSE VFILE LFILE
           OPEN INPUT VFILE LFILE
           PERFORM 3 TIMES
              MOVE 5 TO V-CNT
              MOVE ALL '*' TO V-REC (3:15)
              READ VFILE AT END DISPLAY 'EOF V' END-READ
              MOVE 5 TO V-CNT
              DISPLAY 'VR [' V-REC ']'
           END-PERFORM
           PERFORM 3 TIMES
              MOVE 5 TO L-CNT
              MOVE ALL '*' TO L-REC (3:15)
              READ LFILE AT END DISPLAY 'EOF L' END-READ
              MOVE 5 TO L-CNT
              DISPLAY 'LR [' L-REC ']'
           END-PERFORM
           CLOSE VFILE LFILE
           GOBACK.
