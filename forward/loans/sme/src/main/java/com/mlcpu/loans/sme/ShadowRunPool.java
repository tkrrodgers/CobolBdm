package com.mlcpu.loans.sme;

import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.SerializationFeature;
import com.fasterxml.jackson.databind.node.ArrayNode;
import com.fasterxml.jackson.databind.node.JsonNodeFactory;
import com.fasterxml.jackson.databind.node.ObjectNode;
import com.mlcpu.loans.acceptance.memory.InMemoryPorts;
import com.mlcpu.loans.api.ScheduleRequest;
import com.mlcpu.loans.api.legacy.LegacyCodec;
import com.mlcpu.loans.api.legacy.LegacyRecordException;
import com.mlcpu.loans.api.migration.LegacyLoan;
import com.mlcpu.loans.api.migration.LegacyTerms;
import com.mlcpu.loans.api.servicing.BookLoan;
import com.mlcpu.loans.api.servicing.Booked;
import com.mlcpu.loans.api.servicing.LoanServicing;
import com.mlcpu.loans.application.LoansApplication;
import com.mlcpu.loans.migration.Migration;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.LocalDate;
import java.util.ArrayList;
import java.util.List;

/**
 * The shadow run's scenarios (G11, FE 9.3): ten consecutive days, each with its bookings in OFBiz (bookLoan, terms drawn from the
 * legacy envelope generator's requests: the corpus envelope-2, read as terms by the migration's mapping) under one calculation,
 * then the day's export (exportLoansAsLegacyRequests). After the legacy job has run on the exports (the oracle), the compare
 * scenarios reconcile each day's legacy output with the service's loans (migrateLegacyLoanBook, mode SHADOW): every booking
 * of the day is reconciled and no difference may be left unexplained (no DIFF row); each day's outcome counts are reported to
 * the log ("report"). A booking is kept only when the in-memory ledger of the host scenarios books it.
 *
 * <p>Usage: ShadowRunPool &lt;envelope LOAN.REQUEST.txt&gt; &lt;CURRENCY.txt&gt; &lt;service-model.json&gt; &lt;out dir&gt; &lt;shadow dir as OFBiz sees it&gt;
 * &lt;days&gt; &lt;bookings per day&gt; &lt;the legacy job's command, a JSON array&gt; [first day yyyy-mm-dd]
 * writes &lt;out&gt;/run/{runner.json, shadow-run.json} and &lt;out&gt;/days.json. One scenario: OFBiz's test runner rolls the
 * database back after a run, so the bookings, the legacy job (a command step) and the reconciliation share one run.
 */
public final class ShadowRunPool {

    private static final ObjectMapper JSON = new ObjectMapper().enable(SerializationFeature.INDENT_OUTPUT);
    private static final JsonNodeFactory NODES = JsonNodeFactory.instance;
    private static final String[] MODES = {"half_even", "half_up", "ceiling", "floor", "down", "up", "half_down"};
    private static final int[] PRECISIONS = {12, 9, 6, 3};

