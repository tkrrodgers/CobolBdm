package com.mlcpu.loans.sme;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.node.ArrayNode;
import com.fasterxml.jackson.databind.node.ObjectNode;
import com.fasterxml.jackson.databind.node.TextNode;
import com.mlcpu.loans.acceptance.memory.InMemoryPorts;
import com.mlcpu.loans.api.CurrencyTerms;
import com.mlcpu.loans.api.DownPaymentLine;
import com.mlcpu.loans.api.InstallmentLine;
import com.mlcpu.loans.api.Schedule;
import com.mlcpu.loans.api.ScheduleRequest;
import com.mlcpu.loans.api.ScheduleService;
import com.mlcpu.loans.api.Scheduled;
import com.mlcpu.loans.api.ports.LoansPorts;
import com.mlcpu.loans.api.ports.LoanStatus;
import com.mlcpu.loans.api.servicing.BillDue;
import com.mlcpu.loans.api.servicing.BillingRun;
import com.mlcpu.loans.api.servicing.BookLoan;
import com.mlcpu.loans.api.servicing.Booked;
import com.mlcpu.loans.api.servicing.BookingOutcome;
import com.mlcpu.loans.api.servicing.DisburseLoan;
import com.mlcpu.loans.api.servicing.Disbursed;
import com.mlcpu.loans.api.servicing.InstallmentBilled;
import com.mlcpu.loans.api.servicing.InvoicePaid;
import com.mlcpu.loans.api.servicing.LoanServicing;
import com.mlcpu.loans.application.LoansApplication;
import com.mlcpu.loans.application.ServicingM;
import com.mlcpu.loans.application.ServicingMutant;
import java.io.BufferedReader;
import java.io.IOException;
import java.math.BigDecimal;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.LocalDate;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.EnumMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.TreeMap;
import java.util.TreeSet;
import java.util.concurrent.ConcurrentHashMap;
import java.util.function.Function;
import java.util.stream.Stream;

/**
 * The servicing pool (slice S7, ForwardEngineeringFineract.md section 7.4: SME-authored flows). Each scenario is a flow of
 * servicing commands over in-memory ports (service/loans-acceptance/src/memory) on a loan of the S5 pool: its whole life (book,
 * disburse, bill and collect line by line), a batch billing, the idempotent repeats, a voided payment, the booking rejections
 * (the schedule's and the ledger's), unknown ids and two loans billed together. There is no legacy oracle: the expected outputs
 * and final state are the reference's, checked by invariants that do not use the servicing code (a paid-off loan's account is
 * 0; its invoices bill the schedule's principal and interest; every invoice is dated its line's due date). One mutant per SRV and
 * IDEM rule (generated, tools/make_servicing_mutants.py) must be killed; tags by killed mutants; selection and split as
 * SlicePools.
 *
 * <p>Usage: ServicingPool &lt;pool/schedule&gt; &lt;scenario root&gt; &lt;scenarios.json&gt; &lt;report.md&gt; [loans per group 2]
 */
public final class ServicingPool {

    private static final ObjectMapper JSON = new ObjectMapper();
    private static final String SLICE = "servicing";
    static final List<String> RATES_TO_BASE = List.of("EUR", "INR", "JPY", "ZAR");

    record Step(String call, JsonNode input, JsonNode expected) {
    }

    record Flow(String id, String kind, String origin, JsonNode given, List<Step> steps, JsonNode state, Set<String> tags, InMemoryPorts.State finalState) {
    }

    /** Builds a flow by running the reference step by step (the ids a later step needs come from earlier outputs). */
    static final class Builder {
        final InMemoryPorts memory;
        final LoanServicing servicing;
        final List<Step> steps = new ArrayList<>();
        final JsonNode given;

        Builder(LocalDate today, String base) {
            InMemoryPorts.Given g = new InMemoryPorts.Given(today, base, RATES_TO_BASE.stream().filter(c -> !c.equals(base)).toList());
            given = Canon.of(g);
            memory = new InMemoryPorts(g);
            servicing = LoansApplication.create(memory.ports());
        }

