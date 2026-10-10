//NESTPROC JOB (ACCT),'NESTED PROCS'
//* Cataloged OUTER calls INNER; caller overrides HLQ, SET supplies MBR; O2.DD1 is overridden.
//         SET MBR=JAN
//RUN1     EXEC OUTER,HLQ=QA,MBR=&MBR
//O2.DD1   DD DSN=OVERRIDE.DSN,DISP=SHR
