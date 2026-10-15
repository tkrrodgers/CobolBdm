package com.mlcpu.loans.sme;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.node.ArrayNode;
import com.fasterxml.jackson.databind.node.ObjectNode;
import com.mlcpu.loans.api.ScheduleService;
import com.mlcpu.loans.api.legacy.LegacyCodec;
import com.mlcpu.loans.api.legacy.LegacyRecordException;
import com.mlcpu.loans.api.legacy.LegacyScheduleHeader;
import com.mlcpu.loans.api.legacy.LegacyScheduleLine;
import com.mlcpu.loans.api.legacy.LegacyScheduleRecord;
import com.mlcpu.loans.api.migration.LegacyLoan;
import com.mlcpu.loans.api.migration.Reconciliation;
import com.mlcpu.loans.application.LoansApplication;
import com.mlcpu.loans.migration.Migration;
import java.io.IOException;
import java.math.BigDecimal;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.EnumMap;
import java.util.HexFormat;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.TreeMap;
import java.util.TreeSet;
import java.util.function.UnaryOperator;

/**
 * The migration pool (slice migration, the engine side of FE 9.2 S9; digests/migration.md, rules MIG): every loan of the legacy
 * books (sme/legacy-books.json) is a candidate, given as its records exactly as the legacy datasets hold them; its expected terms
 * and reconciliation are the reference's (loans-migration {@code Migration} over {@code LoansApplication.quotes()}), its tags
 * the rule mutants it kills ({@link MigrationMutants}). SME probes add legacy records altered so that the rules no real loan
 * reaches are exercised (a difference, the order of the checks, codes the API cannot represent); their expected values are the
 * rules', checked against the outcome each probe aims at. Selection and split as {@link SlicePools}.
 *
 * <p>The report is also the engine-level reconciliation of the whole legacy book (G10's first half): every real loan's outcome
 * by book; a DIFF among them is a problem. It is cross-checked with the schedule pool (pool/schedule/pool.json), which classified
 * the same loans independently: its D-2 decisions are the EXPLAINED D-2 loans, its rejections the legacy rejects and D-3 loans.
 *
 * <p>Usage: MigrationPool &lt;books.json&gt; &lt;CobolBdm root&gt; &lt;fineract-cobol-gen root&gt; &lt;schedule pool.json&gt;
 * &lt;scenario root&gt; &lt;scenarios.json&gt; &lt;report.md&gt; [per group, 8]
 */
public final class MigrationPool {

    private static final ObjectMapper JSON = new ObjectMapper();
    private static final String SLICE = "migration";

    record Candidate(String key, String book, String loanId, ObjectNode input, JsonNode terms, JsonNode reconciliation, String origin, boolean probe,
            Set<String> tags) {
    }

    record Book(String name, List<String> requests, String runControl, Map<String, String> currencies, Map<String, List<String>> schedules,
            Map<String, String> rejects) {
    }

