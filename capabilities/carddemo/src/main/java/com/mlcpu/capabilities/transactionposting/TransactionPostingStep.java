package com.mlcpu.capabilities.transactionposting;

import com.mlcpu.carddemo.data.FileRepositories;
import com.mlcpu.carddemo.data.RecordLayouts;
import com.mlcpu.carddemo.domain.Repositories.Received;
import com.mlcpu.carddemo.platform.batch.BatchFailure;
import com.mlcpu.carddemo.platform.records.Fields;
import com.mlcpu.carddemo.platform.records.IndexedDataset;
import com.mlcpu.carddemo.platform.records.SequentialDataset;
import com.mlcpu.rt.ModernStep;
import java.io.IOException;
import java.util.Arrays;
import java.util.Optional;

/**
 * POSTTRAN step for CBTRN02C, normalized: binds the step's datasets (DALYTRAN in, TRANFILE new, XREFFILE, ACCTFILE and
 * TCATBALF for update, DALYREJS out) to the domain ports and runs {@link TransactionPostingService}.
 */
public final class TransactionPostingStep implements ModernStep {
    static final int REJECT_LRECL = 430;

    @Override
    public int run(Context ctx) throws Exception {
        ctx.display("START OF EXECUTION OF PROGRAM CBTRN02C");
        SequentialDataset.Reader daily = open(ctx, "ERROR OPENING DALYTRAN", () -> SequentialDataset.reader(ctx.dataset("DALYTRAN"), RecordLayouts.TRANSACTION));
        IndexedDataset transactions = open(ctx, "ERROR OPENING TRANSACTION FILE", () -> IndexedDataset.create(ctx.dataset("TRANFILE"), RecordLayouts.TRANSACTION, 0, 16));
        IndexedDataset xrefs = open(ctx, "ERROR OPENING CROSS REF FILE", () -> IndexedDataset.open(ctx.dataset("XREFFILE"), RecordLayouts.CROSS_REFERENCE, 0, 16));
        SequentialDataset.Writer rejectFile = open(ctx, "ERROR OPENING DALY REJECTS FILE", () -> SequentialDataset.writer(ctx.dataset("DALYREJS"), REJECT_LRECL));
        IndexedDataset accountData = open(ctx, "ERROR OPENING ACCOUNT MASTER FILE", () -> IndexedDataset.open(ctx.dataset("ACCTFILE"), RecordLayouts.ACCOUNT, 0, 11));
        IndexedDataset balanceData = open(ctx, "ERROR OPENING TRANSACTION BALANCE FILE", () -> IndexedDataset.open(ctx.dataset("TCATBALF"), RecordLayouts.CATEGORY_BALANCE, 0, 17));

        byte[] unwritten = new byte[RecordLayouts.TRANSACTION];
        Arrays.fill(unwritten, ctx.storageDefault());
        var accounts = new FileRepositories.Accounts(accountData);
        var service = new TransactionPostingService(
                () -> {
                    try {
                        Optional<byte[]> r = daily.next();
                        return r.map(img -> new Received<>(RecordLayouts.transaction(img), Fields.image(img)));
                    } catch (IOException e) {
                        throw BatchFailure.abend(ctx::display, "ERROR READING DALYTRAN FILE", e);
                    }
                },
                new TransactionValidator(new FileRepositories.CardCrossReferences(xrefs), accounts),
                accounts,
                new FileRepositories.CategoryBalances(balanceData, ctx.storageDefault()),
                t -> {
                    try {
                        transactions.insert(RecordLayouts.transaction(t, unwritten));
                    } catch (IOException e) {
                        throw BatchFailure.abend(ctx::display, "ERROR WRITING TO TRANSACTION FILE", e);
                    }
                },
                (received, reason) -> rejectFile.write(rejectRecord(received.original(), reason)),
                ctx::display,
                ctx::now);
        TransactionPostingService.Summary summary = service.postAll();
        close(ctx, "ERROR CLOSING DALYTRAN FILE", daily);
        close(ctx, "ERROR CLOSING TRANSACTION FILE", transactions);
        close(ctx, "ERROR CLOSING CROSS REF FILE", xrefs);
        close(ctx, "ERROR CLOSING DAILY REJECTS FILE", rejectFile);
        close(ctx, "ERROR CLOSING ACCOUNT FILE", accountData);
        close(ctx, "ERROR CLOSING TRANSACTION BALANCE FILE", balanceData);
        ctx.display("END OF EXECUTION OF PROGRAM CBTRN02C");
        return summary.returnCode();
    }

    /** The daily record exactly as received, then the reason code (4 digits) and its description (76 characters). */
    static byte[] rejectRecord(String original, RejectReason reason) {
        byte[] r = new byte[REJECT_LRECL];
        byte[] o = original.getBytes(java.nio.charset.StandardCharsets.ISO_8859_1);
        System.arraycopy(o, 0, r, 0, RecordLayouts.TRANSACTION);
        Fields.putUnsigned(r, 350, 4, reason.code);
        Fields.putText(r, 354, 76, reason.description);
        return r;
    }

    static <T> T open(Context ctx, String what, BatchFailure.Opener<T> opener) {
        return BatchFailure.open(ctx::display, what, opener);
    }

    static void close(Context ctx, String what, AutoCloseable c) {
        BatchFailure.close(ctx::display, what, c);
    }
}