        Object call(String call, Object input) {
            Object out = execute(servicing, memory, call, input);
            steps.add(new Step(call, call.equals("today") ? TextNode.valueOf(input.toString()) : Canon.of(input), Canon.of(out)));
            return out;
        }
    }

    static Object execute(LoanServicing s, InMemoryPorts memory, String call, Object input) {
        return switch (call) {
            case "today" -> {
                memory.today((LocalDate) input);
                yield input.toString();
            }
            case "book" -> s.book((BookLoan) input);
            case "disburse" -> s.disburse((DisburseLoan) input);
            case "billDue" -> s.billDue((BillDue) input);
            case "invoicePaid" -> s.invoicePaid((InvoicePaid) input);
            default -> throw new IllegalArgumentException(call);
        };
    }

    /** Replays a flow's commands on fresh ports with a servicing made by the factory; the outputs and the final state, as JSON. */
    static List<JsonNode> replay(Flow f, Function<LoansPorts, LoanServicing> make) {
        JsonNode g = f.given();
        List<String> rates = new ArrayList<>();
        g.get("ratesToBase").forEach(x -> rates.add(x.asText()));
        InMemoryPorts memory = new InMemoryPorts(new InMemoryPorts.Given(LocalDate.parse(g.get("today").asText()), g.get("baseCurrency").asText(), rates));
        LoanServicing s = make.apply(memory.ports());
        List<JsonNode> out = new ArrayList<>();
        for (Step st : f.steps()) {
            Object input = switch (st.call()) {
                case "today" -> LocalDate.parse(st.input().asText());
                case "book" -> book(st.input());
                case "disburse" -> new DisburseLoan(st.input().get("loanId").asText(), st.input().get("paymentMethodId").asText());
                case "billDue" -> new BillDue(LocalDate.parse(st.input().get("asOf").asText()));
                case "invoicePaid" -> new InvoicePaid(st.input().get("invoiceId").asText(), st.input().get("paid").asBoolean());
                default -> throw new IllegalArgumentException(st.call());
            };
            try {
                Object r = execute(s, memory, st.call(), input);
                out.add(st.call().equals("today") ? TextNode.valueOf(r.toString()) : Canon.of(r));
            } catch (RuntimeException e) {
                out.add(JSON.createObjectNode().put("exception", e.toString()));
            }
        }
        out.add(Canon.of(memory.state()));
        return out;
    }

    static BookLoan book(JsonNode in) {
        return new BookLoan(in.get("externalId").asText(), in.get("ownerPartyId").asText(), in.get("organizationPartyId").asText(),
                in.get("loanProductId").asText(), PoolRunner.request(in.get("terms")));
    }

    static BookLoan bookLoan(String externalId, ScheduleRequest terms) {
        return new BookLoan(externalId, "BORROWER-1", "ORG-1", "PRODUCT-1", terms);
    }

    static ScheduleRequest with(ScheduleRequest q, BigDecimal principal, BigDecimal rate) {
        return new ScheduleRequest(q.loanId(), q.scheduleStart(), q.currency(), principal, q.disbursementDate(), q.installments(), q.every(), q.frequency(),
                rate, q.downPaymentPercent(), q.downPaymentEnabled(), q.monthDays(), q.yearDays(), q.fixedLength(), q.interestMethod(), q.fullTermTranche(),
                q.calculation());
    }

    // ---------------------------------------------------------------- the flows

