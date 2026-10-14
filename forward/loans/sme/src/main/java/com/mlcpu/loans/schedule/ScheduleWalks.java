package com.mlcpu.loans.schedule;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.mlcpu.loans.api.Rejected;
import com.mlcpu.loans.api.ScheduleOutcome;
import com.mlcpu.loans.api.ScheduleRequest;
import com.mlcpu.loans.calendar.RepaymentCalendar;
import com.mlcpu.loans.rates.InterestFactors;
import com.mlcpu.loans.sme.PoolRunner;
import com.mlcpu.loans.terms.TermsReview;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.stream.Stream;

/**
 * Writes the walk of every public schedule scenario (walks/schedule/&lt;id&gt;.md): the reference's intermediate values, rule by
 * rule (Walk, AmortizationWalk). The walk's outcome must equal the scenario's expected one (the generated copy is the
 * reference). The debug mode shows the walk of a failing public scenario; hidden scenarios have none.
 *
 * <p>Usage: ScheduleWalks &lt;scenario root&gt; &lt;walks dir&gt;
 */
public final class ScheduleWalks {

    public static void main(String[] args) throws IOException {
        Path root = Path.of(args[0]).resolve("public").resolve("schedule");
        Path out = Path.of(args[1]).resolve("schedule");
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
        TermsReview terms = new TermsReview();
        List<String> problems = new ArrayList<>();
        long chars = 0;
        int max = 0;
        for (Path f : files) {
            JsonNode sc = json.readTree(f.toFile());
            String id = sc.get("id").asText();
            JsonNode step = sc.get("steps").get(0);
            ScheduleRequest q = PoolRunner.request(step.get("input"));
            Walk.start();
            Rejected r = terms.review(q);
            ScheduleOutcome o;
            if (r != null) {
                Walk.note("**Rejected** by the terms review (TRM): " + r);
                o = r;
            } else {
                o = new AmortizationWalk(new RepaymentCalendar(), new InterestFactors()).schedule(q, terms.downPayment(q));
            }
            String walk = Walk.finish();
            if (!PoolRunner.toJson(o).equals(step.get("expected"))) problems.add(id + ": the walk's outcome differs from the expected one");
            String text = "# The walk of " + id + ": the reference's intermediate values, rule by rule\n\n" + walk.trim() + "\n";
            Files.writeString(out.resolve(id + ".md"), text, StandardCharsets.UTF_8);
            chars += text.length();
            max = Math.max(max, text.length());
        }
        System.out.printf("schedule walks: %d written to %s (%,d characters, the longest %,d); %d problem(s)%n", files.size(), out, chars, max, problems.size());
        problems.forEach(p -> System.out.println("  problem: " + p));
        if (!problems.isEmpty()) System.exit(1);
    }
}