    public static void main(String[] args) throws IOException {
        List<String> requests = LegacyCodec.lines(Files.readString(Path.of(args[0]), LegacyCodec.CHARSET), LegacyCodec.REQUEST_LENGTH);
        List<String> currencies = LegacyCodec.lines(Files.readString(Path.of(args[1]), LegacyCodec.CHARSET), LegacyCodec.CURRENCY_LENGTH);
        OfbizPool pool = new OfbizPool(JSON.readTree(Path.of(args[2]).toFile()));
        Path out = Path.of(args[3]);
        String shadowDir = args[4];
        int days = Integer.parseInt(args[5]);
        int perDay = Integer.parseInt(args[6]);
        ArrayNode oracle = (ArrayNode) JSON.readTree(args[7]);
        LocalDate first = args.length > 8 ? LocalDate.parse(args[8]) : LocalDate.of(2026, 9, 1);
        Migration migration = new Migration(LoansApplication.quotes());
        Path run = out.resolve("run");
        Files.createDirectories(run);
        // no isolation: the reconciliation needs the loans as they were booked
        Files.writeString(run.resolve("runner.json"), "{\n  \"schema\": \"mlcpu.ofbiz-scenario-runner.v1\",\n  \"userLogin\": \"admin\"\n}\n", StandardCharsets.UTF_8);
        ObjectNode sc = NODES.objectNode();
        sc.put("schema", "mlcpu.ofbiz-scenario.v1");
        sc.put("id", "shadow-run");
        sc.put("origin", "G11 shadow run (FE 9.3): " + days + " consecutive days of bookings from the envelope generator's requests, each day exported, "
                + "the legacy job run on the exports, each day reconciled with the service's loans (no DIFF)");
        ArrayNode steps = sc.putArray("steps");
        ArrayNode compares = NODES.arrayNode();
        ArrayNode checks = NODES.arrayNode();
        ArrayNode dayList = NODES.arrayNode();
        int next = 0;
        int skipped = 0;
        // why a request is not booked: its record does not decode, its terms cannot be the service's (D-5), the service refuses it
        int undecodable = 0;
        int unrepresentable = 0;
        int refused = 0;
        for (int d = 1; d <= days; d++) {
            LocalDate day = first.plusDays(d - 1L);
            int precision = PRECISIONS[(d - 1) % PRECISIONS.length];
            String mode = MODES[(d - 1) % MODES.length];
            String control = LegacyCodec.encode(new com.mlcpu.loans.api.legacy.LegacyRunControl(precision, mode.toUpperCase()));
            String cell = String.format("p%02d-%s", precision, mode);
            InMemoryPorts memory = new InMemoryPorts(new InMemoryPorts.Given(day, "USD", OfbizPool.RATES));
            LoanServicing servicing = LoansApplication.create(memory.ports());
            String id = String.format("shadow-d%02d", d);
            int booked = 0;
            while (booked < perDay && next < requests.size()) {
                String raw = requests.get(next++);
                LegacyLoan loan;
                try {
                    loan = new LegacyLoan(LegacyCodec.request(raw), LegacyCodec.runControl(control), currency(currencies, raw), null, List.of(), null);
                } catch (LegacyRecordException e) {
                    skipped++;
                    undecodable++;
                    continue;
                }
                if (!(migration.terms(loan) instanceof LegacyTerms t)) {
                    skipped++;
                    unrepresentable++;
                    continue;
                }
                String externalId = "SH-" + d + "-" + raw.substring(2, 18);
                ScheduleRequest terms = t.terms();
                if (!(servicing.book(new BookLoan(externalId, "LOAN_BORROWER_1", "Company", "LOAN_STD", terms)) instanceof Booked)) {
                    skipped++;
                    refused++;
                    continue;
                }
                ObjectNode step = steps.addObject();
                step.put("service", "bookLoan");
                step.put("today", day.toString());
                ObjectNode params = pool.params("bookLoan", Canon.of(new BookLoan(externalId, "LOAN_BORROWER_1", "Company", "LOAN_STD", terms)));
                params.put("businessDate", day.toString());
                step.set("params", params);
                step.putObject("expect").put("outcome", "Booked");
                booked++;
            }
            ObjectNode export = steps.addObject();
            export.put("service", "exportLoansAsLegacyRequests");
            export.put("today", day.toString());
            ObjectNode ep = export.putObject("params");
            ep.put("bookedOn", day.toString());
            ep.put("outDir", shadowDir + "/" + id);
            ep.put("currencyFile", shadowDir + "/CURRENCY.VSAM.KSDS");
            export.putObject("expect").put("outcome", "LegacyExport");
            ObjectNode cs = compares.addObject();
            cs.put("service", "migrateLegacyLoanBook");
            cs.put("today", day.toString());
            ObjectNode cp = cs.putObject("params");
            cp.put("migrationId", "SHADOW-D" + String.format("%02d", d));
            cp.put("bookDir", shadowDir + "/" + id + "/" + cell);
            cp.put("mode", "SHADOW");
            cp.put("ownerPartyId", "LOAN_BORROWER_1");
            cp.put("organizationPartyId", "Company");
            cp.put("loanProductId", "LOAN_STD");
            cs.putObject("expect").put("outcome", "MigrationRun");
            cs.put("report", true);
            // every booking of the day reconciled, and none of them with a difference left unexplained
            ObjectNode all = checks.addObject();
            all.put("entity", "LoanLegacyRecon");
            all.putObject("where").put("migrationId", "SHADOW-D" + String.format("%02d", d));
            all.put("count", booked);
            ObjectNode check = checks.addObject();
            check.put("entity", "LoanLegacyRecon");
            check.putObject("where").put("migrationId", "SHADOW-D" + String.format("%02d", d)).put("outcome", "DIFF");
            check.put("count", 0);
            dayList.addObject().put("day", d).put("date", day.toString()).put("cell", cell).put("bookings", booked).put("runControl", control);
        }
        steps.addObject().set("run", oracle);
        steps.addAll(compares);
        sc.set("checks", checks);
        Files.writeString(run.resolve("shadow-run.json"), JSON.writeValueAsString(sc) + "\n", StandardCharsets.UTF_8);
        ObjectNode summary = NODES.objectNode();
        summary.put("schema", "mlcpu.shadow-run-days.v1");
        summary.set("days", dayList);
        summary.put("requestsUsed", next);
        summary.put("requestsSkipped", skipped);
        summary.putObject("skippedBecause").put("undecodable", undecodable).put("unrepresentable", unrepresentable).put("refusedByTheService", refused);
        Files.writeString(out.resolve("days.json"), JSON.writeValueAsString(summary) + "\n", StandardCharsets.UTF_8);
        System.out.printf("shadow run: %d days, %d requests used, %d skipped (%d undecodable, %d unrepresentable, %d refused by the service) -> %s%n",
                days, next, skipped, undecodable, unrepresentable, refused, out);
    }

    private static com.mlcpu.loans.api.legacy.LegacyCurrency currency(List<String> currencies, String request) {
        for (String c : currencies) {
            if (c.startsWith(request.substring(26, 29))) return LegacyCodec.currency(c);
        }
        return null;
    }
}
