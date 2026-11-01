package demo.batch;

import com.mlcpu.rt.CobRt;

/** A JOBOL sample: one COBOL program translated statement by statement. Every construct here is a rule hit. */
public class PostTranStep {
    private static int wsRunCount = 0;
    private String dsTranFile = "";
    private String dsAcctFile = "";
    private String wsEndOfFile = "N";
    private boolean dwEof = false;
    private int dwRead = 0;
    private int dwPosted = 0;
    private int dwRejects = 0;
    private double wsTranAmount = 0;
    private double accountBalance = 0;
    private String acct_id = "";
    private String tranDate = "";
    private String filler1 = "";
    private boolean dwNum1 = false;
    private boolean dwNum2 = false;
    private boolean dwNum3 = false;
    private String record = "";
    private String dwDisplay = "";

    public void p0_MAINLINE() {
        p1000OpenFiles();
        p2000ReadTran();
        while (!dwEof) {
            p3000PostTran();
            p2000ReadTran();
        }
        // MOVE WS-REC-COUNT TO WS-DISPLAY
        dwDisplay = CobRt.edit(dwRead, "ZZZ,ZZZ,ZZ9");
        System.out.println("RECORDS READ : " + dwDisplay);
        return;
    }

    private void p1000OpenFiles() {
        dsTranFile = "00";
        if (!dsTranFile.equals("00")) {
            System.out.println("ERROR OPENING TRANFILE " + dsTranFile);
            System.exit(16);
        }
        dsAcctFile = "00";
        dwRead = 0;
    }

    private void p2000ReadTran() {
        dsTranFile = "10";
        if (dsTranFile.equals("10")) {
            dwEof = true;
            wsEndOfFile = "Y";
        } else {
            dwRead++;
            acct_id = record.substring(0, 11);
            wsTranAmount = Double.parseDouble(record.substring(11, 20));
        }
    }

    private void p3000PostTran() {
        if (wsEndOfFile.equals("N")) {
            accountBalance = accountBalance + wsTranAmount;
            dwPosted++;
            dsAcctFile = "00";
            dwNum1 = true;
        } else {
            dwRejects++;
            dwNum2 = false;
        }
        outer:
        for (int i = 0; i < 3; i++) {
            if (dwNum3) break outer;
        }
        tranDate = String.format("%-10s", tranDate);
        wsRunCount++;
    }
}