    static Flow life(String id, ScheduleRequest q, String origin) {
        Builder b = new Builder(q.disbursementDate().minusDays(3), "USD");
        Booked booked = (Booked) b.call("book", bookLoan("E-" + id, q));
        b.call("disburse", new DisburseLoan(booked.loanId(), "PM-1"));
        Schedule s = scheduled(q);
        for (var line : s.lines()) {
            if (!(line instanceof InstallmentLine i)) continue;
            b.call("today", i.due());
            BillingRun run = (BillingRun) b.call("billDue", new BillDue(i.due()));
            for (InstallmentBilled x : run.billed()) b.call("invoicePaid", new InvoicePaid(x.invoiceId(), true));
        }
        if (b.memory.state().loans().get(0).lines().stream().anyMatch(l -> l.invoiceId() != null && l.collectionTransactionId() == null)) {
            // the down payment's invoice, billed at disbursement, is paid last
            b.memory.state().loans().get(0).lines().stream().filter(l -> l.invoiceId() != null && l.collectionTransactionId() == null)
                    .forEach(l -> b.call("invoicePaid", new InvoicePaid(l.invoiceId(), true)));
        }
        return flow(id, "life", origin, b);
    }

    static Flow batch(String id, ScheduleRequest q, String origin) {
        Builder b = new Builder(q.disbursementDate(), "USD");
        Booked booked = (Booked) b.call("book", bookLoan("E-" + id, q));
        b.call("billDue", new BillDue(q.disbursementDate().plusYears(100)));
        b.call("disburse", new DisburseLoan(booked.loanId(), "PM-7"));
        Schedule s = scheduled(q);
        LocalDate last = s.lines().get(s.lines().size() - 1).due();
        b.call("today", last.plusDays(1));
        BillingRun run = (BillingRun) b.call("billDue", new BillDue(last.plusDays(1)));
        for (int k = 0; k < run.billed().size(); k += 2) b.call("invoicePaid", new InvoicePaid(run.billed().get(k).invoiceId(), true));
        return flow(id, "batch", origin, b);
    }

    static Flow repeats(String id, ScheduleRequest q, String origin) {
        Builder b = new Builder(q.disbursementDate(), "USD");
        Booked booked = (Booked) b.call("book", bookLoan("E-" + id, q));
        b.call("book", bookLoan("E-" + id, with(q, q.principal().add(BigDecimal.ONE), q.annualRatePercent())));
        Disbursed d = (Disbursed) b.call("disburse", new DisburseLoan(booked.loanId(), "PM-1"));
        b.call("disburse", new DisburseLoan(booked.loanId(), "PM-2"));
        Schedule s = scheduled(q);
        LocalDate first = s.lines().stream().filter(l -> l instanceof InstallmentLine).findFirst().orElseThrow().due();
        b.call("today", first);
        BillingRun run = (BillingRun) b.call("billDue", new BillDue(first));
        b.call("billDue", new BillDue(first));
        if (!run.billed().isEmpty()) {
            String inv = run.billed().get(0).invoiceId();
            b.call("invoicePaid", new InvoicePaid(inv, false));
            b.call("invoicePaid", new InvoicePaid(inv, true));
            b.call("invoicePaid", new InvoicePaid(inv, true));
        }
        if (d.downPaymentInvoiceId() != null) b.call("invoicePaid", new InvoicePaid(d.downPaymentInvoiceId(), true));
        return flow(id, "repeats", origin, b);
    }

    static Flow voided(String id, ScheduleRequest q, String origin) {
        Builder b = new Builder(q.disbursementDate(), "USD");
        Booked booked = (Booked) b.call("book", bookLoan("E-" + id, q));
        Disbursed d = (Disbursed) b.call("disburse", new DisburseLoan(booked.loanId(), "PM-1"));
        Schedule s = scheduled(q);
        LocalDate last = s.lines().get(s.lines().size() - 1).due();
        b.call("today", last);
        BillingRun run = (BillingRun) b.call("billDue", new BillDue(last));
        List<String> invoices = new ArrayList<>();
        if (d.downPaymentInvoiceId() != null) invoices.add(d.downPaymentInvoiceId());
        run.billed().forEach(x -> invoices.add(x.invoiceId()));
        for (String inv : invoices) b.call("invoicePaid", new InvoicePaid(inv, true));
        if (!invoices.isEmpty()) {
            String lastInvoice = invoices.get(invoices.size() - 1);
            b.call("today", last.plusDays(5));
            b.call("invoicePaid", new InvoicePaid(lastInvoice, false));
            b.call("invoicePaid", new InvoicePaid(lastInvoice, false));
            b.call("today", last.plusDays(9));
            b.call("invoicePaid", new InvoicePaid(lastInvoice, true));
            // voided once more: the loan ends active (SRV-09), with the other lines still collected
            b.call("today", last.plusDays(12));
            b.call("invoicePaid", new InvoicePaid(lastInvoice, false));
        }
        return flow(id, "voided", origin, b);
    }

