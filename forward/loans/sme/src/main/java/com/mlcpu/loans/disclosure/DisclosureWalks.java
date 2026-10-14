package com.mlcpu.loans.disclosure;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.mlcpu.loans.api.DisbursementLine;
import com.mlcpu.loans.api.Disclosed;
import com.mlcpu.loans.api.Disclosure;
import com.mlcpu.loans.api.DisclosureOutcome;
import com.mlcpu.loans.api.InstallmentLine;
import com.mlcpu.loans.api.Schedule;
import com.mlcpu.loans.api.ScheduleLine;
import com.mlcpu.loans.api.ScheduleRequest;
import com.mlcpu.loans.api.ScheduleService;
import com.mlcpu.loans.api.Scheduled;
import com.mlcpu.loans.application.LoansApplication;
import com.mlcpu.loans.sme.Canon;
import com.mlcpu.loans.sme.PoolRunner;
import java.io.IOException;
import java.math.BigDecimal;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.LocalDate;
import java.util.ArrayList;
import java.util.List;
import java.util.stream.Stream;

/**
 * The walks of the public disclosure scenarios (walks/disclosure/&lt;id&gt;.md): for a disclosure, the reference's intermediate
 * values in the order DISC computes them: the advance and the payments, the periods as DISC-08 measures them, the unit-period, each
 * payment's full unit-periods and fraction (DISC-09), the unit-periods per year, and the present value at the half cents around
 * the APR (DISC-05). A document scenario has no walk (its rules are a projection).
 *
 * <p>Usage: DisclosureWalks &lt;scenario root&gt; &lt;walks dir&gt;
 */
public final class DisclosureWalks {

    public static void main(String[] args) throws IOException {
        Path root = Path.of(args[0]).resolve("public").resolve("disclosure");
        Path out = Path.of(args[1]).resolve("disclosure");
        ObjectMapper json = new ObjectMapper();
        if (Files.isDirectory(out)) {
            try (Stream<Path> old = Files.list(out)) {
                for (Path f : old.toList()) Files.delete(f);
            }
        }
        Files.createDirectories(out);
        List<Path> files;
        try (Stream<Path> s = Files.list(root)) {
            files = s.filter(p -> p.toString().endsWith(".json")).sorted().toList();
        }
        ScheduleService quotes = LoansApplication.quotes();
        int written = 0;
        List<String> problems = new ArrayList<>();
        for (Path f : files) {
            JsonNode sc = json.readTree(f.toFile());
            JsonNode step = sc.get("steps").get(0);
            if (!step.get("call").asText().equals("disclose")) continue;
            ScheduleRequest q = PoolRunner.request(step.get("input"));
            DisclosureOutcome d = quotes.disclose(q);
            if (!Canon.of(d).equals(step.get("expected"))) problems.add(sc.get("id").asText() + ": the reference differs");
            if (!(d instanceof Disclosed dd) || !(quotes.schedule(q) instanceof Scheduled ok)) continue;
            String text = walk(sc.get("id").asText(), ok.schedule(), dd.disclosure());
            Files.writeString(out.resolve(sc.get("id").asText() + ".md"), text, StandardCharsets.UTF_8);
            written++;
        }
        System.out.printf("disclosure walks: %d written to %s; %d problem(s)%n", written, out, problems.size());
        problems.forEach(p -> System.out.println("  problem: " + p));
        if (!problems.isEmpty()) System.exit(1);
    }

    static String walk(String id, Schedule s, Disclosure d) {
        StringBuilder sb = new StringBuilder("# The walk of " + id + ": the reference's intermediate values, rule by rule\n\n");
        LocalDate advance = null;
        for (ScheduleLine l : s.lines()) if (l instanceof DisbursementLine x) advance = x.due();
        List<LocalDate> dates = new ArrayList<>();
        List<BigDecimal> pays = new ArrayList<>();
        for (ScheduleLine l : s.lines()) {
            if (l instanceof InstallmentLine i && i.due().isAfter(advance)) {
                dates.add(i.due());
                pays.add(i.totalDue());
            }
        }
        sb.append("## The figures (DISC-01 to DISC-03)\namount financed ").append(d.amountFinanced()).append(", total of payments ").append(d.totalOfPayments())
                .append(", finance charge ").append(d.financeCharge()).append("\n\n");
        sb.append("## The advance and the payments (DISC-04)\nadvance ").append(d.amountFinanced()).append(" on ").append(advance).append("; ")
                .append(dates.size()).append(" payment(s) due after it\n\n");
        if (d.financeCharge().signum() <= 0) {
            sb.append("## The APR (DISC-06)\nfinance charge 0: APR 0.00\n");
            return sb.toString();
        }
        sb.append("## The periods (DISC-08)\n");
        LocalDate prev = advance;
        int shown = 0;
        for (LocalDate p : dates) {
            if (shown++ < 14) {
                long k = UnitPeriod.fullMonths(prev, p);
                boolean whole = k > 0 && UnitPeriod.back(p, k).equals(prev);
                sb.append(prev).append(" to ").append(p).append(": ").append(whole ? k + " month(s)" : UnitPeriod.days(prev, p) + " days").append('\n');
            }
            prev = p;
        }
        if (dates.size() > 14) sb.append("...\n");
        UnitPeriod u = UnitPeriod.of(advance, dates);
        BigDecimal perYear = u.perYear();
        sb.append("\n## The unit-period (DISC-08)\n").append(DisclosureProbes.label(u)).append("; per year ").append(perYear.stripTrailingZeros().toPlainString()).append("\n\n");
        sb.append("## Each payment: full unit-periods t and the fraction f (DISC-09)\n| payment | due | amount | t | f |\n|---|---|---|---|---|\n");
        List<UnitPeriod.Count> counts = new ArrayList<>();
        for (int k = 0; k < dates.size(); k++) {
            UnitPeriod.Count c = u.count(advance, dates.get(k));
            counts.add(c);
            if (k < 14) sb.append("| ").append(k + 1).append(" | ").append(dates.get(k)).append(" | ").append(pays.get(k)).append(" | ").append(c.t()).append(" | ")
                    .append(c.f().stripTrailingZeros().toPlainString()).append(" |\n");
        }
        if (dates.size() > 14) sb.append("| ... | | | | |\n");
        BigDecimal r = d.annualPercentageRate();
        BigDecimal half = new BigDecimal("0.005");
        sb.append("\n## The APR (DISC-05)\nPV at ").append(r.subtract(half)).append("% = ").append(CostOfCredit.pv(r.subtract(half), perYear, counts, pays).toPlainString())
                .append(" >= amount financed ").append(d.amountFinanced()).append(" > PV at ").append(r.add(half)).append("% = ")
                .append(CostOfCredit.pv(r.add(half), perYear, counts, pays).toPlainString()).append(": APR ").append(r).append('\n');
        return sb.toString();
    }
}
