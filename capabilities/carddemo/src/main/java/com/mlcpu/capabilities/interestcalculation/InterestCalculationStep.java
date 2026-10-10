package com.mlcpu.capabilities.interestcalculation;

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
import java.util.Iterator;
import java.util.Optional;

/** INTCALC step for CBACT04C, normalized: TCATBALF read in key order, ACCTFILE updated, interest transactions to TRANSACT. */
public final class InterestCalculationStep implements ModernStep {

    @Override
    public int run(Context ctx) throws Exception {
        ctx.display("START OF EXECUTION OF PROGRAM CBACT04C");
        IndexedDataset balanceData = open(ctx, "ERROR OPENING TRANSACTION CATEGORY BALANCE", () -> IndexedDataset.open(ctx.dataset("TCATBALF"), RecordLayouts.CATEGORY_BALANCE, 0, 17));
        IndexedDataset xrefs = open(ctx, "ERROR OPENING CROSS REF FILE", () -> IndexedDataset.open(ctx.dataset("XREFFILE"), RecordLayouts.CROSS_REFERENCE, 0, 16));
        IndexedDataset groups = open(ctx, "ERROR OPENING DALY REJECTS FILE", () -> IndexedDataset.open(ctx.dataset("DISCGRP"), RecordLayouts.DISCLOSURE_GROUP, 0, 16));
        IndexedDataset accountData = open(ctx, "ERROR OPENING ACCOUNT MASTER FILE", () -> IndexedDataset.open(ctx.dataset("ACCTFILE"), RecordLayouts.ACCOUNT, 0, 11));
        SequentialDataset.Writer out = open(ctx, "ERROR OPENING TRANSACTION FILE", () -> SequentialDataset.writer(ctx.dataset("TRANSACT"), RecordLayouts.TRANSACTION));

        byte[] unwritten = new byte[RecordLayouts.TRANSACTION];
        Arrays.fill(unwritten, ctx.storageDefault());
        Iterator<byte[]> inOrder = balanceData.inKeyOrder().iterator();
        var service = new InterestCalculationService(
                () -> inOrder.hasNext() ? Optional.of(inOrder.next()).map(img -> new Received<>(RecordLayouts.categoryBalance(img), Fields.image(img))) : Optional.empty(),
                new FileRepositories.Accounts(accountData),
                new FileRepositories.CardCrossReferences(xrefs),
                new FileRepositories.DisclosureGroups(groups),
                t -> out.write(RecordLayouts.transaction(t, unwritten)),
                ctx::display, ctx::now, ctx.storageDefault());
        try {
            service.run(ctx.parm() == null ? "" : ctx.parm());
        } catch (InterestCalculationService.MissingReference m) {
            switch (m.what) {
                case "ACCOUNT" -> {
                    ctx.display("ACCOUNT NOT FOUND: " + m.key);
                    throw BatchFailure.abend(ctx::display, "ERROR READING ACCOUNT FILE", "23");
                }
                case "XREF" -> {
                    ctx.display("ACCOUNT NOT FOUND: " + m.key);
                    throw BatchFailure.abend(ctx::display, "ERROR READING XREF FILE", "23");
                }
                default -> throw BatchFailure.abend(ctx::display, "ERROR READING DEFAULT DISCLOSURE GROUP", "23");
            }
        }
        close(ctx, "ERROR CLOSING TRANSACTION BALANCE FILE", balanceData);
        close(ctx, "ERROR CLOSING CROSS REF FILE", xrefs);
        close(ctx, "ERROR CLOSING DISCLOSURE GROUP FILE", groups);
        close(ctx, "ERROR CLOSING ACCOUNT FILE", accountData);
        close(ctx, "ERROR CLOSING TRANSACTION FILE", out);
        ctx.display("END OF EXECUTION OF PROGRAM CBACT04C");
        return 0;
    }

    static <T> T open(Context ctx, String what, BatchFailure.Opener<T> opener) {
        return BatchFailure.open(ctx::display, what, opener);
    }

    static void close(Context ctx, String what, AutoCloseable c) {
        BatchFailure.close(ctx::display, what, c);
    }
}