    public static void main(String[] args) throws IOException {
        JsonNode books = JSON.readTree(Path.of(args[0]).toFile());
        Map<String, Path> roots = Map.of("cobolbdm", Path.of(args[1]), "fcg", Path.of(args[2]));
        JsonNode schedulePool = JSON.readTree(Path.of(args[3]).toFile());
        Path scenarioRoot = Path.of(args[4]);
        Path manifest = Path.of(args[5]);
        Path report = Path.of(args[6]);
        int perGroup = args.length > 7 ? Integer.parseInt(args[7]) : 8;
        ScheduleService quotes = LoansApplication.quotes();
        Migration reference = new Migration(quotes);
        List<String> problems = new ArrayList<>();
        Map<String, Candidate> all = new LinkedHashMap<>();
        Map<MigrationMutants.Mutant, Integer> kills = new EnumMap<>(MigrationMutants.Mutant.class);
        Map<String, Map<String, Integer>> byBook = new TreeMap<>();
        Map<String, Integer> undecodable = new TreeMap<>();
        List<String[]> explainedD2 = new ArrayList<>();
        Candidate base = null;
        Book baseBook = null;
        Candidate withP = null;
        Book withPBook = null;
        Candidate d2 = null;
        Book d2Book = null;
        int inverseSame = 0;
        int inverseD4 = 0;

        for (JsonNode b : books.get("books")) {
            Book book = read(b, roots, problems);
            Map<String, Integer> counts = byBook.computeIfAbsent(book.name(), k -> new TreeMap<>());
            for (String q : book.requests()) {
                String loanId = q.substring(2, 18);
                ObjectNode input = input(book, q, book.schedules().getOrDefault(loanId, List.of()), book.rejects().get(loanId));
                LegacyLoan loan;
                try {
                    loan = loan(input);
                } catch (LegacyRecordException e) {
                    undecodable.merge(book.name() + ": " + e.layout() + " " + e.field(), 1, Integer::sum);
                    counts.merge("undecodable (the plugin: D-5)", 1, Integer::sum);
                    continue;
                }
                Candidate c = add(all, book.name(), loanId, input, loan, reference, quotes, "legacy book " + book.name() + ", loan " + loanId, false,
                        problems, kills);
                // the shadow run's inverse (LegacyTermsCodec, generated): the terms back as the same request, but for the D-4 inputs
                if (reference.terms(loan) instanceof com.mlcpu.loans.api.migration.LegacyTerms t) {
                    String back = LegacyCodec.encode(com.mlcpu.loans.api.legacy.LegacyTermsCodec.request(t.terms(), loanId));
                    if (back.equals(q)) inverseSame++;
                    else if (d4Only(q, back)) inverseD4++;
                    else problems.add("the inverse mapping does not give back " + book.name() + " " + loanId + ": " + q + " / " + back);
                }
                Reconciliation r = reference.reconcile(loan);
                counts.merge(r.outcome() + (r.decision() == null ? "" : " " + r.decision()), 1, Integer::sum);
                if (r.outcome().name().equals("DIFF")) problems.add("a real legacy loan differs: " + book.name() + " " + loanId + ": " + r);
                if ("D-2".equals(r.decision())) explainedD2.add(new String[] {book.name(), loanId});
                if (base == null && c != null && book.name().startsWith("edge") && r.outcome().name().equals("MATCH") && loan.lines().size() >= 5
                        && loan.lines().stream().allMatch(l -> !"P".equals(l.recType())) && loan.header().decimalPlaces() == 2) {
                    base = c;
                    baseBook = book;
                }
                if (withP == null && c != null && book.name().startsWith("edge") && r.outcome().name().equals("MATCH") && loan.header().decimalPlaces() == 2
                        && loan.lines().size() >= 4 && "P".equals(loan.lines().get(1).recType())) {
                    withP = c;
                    withPBook = book;
                }
                if (d2 == null && c != null && "D-2".equals(r.decision())) {
                    d2 = c;
                    d2Book = book;
                }
            }
        }
        crossCheck(schedulePool, byBook, explainedD2, problems);
        undecodable.put("(the inverse mapping: " + inverseSame + " requests back byte for byte, " + inverseD4 + " but for D-4 inputs)", 0);
        if (base == null || withP == null || d2 == null) throw new IllegalStateException("no base loans for the probes");
        probes(all, baseBook, base, reference, quotes, problems, kills);
        fieldProbes(all, baseBook, base, withPBook, withP, d2Book, d2, reference, quotes, problems, kills);
        for (MigrationMutants.Mutant m : MigrationMutants.Mutant.values()) {
            if (kills.getOrDefault(m, 0) == 0) problems.add("rule " + m.rule() + ": no candidate kills its mutant (" + m.change + ")");
        }

        List<Candidate> list = new ArrayList<>(all.values());
        SlicePools.Chosen<Candidate> chosen = SlicePools.choose(list, Candidate::tags,
                c -> c.reconciliation().path("outcome").asText() + "|" + c.reconciliation().path("decision").asText() + "|"
                        + c.terms().path("@type").asText(),
                Candidate::probe, perGroup, Comparator.comparing(Candidate::book).thenComparing(Candidate::loanId).thenComparing(Candidate::key), "mig",
                c -> c.input().toString().length(), "MIG-11");
        SlicePools.clear(scenarioRoot, SLICE);
        ArrayNode entries = JSON.createArrayNode();
        for (var e : chosen.byId().entrySet()) {
            String id = e.getKey();
            Candidate c = e.getValue();
            String visibility = chosen.publicIds().contains(id) ? "public" : "hidden";
            Files.writeString(scenarioRoot.resolve(visibility).resolve(SLICE).resolve(id + ".json"), scenario(id, c), StandardCharsets.UTF_8);
            SlicePools.entry(entries, id, SLICE, visibility, chosen.primary().get(id), c.tags(), c.origin());
        }
        SlicePools.writeManifest(manifest, SLICE, entries);
        writeReport(report, list, byBook, undecodable, kills, chosen, problems);
        System.out.printf("migration pool: %d candidates (%d probes), %d chosen, %d public; %d problem(s)%n", list.size(),
                list.stream().filter(Candidate::probe).count(), chosen.byId().size(), chosen.publicIds().size(), problems.size());
        byBook.forEach((k, v) -> { if (k.startsWith("edge") || k.startsWith("rejects")) System.out.println("  " + k + " " + v); });
        kills.forEach((m, n) -> System.out.printf("  %s killed by %d candidate(s)%n", m.rule(), n));
        problems.stream().limit(25).forEach(p -> System.out.println("  problem: " + p));
        if (!problems.isEmpty()) System.exit(1);
    }

