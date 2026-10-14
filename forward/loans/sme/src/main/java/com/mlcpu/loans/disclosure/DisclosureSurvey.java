package com.mlcpu.loans.disclosure;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.mlcpu.loans.api.DisbursementLine;
import com.mlcpu.loans.api.Disclosure;
import com.mlcpu.loans.api.InstallmentLine;
import com.mlcpu.loans.api.Schedule;
import com.mlcpu.loans.api.ScheduleLine;
import com.mlcpu.loans.api.ScheduleOutcome;
import com.mlcpu.loans.api.ScheduleService;
import com.mlcpu.loans.api.Scheduled;
import com.mlcpu.loans.application.LoansApplication;
import com.mlcpu.loans.sme.PoolRunner;
import java.io.BufferedReader;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.LocalDate;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.TreeMap;
import java.util.concurrent.ConcurrentHashMap;
import java.util.stream.Stream;

/** What the S5 pool's schedules give the disclosure: unit-periods, finance-charge signs, APR range. Usage: DisclosureSurvey &lt;pool/schedule&gt; [per file] */
public final class DisclosureSurvey {

    public static void main(String[] args) throws IOException {
        Path pool = Path.of(args[0]);
        int perFile = args.length > 1 ? Integer.parseInt(args[1]) : Integer.MAX_VALUE;
        ObjectMapper json = new ObjectMapper();
        List<JsonNode> read = new ArrayList<>();
        try (Stream<Path> s = Files.list(pool)) {
            for (Path f : s.filter(p -> p.toString().endsWith(".jsonl")).sorted().toList()) {
                int n = 0;
                try (BufferedReader r = Files.newBufferedReader(f, StandardCharsets.UTF_8)) {
                    String line;
                    while ((line = r.readLine()) != null && n++ < perFile) read.add(json.readTree(line));
                }
            }
        }
        ScheduleService quotes = LoansApplication.quotes();
        CostOfCredit cost = new CostOfCredit();
        Map<String, Integer> stats = new ConcurrentHashMap<>();
        List<String> samples = java.util.Collections.synchronizedList(new ArrayList<>());
        read.parallelStream().forEach(sc -> {
            ScheduleOutcome o = quotes.schedule(PoolRunner.request(sc.get("steps").get(0).get("input")));
            if (!(o instanceof Scheduled ok)) {
                stats.merge("rejected", 1, Integer::sum);
                return;
            }
            Schedule s = ok.schedule();
            LocalDate advance = null;
            List<LocalDate> dates = new ArrayList<>();
            for (ScheduleLine l : s.lines()) {
                if (l instanceof DisbursementLine d) advance = d.due();
                if (l instanceof InstallmentLine i) dates.add(i.due());
            }
            UnitPeriod u = UnitPeriod.of(advance, dates);
            stats.merge("unit " + u.kind + (u.kind == UnitPeriod.Kind.TERM ? "" : "(" + u.size + ")"), 1, Integer::sum);
            Disclosure d = cost.of(s);
            stats.merge("charge " + (d.financeCharge().signum() < 0 ? "negative" : d.financeCharge().signum() == 0 ? "zero" : "positive"), 1, Integer::sum);
            int apr = d.annualPercentageRate().intValue();
            stats.merge("apr " + (apr < 10 ? "0-9" : apr < 50 ? "10-49" : apr < 100 ? "50-99" : apr < 1000 ? "100-999" : "1000+"), 1, Integer::sum);
            if (!advance.equals(dates.isEmpty() ? advance : s.lines().get(0).due())) stats.merge("advance not first line", 1, Integer::sum);
            if (d.financeCharge().signum() < 0 && samples.size() < 5) samples.add(sc.get("id").asText() + " " + d);
            if (apr >= 1000 && samples.size() < 10) samples.add(sc.get("id").asText() + " " + d + " " + u);
        });
        new TreeMap<>(stats).forEach((k, v) -> System.out.println(k + ": " + v));
        samples.forEach(x -> System.out.println("  " + x));
    }
}