    /** A booking rejected (the schedule's or the ledger's reason), then booked with terms that pass where they can. */
    static Flow rejected(String id, ScheduleRequest q, String base, ScheduleRequest fixed, String origin) {
        Builder b = new Builder(q.disbursementDate(), base);
        b.call("book", bookLoan("E-" + id, q));
        b.call("disburse", new DisburseLoan("LN-E-" + id, "PM-1"));
        if (fixed != null) {
            b.call("book", bookLoan("E-" + id, fixed));
            b.call("disburse", new DisburseLoan("LN-E-" + id, "PM-1"));
        }
        b.call("invoicePaid", new InvoicePaid("INV-9999", true));
        return flow(id, "rejected", origin, b);
    }

    static Flow two(String id, ScheduleRequest a, ScheduleRequest c, String origin) {
        Builder b = new Builder(a.disbursementDate().isBefore(c.disbursementDate()) ? a.disbursementDate() : c.disbursementDate(), "USD");
        Booked ba = (Booked) b.call("book", bookLoan("E-" + id + "-Z", a));
        Booked bc = (Booked) b.call("book", bookLoan("E-" + id + "-A", c));
        b.call("disburse", new DisburseLoan(ba.loanId(), "PM-1"));
        b.call("disburse", new DisburseLoan(bc.loanId(), "PM-1"));
        LocalDate last = Stream.of(scheduled(a), scheduled(c)).map(s -> s.lines().get(s.lines().size() - 1).due()).max(Comparator.naturalOrder()).orElseThrow();
        b.call("today", last);
        BillingRun run = (BillingRun) b.call("billDue", new BillDue(last));
        for (InstallmentBilled x : run.billed()) if (x.number() % 2 == 1) b.call("invoicePaid", new InvoicePaid(x.invoiceId(), true));
        return flow(id, "two", origin, b);
    }

    static Schedule scheduled(ScheduleRequest q) {
        return ((Scheduled) QUOTES.schedule(q)).schedule();
    }

    static final ScheduleService QUOTES = LoansApplication.quotes();

    static Flow flow(String id, String kind, String origin, Builder b) {
        InMemoryPorts.State st = b.memory.state();
        return new Flow(id, kind, origin, b.given, List.copyOf(b.steps), Canon.of(st), new TreeSet<>(), st);
    }

    // ---------------------------------------------------------------- invariants (independent of the servicing code)

    static void check(Flow f, InMemoryPorts.State state, List<String> problems) {
        for (InMemoryPorts.LoanState l : state.loans()) {
            List<InMemoryPorts.Invoice> invs = state.invoices().stream().filter(i -> i.invoice().loanId().equals(l.loanId())).toList();
            for (InMemoryPorts.Invoice i : invs) {
                var line = l.lines().stream().filter(x -> x.number() == i.invoice().lineNumber()).findFirst().orElse(null);
                if (line == null || !i.invoice().principal().equals(line.principal()) || !i.invoice().interest().equals(line.interest())) {
                    problems.add(f.id() + ": invoice " + i.invoiceId() + " does not bill its line");
                } else if (!i.invoice().invoiceDate().equals(line.due()) && !i.invoice().invoiceDate().equals(line.billedOn())) {
                    problems.add(f.id() + ": invoice " + i.invoiceId() + " is dated " + i.invoice().invoiceDate() + ", its line is due " + line.due());
                }
            }
            if (l.status() == LoanStatus.PAID_OFF) {
                var acc = state.accounts().stream().filter(a -> a.accountId().equals(l.accountId())).findFirst().orElseThrow();
                if (acc.balance().signum() != 0) problems.add(f.id() + ": paid off, but the account's balance is " + acc.balance());
                BigDecimal billed = invs.stream().map(i -> i.invoice().principal()).reduce(BigDecimal.ZERO, BigDecimal::add);
                BigDecimal lines = l.lines().stream().map(x -> x.principal()).reduce(BigDecimal.ZERO, BigDecimal::add);
                if (billed.compareTo(lines) != 0) problems.add(f.id() + ": paid off, invoices bill " + billed + " of principal " + lines);
            }
        }
    }