    // ------------------------------------------------------------------------------------------------ the books

    static Book read(JsonNode b, Map<String, Path> roots, List<String> problems) throws IOException {
        List<String> requests = dataset(b.get("request"), LegacyCodec.REQUEST_LENGTH, roots, problems);
        String runControl = dataset(b.get("runControl"), LegacyCodec.RUN_CONTROL_LENGTH, roots, problems).get(0);
        Map<String, String> currencies = new TreeMap<>();
        for (String c : dataset(b.get("currency"), LegacyCodec.CURRENCY_LENGTH, roots, problems)) currencies.put(c.substring(0, 3), c);
        Map<String, List<String>> schedules = new LinkedHashMap<>();
        for (String s : dataset(b.get("schedule"), LegacyCodec.SCHEDULE_RECORD_LENGTH, roots, problems)) {
            schedules.computeIfAbsent(s.substring(0, 16), k -> new ArrayList<>()).add(s);
        }
        Map<String, String> rejects = new LinkedHashMap<>();
        for (String r : dataset(b.get("rejects"), LegacyCodec.REJECT_LENGTH, roots, problems)) rejects.put(r.substring(0, 16), r);
        return new Book(b.get("name").asText(), requests, runControl, currencies, schedules, rejects);
    }

    private static List<String> dataset(JsonNode d, int length, Map<String, Path> roots, List<String> problems) throws IOException {
        Path p = roots.get(d.get("root").asText()).resolve(d.get("path").asText());
        byte[] bytes = Files.readAllBytes(p);
        if (!sha256(bytes).equals(d.get("sha256").asText())) problems.add(p + ": its SHA-256 is not the one books.json records");
        return "lines".equals(d.get("format").asText()) ? LegacyCodec.lines(new String(bytes, LegacyCodec.CHARSET), length)
                : LegacyCodec.records(bytes, length);
    }

