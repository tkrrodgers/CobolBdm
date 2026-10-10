package com.mlcpu.capabilities.transactionreporting;

import com.mlcpu.carddemo.data.FileRepositories;
import com.mlcpu.carddemo.data.RecordLayouts;
import com.mlcpu.carddemo.domain.Repositories.Received;
import com.mlcpu.carddemo.platform.batch.BatchFailure;
import com.mlcpu.carddemo.platform.records.Fields;
import com.mlcpu.carddemo.platform.records.IndexedDataset;
import com.mlcpu.carddemo.platform.records.SequentialDataset;
import com.mlcpu.rt.ModernStep;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.util.Optional;

/** TRANREPT step for CBTRN03C, normalized: TRANFILE (sorted) in, CARDXREF/TRANTYPE/TRANCATG lookups, DATEPARM, TRANREPT out. */
public final class TransactionReportStep implements ModernStep {
    static final int DATEPARM_LRECL = 80;

    @Override
    public int run(Context ctx) throws Exception {
        ctx.display("START OF EXECUTION OF PROGRAM CBTRN03C");
        SequentialDataset.Reader in = open(ctx, "ERROR OPENING TRANFILE", () -> SequentialDataset.reader(ctx.dataset("TRANFILE"), RecordLayouts.TRANSACTION));
        SequentialDataset.Writer report = open(ctx, "ERROR OPENING REPTFILE", () -> SequentialDataset.writer(ctx.dataset("TRANREPT"), ReportLayout.WIDTH));
        IndexedDataset xrefs = open(ctx, "ERROR OPENING CROSS REF FILE", () -> IndexedDataset.open(ctx.dataset("CARDXREF"), RecordLayouts.CROSS_REFERENCE, 0, 16));
        IndexedDataset types = open(ctx, "ERROR OPENING TRANSACTION TYPE FILE", () -> IndexedDataset.open(ctx.dataset("TRANTYPE"), RecordLayouts.TRANSACTION_TYPE, 0, 2));
        IndexedDataset categories = open(ctx, "ERROR OPENING TRANSACTION CATG FILE", () -> IndexedDataset.open(ctx.dataset("TRANCATG"), RecordLayouts.TRANSACTION_CATEGORY, 0, 6));
        SequentialDataset.Reader dates = open(ctx, "ERROR OPENING DATE PARM FILE", () -> SequentialDataset.reader(ctx.dataset("DATEPARM"), DATEPARM_LRECL));

        Optional<byte[]> parm = dates.next();
        if (parm.isPresent()) {
            String text = new String(parm.get(), 0, 21, StandardCharsets.ISO_8859_1);
            var service = new TransactionReportService(
                    () -> in.next().map(img -> new Received<>(RecordLayouts.transaction(img), Fields.image(img))),
                    new FileRepositories.CardCrossReferences(xrefs),
                    new FileRepositories.TransactionTypes(types),
                    new FileRepositories.TransactionCategories(categories),
                    new ReportPager(line -> report.write(line.getBytes(StandardCharsets.ISO_8859_1))),
                    ctx::display, ctx.displayNumeric());
            try {
                service.report(new TransactionReportService.Period(text.substring(0, 10), text.substring(11, 21)));
            } catch (TransactionReportService.MissingReference m) {
                String what = switch (m.what) {
                    case "CARD" -> "INVALID CARD NUMBER : " + String.format("%-16s", m.key);
                    case "TYPE" -> "INVALID TRANSACTION TYPE : " + String.format("%-2s", m.key);
                    default -> "INVALID TRAN CATG KEY : " + m.key;
                };
                throw BatchFailure.abend(ctx::display, what, "23");
            }
        }
        close(ctx, "ERROR CLOSING POSTED TRANSACTION FILE", in);
        close(ctx, "ERROR CLOSING REPORT FILE", report);
        close(ctx, "ERROR CLOSING CROSS REF FILE", xrefs);
        close(ctx, "ERROR CLOSING TRANSACTION TYPE FILE", types);
        close(ctx, "ERROR CLOSING TRANSACTION CATG FILE", categories);
        close(ctx, "ERROR CLOSING DATE PARM FILE", dates);
        ctx.display("END OF EXECUTION OF PROGRAM CBTRN03C");
        return 0;
    }

    static <T> T open(Context ctx, String what, BatchFailure.Opener<T> opener) {
        return BatchFailure.open(ctx::display, what, opener);
    }

    static void close(Context ctx, String what, AutoCloseable c) {
        BatchFailure.close(ctx::display, what, c);
    }
}