    // ---------------------------------------------------------------- the pool

    public static void main(String[] args) throws IOException {
        Path pool = Path.of(args[0]);
        Path scenarioRoot = Path.of(args[1]);
        Path manifest = Path.of(args[2]);
        Path report = Path.of(args[3]);
        int perGroup = args.length > 4 ? Integer.parseInt(args[4]) : 2;
        // the loans: scheduled and rejected S5 loans of at most 12 installments, a few per shape
        Map<String, List<JsonNode>> groups = new TreeMap<>();
        List<Path> files;
        try (Stream<Path> s = Files.list(pool)) {
            files = s.filter(p -> p.toString().endsWith(".jsonl")).sorted().toList();
        }
        for (Path f : files) {
            try (BufferedReader r = Files.newBufferedReader(f, StandardCharsets.UTF_8)) {
                String line;
                while ((line = r.readLine()) != null) {
                    JsonNode sc = JSON.readTree(line);
                    JsonNode in = sc.get("steps").get(0).get("input");
                    JsonNode exp = sc.get("steps").get(0).get("expected");
                    if (in.get("installments").asInt() > 12) continue;
                    String shape = exp.path("@type").asText() + "|" + exp.path("reason").asText() + "|" + in.get("currency").get("code").asText() + "|"
                            + in.get("frequency").asText() + "|" + in.get("downPaymentEnabled").asBoolean() + "|"
                            + (exp.toString().contains("\"totalDue\":\"0") ? "zero-line" : "");
                    List<JsonNode> g = groups.computeIfAbsent(shape, k -> new ArrayList<>());
                    if (g.size() < perGroup) g.add(sc);
                }
            }
        }
        List<Flow> flows = new ArrayList<>();
        List<String> problems = new ArrayList<>();
        int n = 0;
        List<ScheduleRequest> scheduledLoans = new ArrayList<>();
        for (var e : groups.entrySet()) {
            for (JsonNode sc : e.getValue()) {
                ScheduleRequest q = PoolRunner.request(sc.get("steps").get(0).get("input"));
                String origin = "the S5 loan " + sc.get("id").asText() + "; expected values: the reference over the in-memory ports";
                String id = String.format("%04d", ++n);
                try {
                    if (e.getKey().startsWith("Rejected")) {
                        flows.add(rejected(id, q, "USD", null, origin));
                        continue;
                    }
                    scheduledLoans.add(q);
                    int dp = q.currency().decimalPlaces();
                    String code = q.currency().code();
                    if (dp > 2) {
                        flows.add(rejected(id, q, code, null, origin + " (decimal places above the ledger's)"));
                        continue;
                    }
                    try {
                        switch (n % 4) {
                            case 0 -> flows.add(life(id, q, origin));
                            case 1 -> flows.add(batch(id, q, origin));
                            case 2 -> flows.add(repeats(id, q, origin));
                            default -> flows.add(voided(id, q, origin));
                        }
                    } catch (ClassCastException notBooked) {
                        // the ledger refuses the loan (SRV-02): its flow is the rejection's
                        flows.add(rejected(id, q, "USD", null, origin + " (the ledger refuses it)"));
                        continue;
                    }
                    if (n % 5 == 0) flows.add(rejected(id + "-R", with(q, q.principal(), q.annualRatePercent().add(new BigDecimal("0.0000001"))), "USD", q,
                            origin + " with a rate of 7 decimal places"));
                    if (n % 7 == 0) flows.add(rejected(id + "-C", with(q, q.principal().multiply(BigDecimal.valueOf(2_000_000_000L)), q.annualRatePercent()),
                            "USD", q, origin + " with a principal beyond the ledger's capacity"));
                    if (n % 6 == 0 && !code.equals("USD")) {
                        flows.add(rejected(id + "-X", q, "GBP", null, origin + " for an organization whose base currency is GBP, without a rate from " + code));
                    }
                } catch (RuntimeException ex) {
                    problems.add("flow " + id + " (" + sc.get("id").asText() + "): " + ex);
                }
            }
        }
        for (int k = 0; k + 1 < scheduledLoans.size() && k < 40; k += 4) {
            ScheduleRequest a = scheduledLoans.get(k), c = scheduledLoans.get(k + 1);
            if (a.currency().decimalPlaces() <= 2 && c.currency().decimalPlaces() <= 2) {
                flows.add(two(String.format("T%03d", k), a, c, "two S5 loans billed together; expected values: the reference over the in-memory ports"));
            }
        }

        // invariants, and the mutants
        Map<ServicingMutant, Integer> kills = new ConcurrentHashMap<>();
        List<String> sync = Collections.synchronizedList(problems);
        List<Flow> tagged = flows.parallelStream().map(f -> {
            List<JsonNode> ref = replay(f, LoansApplication::create);
            List<JsonNode> expected = new ArrayList<>();
            f.steps().forEach(s -> expected.add(s.expected()));
            expected.add(f.state());
            if (!ref.equals(expected)) sync.add(f.id() + ": the replay differs from the flow as built");
            check(f, f.finalState(), sync);
            Set<String> tags = new TreeSet<>();
            for (ServicingMutant m : ServicingMutant.values()) {
                ServicingMutant.M.set(m);
                List<JsonNode> got;
                try {
                    got = replay(f, p -> new ServicingM(QUOTES, p));
                } finally {
                    ServicingMutant.M.remove();
                }
                if (!got.equals(expected)) {
                    tags.add(m.rule());
                    kills.merge(m, 1, Integer::sum);
                }
            }
            return new Flow(f.id(), f.kind(), f.origin(), f.given(), f.steps(), f.state(), tags, f.finalState());
        }).toList();
        for (ServicingMutant m : ServicingMutant.values()) {
            if (kills.getOrDefault(m, 0) == 0) problems.add("rule " + m.rule() + ": no flow kills its mutant (" + m.change + ")");
        }
        SlicePools.Chosen<Flow> chosen = SlicePools.choose(tagged, Flow::tags, Flow::kind, f -> false, 3, Comparator.comparing(Flow::kind).thenComparing(Flow::id),
                "srv", f -> f.steps().size(), "SRV-03");
        SlicePools.clear(scenarioRoot, SLICE);
        ArrayNode entries = JSON.createArrayNode();
        for (var e : chosen.byId().entrySet()) {
            String visibility = chosen.publicIds().contains(e.getKey()) ? "public" : "hidden";
            Files.writeString(scenarioRoot.resolve(visibility).resolve(SLICE).resolve(e.getKey() + ".json"), scenario(e.getKey(), e.getValue()),
                    StandardCharsets.UTF_8);
            SlicePools.entry(entries, e.getKey(), SLICE, visibility, chosen.primary().get(e.getKey()), e.getValue().tags(), e.getValue().origin());
        }
        SlicePools.writeManifest(manifest, SLICE, entries);
        Map<ServicingMutant, Integer> ordered = new EnumMap<>(ServicingMutant.class);
        ordered.putAll(kills);
        writeReport(report, groups.values().stream().mapToInt(List::size).sum(), tagged, ordered, chosen, problems);
        System.out.printf("servicing pool: %d flows, %d chosen, %d public; %d problem(s)%n", tagged.size(), chosen.byId().size(), chosen.publicIds().size(),
                problems.size());
        ordered.forEach((m, k) -> System.out.printf("  %s killed by %d flow(s)%n", m.rule(), k));
        problems.stream().limit(20).forEach(p -> System.out.println("  problem: " + p));
        if (!problems.isEmpty()) System.exit(1);
    }