    private static String sha256(byte[] bytes) {
        try {
            return HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(bytes));
        } catch (NoSuchAlgorithmException e) {
            throw new IllegalStateException(e);
        }
    }

    /** The scenario input: the loan's records as the datasets hold them (the acceptance runner's legacyLoan). */
    private static ObjectNode input(Book book, String request, List<String> schedule, String reject) {
        ObjectNode in = JSON.createObjectNode();
        in.put("request", request);
        in.put("runControl", book.runControl());
        String currency = book.currencies().get(request.substring(26, 29));
        if (currency == null) in.putNull("currency");
        else in.put("currency", currency);
        ArrayNode s = in.putArray("schedule");
        schedule.forEach(s::add);
        if (reject == null) in.putNull("reject");
        else in.put("reject", reject);
        return in;
    }

    /** The acceptance runner's decoding of a scenario input (service/loans-acceptance Scenario.legacyLoan, copied). */
    static LegacyLoan loan(JsonNode in) {
        LegacyScheduleHeader header = null;
        List<LegacyScheduleLine> lines = new ArrayList<>();
        for (JsonNode r : in.path("schedule")) {
            LegacyScheduleRecord x = LegacyCodec.scheduleRecord(r.asText());
            if (x instanceof LegacyScheduleHeader h) header = h;
            else lines.add((LegacyScheduleLine) x);
        }
        return new LegacyLoan(LegacyCodec.request(in.get("request").asText()), LegacyCodec.runControl(in.get("runControl").asText()),
                in.path("currency").isTextual() ? LegacyCodec.currency(in.get("currency").asText()) : null, header, lines,
                in.path("reject").isTextual() ? LegacyCodec.reject(in.get("reject").asText()) : null);
    }

    private static Candidate add(Map<String, Candidate> all, String book, String loanId, ObjectNode input, LegacyLoan loan, Migration reference,
            ScheduleService quotes, String origin, boolean probe, List<String> problems, Map<MigrationMutants.Mutant, Integer> kills) {
        String key = book + "|" + loanId + "|" + input;
        if (all.containsKey(key)) return null;
        JsonNode terms = Canon.of(reference.terms(loan));
        JsonNode reconciliation = Canon.of(reference.reconcile(loan));
        if (!terms.equals(Canon.of(MigrationMutants.terms(loan, null))) || !reconciliation.equals(Canon.of(MigrationMutants.reconcile(loan, quotes, null)))) {
            problems.add("the mutants' base differs from the reference on " + origin);
        }
        Set<String> tags = new TreeSet<>();
        for (MigrationMutants.Mutant m : MigrationMutants.Mutant.values()) {
            if (!terms.equals(Canon.of(MigrationMutants.terms(loan, m))) || !reconciliation.equals(Canon.of(MigrationMutants.reconcile(loan, quotes, m)))) {
                tags.add(m.rule());
                kills.merge(m, 1, Integer::sum);
            }
        }
        Candidate c = new Candidate(key, book, loanId, input, terms, reconciliation, origin, probe, tags);
        all.put(key, c);
        return c;
    }

    /** The schedule pool classified the same loans from the same outputs: its D-2 decisions and its rejections must agree. */
    private static void crossCheck(JsonNode pool, Map<String, Map<String, Integer>> byBook, List<String[]> explainedD2, List<String> problems) {
        Set<String> poolD2 = new TreeSet<>();
        for (JsonNode d : pool.path("decisions").path("D-2")) poolD2.add(d.asText());
        Set<String> ours = new TreeSet<>();
        for (String[] e : explainedD2) ours.add(e[0] + "-" + e[1]);
        for (String book : byBook.keySet()) {
            Set<String> theirs = new TreeSet<>(poolD2.stream().filter(x -> x.startsWith(book + "-")).toList());
            Set<String> mine = new TreeSet<>(ours.stream().filter(x -> x.startsWith(book + "-")).toList());
            if (!theirs.equals(mine)) problems.add("cross-check " + book + ": the schedule pool's D-2 loans " + theirs + ", EXPLAINED D-2 " + mine);
            JsonNode counts = pool.path("scenarios").path(book);
            if (counts.isMissingNode() || counts.path("setAside").asInt() > 0) continue;
            Map<String, Integer> c = byBook.get(book);
            int rejected = c.getOrDefault("LEGACY_REJECT", 0) + c.getOrDefault("EXPLAINED D-3", 0);
            int scheduled = c.getOrDefault("MATCH", 0) + c.getOrDefault("EXPLAINED D-2", 0);
            if (rejected != counts.path("rejected").asInt() || scheduled != counts.path("scheduled").asInt()) {
                problems.add("cross-check " + book + ": the schedule pool has " + counts.path("scheduled").asInt() + " scheduled and "
                        + counts.path("rejected").asInt() + " rejected, the reconciliation " + scheduled + " and " + rejected);
            }
        }
    }

    // ------------------------------------------------------------------------------------------------ SME probes

    /** An amount field (S9(13)V9(4) SIGN LEADING SEPARATE, 18 characters) moved by delta. */
    private static String amount(String r, int offset, BigDecimal delta) {
        BigDecimal v = new BigDecimal(r.substring(offset + 1, offset + 18)).movePointLeft(4);
        if (r.charAt(offset) == '-') v = v.negate();
        v = v.add(delta);
        String digits = v.abs().movePointRight(4).setScale(0).toPlainString();
        return r.substring(0, offset) + (v.signum() < 0 ? "-" : "+") + "0".repeat(17 - digits.length()) + digits + r.substring(offset + 18);
    }

    private static String put(String r, int offset, String text) {
        return r.substring(0, offset) + text + r.substring(offset + text.length());
    }

    private static String reject(String loanId, String code) {
        String r = loanId + code + String.format("%-24s", "PROBE") + String.format("%-60s", "an SME probe");
        return r + " ".repeat(LegacyCodec.REJECT_LENGTH - r.length());
    }

    private static void probes(Map<String, Candidate> all, Book book, Candidate base, Migration reference, ScheduleService quotes, List<String> problems,
            Map<MigrationMutants.Mutant, Integer> kills) {
        String q = base.input().get("request").asText();
        List<String> sched = new ArrayList<>();
        base.input().get("schedule").forEach(s -> sched.add(s.asText()));
        BigDecimal cent = new BigDecimal("0.01");
        // offsets of FN-SCHEDULE-REC (legacy-model.json): SH-TOT-INTEREST 72; SP-DUE-DATE 33, SP-INTEREST 59, SP-OUTST-BALANCE 131
        record Probe(String what, String outcome, UnaryOperator<List<String>> schedule, UnaryOperator<String> request, String reject) {
        }
        List<Probe> probes = List.of(
                new Probe("the legacy's interest one hundredth higher on the second installment", "DIFF",
                        s -> with(s, 3, r -> amount(r, 59, cent)), x -> x, null),
                new Probe("the legacy's interest total and the second installment's interest one hundredth higher", "DIFF",
                        s -> with(with(s, 0, r -> amount(r, 72, cent)), 3, r -> amount(r, 59, cent)), x -> x, null),
                new Probe("the legacy's disbursement line balance one hundredth lower", "DIFF",
                        s -> with(s, 1, r -> amount(r, 131, cent.negate())), x -> x, null),
                new Probe("a due date of the legacy's first installment a day later", "DIFF",
                        s -> with(s, 2, r -> put(r, 33, java.time.LocalDate.parse(LegacyCodec.scheduleLine(r).dueDate().toString()).plusDays(1).toString()
                                .replace("-", ""))), x -> x, null),
                new Probe("the legacy schedule one installment short", "DIFF", s -> new ArrayList<>(s.subList(0, s.size() - 1)), x -> x, null),
                new Probe("the request's annual rate 150% (the service rejects it, the legacy scheduled it)", "DIFF", s -> s,
                        x -> put(x, 73, "+150000000000"), null),
                new Probe("the frequency MONTHLY, rejected by the legacy (RJFREQ01)", "EXPLAINED", s -> List.of(), x -> put(x, 63, "MONTHLY   "), "RJFREQ01"),
                new Probe("the frequency MONTHLY and the interest method SIMPLE, rejected by the legacy (RJFREQ01)", "EXPLAINED", s -> List.of(),
                        x -> put(put(x, 63, "MONTHLY   "), 143, "SIMPLE            "), "RJFREQ01"),
                new Probe("31 days in a month, rejected by the legacy (RJUNSP02)", "EXPLAINED", s -> List.of(), x -> put(x, 87, "31"), "RJUNSP02"),
                new Probe("0 days in a year, rejected by the legacy (RJARIT01)", "EXPLAINED", s -> List.of(), x -> put(x, 89, "000"), "RJARIT01"),
                new Probe("a currency the currency file does not have (XXX), rejected by the legacy (RJENV010)", "EXPLAINED", s -> List.of(),
                        x -> put(x, 26, "XXX"), "RJENV010"),
                new Probe("1,500 installments: the legacy envelope (RJENV020) and the service's limit both reject it", "LEGACY_REJECT", s -> List.of(),
                        x -> put(x, 55, "1500"), "RJENV020"),
                new Probe("a principal of 2,000,000,000: the legacy envelope rejects it (RJENV030), the service schedules it (D-6)", "EXPLAINED",
                        s -> List.of(), x -> amountRequest(x), "RJENV030"),
                new Probe("a legacy reject the service does not share (RJTNNT01 on a declining-balance loan)", "DIFF", s -> List.of(), x -> x, "RJTNNT01"));
        int n = 0;
        for (Probe p : probes) {
            n++;
            String request = p.request().apply(q);
            String loanId = request.substring(2, 18);
            ObjectNode input = input(book, request, p.schedule().apply(sched), p.reject() == null ? null : reject(loanId, p.reject()));
            LegacyLoan loan = loan(input);
            Candidate c = add(all, book.name(), loanId + "-probe-" + n, input, loan, reference, quotes, "SME probe: " + p.what(), true, problems, kills);
            if (c != null && !p.outcome().equals(c.reconciliation().path("outcome").asText())) {
                problems.add("probe '" + p.what() + "' aims at " + p.outcome() + " but the reference gives " + c.reconciliation());
            }
        }
        // a run control whose rounding mode the API does not have: the legacy run stopped (no schedule, no reject)
        ObjectNode input = input(book, q, List.of(), null);
        input.put("runControl", put(book.runControl(), 4, "HALF_ODD  "));
        Candidate c = add(all, book.name(), q.substring(2, 18) + "-probe-mode", input, loan(input), reference, quotes,
                "SME probe: the run's rounding mode HALF_ODD (the legacy run stopped: no schedule, no reject)", true, problems, kills);
        if (c != null && !"EXPLAINED".equals(c.reconciliation().path("outcome").asText())) problems.add("probe rounding mode: " + c.reconciliation());
    }

    /** RQ-DISB-AMOUNT (offset 29, S9(13)V9(4)) at 2,000,000,000.0000: 13 integer digits, then 4 decimals. */
    /**
     * Two request records that differ only in the inputs the service does not carry (D-4): the rounding multiples of
     * installments (106-115), interest recognition (122), the days-in-year strategy (123-142) and partial periods (161).
     */
    private static boolean d4Only(String a, String b) {
        for (int i = 0; i < a.length(); i++) {
            boolean d4 = i >= 106 && i < 116 || i >= 122 && i < 143 || i == 161;
            if (!d4 && a.charAt(i) != b.charAt(i)) return false;
        }
        return true;
    }

    /** A date field (yyyymmdd) one day later. */
    private static String dayLater(String r, int offset) {
        java.time.LocalDate d = java.time.LocalDate.parse(r.substring(offset, offset + 8), java.time.format.DateTimeFormatter.BASIC_ISO_DATE);
        return put(r, offset, d.plusDays(1).format(java.time.format.DateTimeFormatter.BASIC_ISO_DATE));
    }

    /** An unsigned number field one higher. */
    private static String plusOne(String r, int offset, int length) {
        return put(r, offset, String.format("%0" + length + "d", Long.parseLong(r.substring(offset, offset + length)) + 1));
    }

    /**
     * One probe per field MIG-11 compares (FE 7.7: each comparison must be seen by a scenario): the legacy record with that
     * field altered, on a plain loan (header, the disbursement line, an installment line), a loan with a down payment line,
     * and the legacy's own D-2 loan with an interest altered (a difference D-2 does not explain). Offsets are those of
     * FN-SCHEDULE-REC (legacy-model.json): header 21 currency, 24 decimal places, 26 term days, 36 + 18 i the totals; a line
     * 16 its type, 21 its number, 25 from, 33 due, 41 + 18 i its amounts (principal, interest, fee, penalty, total due,
     * balance, remaining due).
     */
    private static void fieldProbes(Map<String, Candidate> all, Book book, Candidate base, Book pBook, Candidate withP, Book d2Book, Candidate d2,
            Migration reference, ScheduleService quotes, List<String> problems, Map<MigrationMutants.Mutant, Integer> kills) {
        BigDecimal cent = new BigDecimal("0.01");
        String[] totals = {"disbursed", "principal", "interest", "fee", "penalty", "repayment", "outstanding"};
        String[] amounts = {"principal", "interest", "fee", "penalty", "total due", "balance", "remaining due"};
        record Field(String what, Candidate on, Book in, int index, UnaryOperator<String> change, String aim) {
        }
        List<Field> fields = new ArrayList<>();
        fields.add(new Field("the header's currency", base, book, 0, r -> put(r, 21, "ZZZ"), "DIFF"));
        fields.add(new Field("the header's decimal places", base, book, 0, r -> put(r, 24, "03"), "DIFF"));
        fields.add(new Field("the header's term days", base, book, 0, r -> plusOne(r, 26, 6), "DIFF"));
        for (int i = 0; i < totals.length; i++) {
            int at = 36 + 18 * i;
            fields.add(new Field("the total " + totals[i], base, book, 0, r -> amount(r, at, cent), "DIFF"));
        }
        fields.add(new Field("the disbursement line's from date", base, book, 1, r -> dayLater(r, 25), "DIFF"));
        fields.add(new Field("the disbursement line's due date", base, book, 1, r -> dayLater(r, 33), "DIFF"));
        fields.add(new Field("the disbursement line's amount", base, book, 1, r -> amount(r, 41, cent), "DIFF"));
        fields.add(new Field("the first installment's type (R written as P)", base, book, 2, r -> put(r, 16, "P"), "DIFF"));
        fields.add(new Field("the first installment's number", base, book, 2, r -> plusOne(r, 21, 4), "DIFF"));
        fields.add(new Field("the first installment's from date", base, book, 2, r -> dayLater(r, 25), "DIFF"));
        for (int i = 0; i < amounts.length; i++) {
            int at = 41 + 18 * i;
            fields.add(new Field("the first installment's " + amounts[i], base, book, 2, r -> amount(r, at, cent), i == 5 ? "EXPLAINED" : "DIFF"));
        }
        fields.add(new Field("the down payment line's number", withP, pBook, 2, r -> plusOne(r, 21, 4), "DIFF"));
        fields.add(new Field("the down payment line's from date", withP, pBook, 2, r -> dayLater(r, 25), "DIFF"));
        fields.add(new Field("the down payment line's due date", withP, pBook, 2, r -> dayLater(r, 33), "DIFF"));
        for (int i : new int[] {0, 4, 5, 6}) {
            int at = 41 + 18 * i;
            fields.add(new Field("the down payment line's " + amounts[i], withP, pBook, 2, r -> amount(r, at, cent), i == 5 ? "EXPLAINED" : "DIFF"));
        }
        fields.add(new Field("an interest of the D-2 loan (a difference D-2 does not explain)", d2, d2Book, 2, r -> amount(r, 59, cent), "DIFF"));
        int n = 0;
        for (Field f : fields) {
            n++;
            List<String> sched = new ArrayList<>();
            f.on().input().get("schedule").forEach(s -> sched.add(s.asText()));
            if (f.index() >= sched.size()) {
                problems.add("field probe '" + f.what() + "': the base loan has no record " + f.index());
                continue;
            }
            sched.set(f.index(), f.change().apply(sched.get(f.index())));
            String q = f.on().input().get("request").asText();
            ObjectNode input = input(f.in(), q, sched, null);
            Candidate c = add(all, f.in().name(), q.substring(2, 18) + "-field-" + n, input, loan(input), reference, quotes,
                    "SME probe: " + f.what() + " altered in the legacy schedule", true, problems, kills);
            if (c != null && !f.aim().equals(c.reconciliation().path("outcome").asText())) {
                problems.add("field probe '" + f.what() + "' aims at " + f.aim() + " but the reference gives " + c.reconciliation());
            }
        }
        // a legacy schedule without its disbursement line that the service schedules (MIG-09's DIFF)
        List<String> sched = new ArrayList<>();
        base.input().get("schedule").forEach(s -> sched.add(s.asText()));
        sched.remove(1);
        String q = base.input().get("request").asText();
        ObjectNode input = input(book, q, sched, null);
        Candidate c = add(all, book.name(), q.substring(2, 18) + "-field-noD", input, loan(input), reference, quotes,
                "SME probe: the legacy schedule without its disbursement line, a loan the service schedules", true, problems, kills);
        if (c != null && !"DIFF".equals(c.reconciliation().path("outcome").asText())) problems.add("probe without D: " + c.reconciliation());
    }

    private static String amountRequest(String q) {
        return put(q, 29, "+" + "0002000000000" + "0000");
    }

    private static List<String> with(List<String> s, int i, UnaryOperator<String> change) {
        List<String> out = new ArrayList<>(s);
        out.set(i, change.apply(out.get(i)));
        return out;
    }

    // ------------------------------------------------------------------------------------------------ output

    private static String scenario(String id, Candidate c) throws IOException {
        ObjectNode termsInput = JSON.createObjectNode();
        termsInput.set("request", c.input().get("request"));
        termsInput.set("runControl", c.input().get("runControl"));
        termsInput.set("currency", c.input().get("currency"));
        return "{\n  \"id\": " + JSON.writeValueAsString(id) + ",\n  \"slice\": \"" + SLICE + "\",\n  \"origin\": " + JSON.writeValueAsString(c.origin())
                + ",\n  \"rules\": " + JSON.writeValueAsString(c.tags()) + ",\n  \"steps\": [ {\n    \"call\": \"legacyTerms\",\n    \"input\": "
                + JSON.writeValueAsString(termsInput) + ",\n    \"expected\": " + JSON.writeValueAsString(c.terms()) + "\n  }, {\n    \"call\": \"reconcile\",\n"
                + "    \"input\": " + JSON.writeValueAsString(c.input()) + ",\n    \"expected\": " + JSON.writeValueAsString(c.reconciliation()) + "\n  } ]\n}\n";
    }

    private static void writeReport(Path report, List<Candidate> all, Map<String, Map<String, Integer>> byBook, Map<String, Integer> undecodable,
            Map<MigrationMutants.Mutant, Integer> kills, SlicePools.Chosen<Candidate> chosen, List<String> problems) throws IOException {
        StringBuilder sb = new StringBuilder("# Migration pool (slice migration, S9 engine side): the reconciliation of the legacy book, and the selection\n\n");
        sb.append("Built by `MigrationPool` from `sme/legacy-books.json` (every loan of every book, its records as the legacy datasets hold them) and SME probes.\n");
        sb.append("Expected values: the reference (`loans-migration`) over `LoansApplication.quotes()`; cross-checked with the schedule pool's own\n");
        sb.append("classification of the same loans (D-2 decisions, scheduled and rejected counts).\n\n");
        sb.append("## The legacy book, reconciled (G10, engine level)\n\n| Book | Outcomes |\n| --- | --- |\n");
        Map<String, Integer> total = new TreeMap<>();
        byBook.forEach((book, counts) -> {
            sb.append("| ").append(book).append(" | ").append(counts).append(" |\n");
            counts.forEach((k, v) -> total.merge(k, v, Integer::sum));
        });
        sb.append("| **all** | ").append(total).append(" |\n\n");
        if (!undecodable.isEmpty()) {
            sb.append("Requests the generated codec cannot decode (the plugin reports them; D-5):\n\n");
            undecodable.forEach((k, v) -> sb.append("- ").append(k).append(": ").append(v).append('\n'));
            sb.append('\n');
        }
        sb.append("## Rule mutants\n\n| Rule | Mutant | Candidates that kill it | Chosen scenarios tagged | of them public |\n| --- | --- | --- | --- | --- |\n");
        for (MigrationMutants.Mutant m : MigrationMutants.Mutant.values()) {
            String rule = m.rule();
            long tagged = chosen.byId().values().stream().filter(c -> c.tags().contains(rule)).count();
            long pub = chosen.publicIds().stream().filter(id -> chosen.byId().get(id).tags().contains(rule)).count();
            sb.append("| ").append(rule).append(" | ").append(m.change).append(" | ").append(kills.getOrDefault(m, 0)).append(" | ").append(tagged)
                    .append(" | ").append(pub).append(" |\n");
        }
        sb.append("\n").append(all.size()).append(" candidates, ").append(all.stream().filter(Candidate::probe).count()).append(" of them probes; ")
                .append(chosen.byId().size()).append(" chosen, ").append(chosen.publicIds().size()).append(" public.\n\n");
        sb.append(problems.isEmpty() ? "No problems.\n" : "## Problems\n\n" + String.join("\n", problems.stream().map(p -> "- " + p).toList()) + "\n");
        Files.createDirectories(report.getParent());
        Files.writeString(report, sb.toString(), StandardCharsets.UTF_8);
    }
}