    /** A scenario file: header fields one per line, then the given, each step on its own lines, and the state. */
    static String scenario(String id, Flow f) throws IOException {
        StringBuilder sb = new StringBuilder("{\n");
        sb.append("  \"id\": ").append(JSON.writeValueAsString(id)).append(",\n");
        sb.append("  \"slice\": \"").append(SLICE).append("\",\n");
        sb.append("  \"origin\": ").append(JSON.writeValueAsString(f.origin())).append(",\n");
        sb.append("  \"rules\": ").append(JSON.writeValueAsString(f.tags())).append(",\n");
        sb.append("  \"given\": ").append(JSON.writeValueAsString(f.given())).append(",\n");
        sb.append("  \"steps\": [");
        for (int i = 0; i < f.steps().size(); i++) {
            Step s = f.steps().get(i);
            sb.append(i == 0 ? "\n" : ",\n").append("    {\"call\": ").append(JSON.writeValueAsString(s.call())).append(", \"input\": ")
                    .append(JSON.writeValueAsString(s.input())).append(",\n     \"expected\": ").append(JSON.writeValueAsString(s.expected())).append("}");
        }
        sb.append("\n  ],\n  \"state\": ").append(JSON.writerWithDefaultPrettyPrinter().writeValueAsString(f.state()).replace("\n", "\n  ")).append("\n}\n");
        return sb.toString();
    }

    private static void writeReport(Path report, int loans, List<Flow> flows, Map<ServicingMutant, Integer> kills, SlicePools.Chosen<Flow> chosen,
            List<String> problems) throws IOException {
        StringBuilder sb = new StringBuilder("# Servicing pool (slice S7): verification and selection\n\n");
        sb.append("Built by `ServicingPool`: flows on ").append(loans).append(" loans of the S5 pool (at most 12 installments, a few per shape), run on the ")
                .append("reference over the in-memory ports (`service/loans-acceptance/src/memory`); invariants independent of the servicing code checked on ")
                .append("each (a paid-off loan's account is 0 and its invoices bill its lines' principal; every invoice bills its line, dated its due date).\n\n");
        Map<String, Integer> kinds = new LinkedHashMap<>();
        flows.forEach(f -> kinds.merge(f.kind(), 1, Integer::sum));
        sb.append("| | |\n| --- | --- |\n");
        kinds.forEach((k, v) -> sb.append("| Flows: ").append(k).append(" | ").append(v).append(" |\n"));
        sb.append("| Chosen | ").append(chosen.byId().size()).append(": ").append(chosen.publicIds().size()).append(" public |\n\n");
        sb.append("## Rule mutants (generated by `tools/make_servicing_mutants.py`)\n\n| Rule | Mutant | Flows that kill it | Chosen tagged | of them public |\n| --- | --- | --- | --- | --- |\n");
        for (ServicingMutant m : ServicingMutant.values()) {
            String rule = m.rule();
            long pub = chosen.publicIds().stream().filter(id -> chosen.byId().get(id).tags().contains(rule)).count();
            sb.append("| ").append(rule).append(" | ").append(m.change).append(" | ").append(kills.getOrDefault(m, 0)).append(" | ")
                    .append(chosen.ruleCount().getOrDefault(rule, 0)).append(" | ").append(pub).append(" |\n");
        }
        if (!problems.isEmpty()) {
            sb.append("\n## Problems\n\n");
            problems.forEach(p -> sb.append("- ").append(p).append('\n'));
        }
        Files.createDirectories(report.getParent());
        Files.writeString(report, sb.toString(), StandardCharsets.UTF_8);
    }
}
