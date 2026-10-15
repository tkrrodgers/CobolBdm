package com.mlcpu.loans.sme;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.SerializationFeature;
import com.fasterxml.jackson.databind.node.ArrayNode;
import com.fasterxml.jackson.databind.node.JsonNodeFactory;
import com.fasterxml.jackson.databind.node.ObjectNode;
import com.mlcpu.loans.acceptance.memory.InMemoryPorts;
import com.mlcpu.loans.api.CurrencyTerms;
import com.mlcpu.loans.api.InstallmentLine;
import com.mlcpu.loans.api.Schedule;
import com.mlcpu.loans.api.ScheduleRequest;
import com.mlcpu.loans.api.Scheduled;
import com.mlcpu.loans.api.ports.AccountTransactionType;
import com.mlcpu.loans.api.ports.LineServicing;
import com.mlcpu.loans.api.ports.LoanStatus;
import com.mlcpu.loans.api.servicing.BillDue;
import com.mlcpu.loans.api.servicing.BillingRun;
import com.mlcpu.loans.api.servicing.BookLoan;
import com.mlcpu.loans.api.servicing.Booked;
import com.mlcpu.loans.api.servicing.DisburseLoan;
import com.mlcpu.loans.api.servicing.Disbursed;
import com.mlcpu.loans.api.servicing.InstallmentBilled;
import com.mlcpu.loans.api.servicing.InvoicePaid;
import com.mlcpu.loans.api.servicing.LoanServicing;
import com.mlcpu.loans.application.LoansApplication;
import java.io.BufferedReader;
import java.io.IOException;
import java.math.BigDecimal;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.LocalDate;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.TreeMap;
import java.util.TreeSet;
import java.util.regex.Pattern;
import java.util.stream.Stream;

/**
 * The OFBiz host pool (slice S8, ForwardEngineeringFineract.md §9.1): scenarios (mlcpu.ofbiz-scenario.v1) that drive the plugin's
 * services inside OFBiz and check what OFBiz holds afterwards. Each flow is run twice, step for step: as OFBiz service calls (an
 * invoice is paid by a real OFBiz payment and its application, unpaid by voiding that payment), and through the reference over
 * the in-memory ports with the same ledger (base currency USD, rates for EUR, INR, JPY, ZAR: the demo organization's), whose
 * outcomes and final state are the expected values. In-memory ids (ACC-0001, PAY-0001, INV-0001, TRN-0001) become OFBiz ids
 * bound where a result first names them. The OFBiz rows each rule implies:
 * <ul>
 * <li>OFB-02: the loan, its servicing lines, a LOAN_ACCOUNT FinAccount for the borrower and the organization;</li>
 * <li>OFB-04: one WITHDRAWAL FinAccountTrans of the principal, APPROVED, dated the disbursement, linked to the payment, so the
 * account's actual balance is the deposits less the withdrawal;</li>
 * <li>OFB-05: per line, a SALES_INVOICE dated its due date with a LOAN_PRINCIPAL and an INV_INTRST_CHRG item (none for 0) on the
 * product's GL accounts, which OFBiz posts: credit 124000 and 810000, debit the receivable 120000;</li>
 * <li>OFB-06: a DISBURSEMENT payment from the organization, PMNT_SENT, overriding its GL account with 124000, which OFBiz posts:
 * debit 124000, credit the payment method's 111100;</li>
 * <li>OFB-07: a DEPOSIT of a line's principal when its invoice is paid, CANCELED when the payment is voided.</li>
 * </ul>
 * Plus the host-only rules: OFB-01 (a quote writes nothing), OFB-03 (an unknown enumeration id is an error naming the field and
 * writes nothing), OFB-09 (a user without accounting permission is refused).
 *
 * <p>Usage: OfbizPool &lt;pool/schedule&gt; &lt;service-model.json&gt; &lt;scenario root&gt; [loans per group 2]
 */
public final class OfbizPool {

    private static final ObjectMapper JSON = new ObjectMapper().enable(SerializationFeature.INDENT_OUTPUT);
    private static final JsonNodeFactory NODES = JsonNodeFactory.instance;
    static final List<String> RATES = List.of("EUR", "INR", "JPY", "ZAR");
    static final String ORG = "Company";
    static final String BORROWER = "LOAN_BORROWER_1";
    static final String PRODUCT = "LOAN_STD";
    static final String METHOD = "SC_CHECKING";
    static final String CASH_GL = "111100";
    static final String RECEIVABLE_GL = "124000";
    static final String INTEREST_GL = "810000";
    static final String AR_GL = "120000";
    static final Pattern MEMORY_ID = Pattern.compile("(ACC|PAY|INV|TRN)-(\\d{4})");

    final JsonNode model;
    final Map<String, Map<String, String>> enumIds = new HashMap<>();

    OfbizPool(JsonNode model) {
        this.model = model;
        for (JsonNode e : model.get("enums")) {
            Map<String, String> ids = new HashMap<>();
            for (JsonNode c : e.get("constants")) ids.put(c.get("name").asText(), c.get("id").asText());
            enumIds.put(e.get("name").asText(), ids);
        }
    }

    JsonNode service(String name) {
        for (JsonNode s : model.get("services")) if (s.get("name").asText().equals(name)) return s;
        throw new IllegalArgumentException("no service " + name);
    }

    /** The enum of each input path of a service (from its argument tree). */
    static void enumPaths(JsonNode argument, Map<String, String> out) {
        for (JsonNode p : argument.get("parts")) {
            if (p.hasNonNull("record")) enumPaths(p.get("record"), out);
            else if (p.hasNonNull("enum")) out.put(p.get("path").asText(), p.get("enum").asText());
        }
    }

    /** A command's canonical JSON as the service's input attributes (enumerations by their host id; nulls left out). */
    ObjectNode params(String serviceName, JsonNode command) {
        JsonNode s = service(serviceName);
        Map<String, String> enums = new HashMap<>();
        enumPaths(s.get("argument"), enums);
        ObjectNode params = NODES.objectNode();
        for (JsonNode in : s.get("inputs")) {
            if (!in.hasNonNull("path")) continue;
            String path = in.get("path").asText();
            JsonNode v = command;
            for (String part : path.split("\\.")) v = v == null ? null : v.get(part);
            if (v == null || v.isNull()) continue;
            String name = in.get("name").asText();
            if (enums.containsKey(path)) params.put(name, enumIds.get(enums.get(path)).get(v.asText()));
            else if (v.isBoolean()) params.put(name, v.asBoolean());
            else params.put(name, v.asText());
        }
        return params;
    }

    /** One scenario: the OFBiz steps and, alongside, the reference over the in-memory ports. */
    final class Flow {
        final String id;
        final TreeSet<String> rules = new TreeSet<>();
        final InMemoryPorts memory;
        final LoanServicing servicing;
        final ArrayNode steps = NODES.arrayNode();
        final Map<String, String> vars = new HashMap<>();
        final Map<String, String> receiptOf = new HashMap<>();
        final Map<String, String> receiptAmount = new HashMap<>();
        int receipts;
        LocalDate today;

        Flow(String id, LocalDate today, String... rules) {
            this.id = id;
            this.today = today;
            this.memory = new InMemoryPorts(new InMemoryPorts.Given(today, "USD", RATES));
            this.servicing = LoansApplication.create(memory.ports());
            this.rules.addAll(List.of(rules));
        }

        void today(LocalDate date) {
            today = date;
            memory.today(date);
        }

        /** A servicing call: the OFBiz step with its expected outcome and result, its ids bound for later steps. */
        Object call(String service, Object command, Object outcome, ObjectNode params) {
            params.put("businessDate", today.toString());
            ObjectNode step = NODES.objectNode();
            step.put("service", service);
            step.put("today", today.toString());
            step.set("params", params);
            ObjectNode expect = NODES.objectNode();
            expect.put("outcome", outcome.getClass().getSimpleName());
            ObjectNode bind = NODES.objectNode();
            expect.set("result", translate(Canon.of(outcome), "result", bind));
            step.set("expect", expect);
            if (!bind.isEmpty()) step.set("bind", bind);
            steps.add(step);
            return outcome;
        }

        Object book(BookLoan command) {
            ObjectNode params = params("bookLoan", Canon.of(command));
            return call("bookLoan", command, servicing.book(command), params);
        }

        Object disburse(DisburseLoan command) {
            ObjectNode params = NODES.objectNode();
            params.put("loanId", command.loanId());
            params.put("paymentMethodId", METHOD);
            return call("disburseLoan", command, servicing.disburse(command), params);
        }

        BillingRun billDue(LocalDate asOf) {
            ObjectNode params = NODES.objectNode();
            params.put("asOf", asOf.toString());
            return (BillingRun) call("billDueLoanInstallments", new BillDue(asOf), servicing.billDue(new BillDue(asOf)), params);
        }

        /** The borrower pays the invoice in full: an OFBiz payment received, applied to the invoice, which OFBiz then sets PAID. */
        void pay(String invoiceId) {
            if (receiptOf.containsKey(invoiceId)) return;
            InMemoryPorts.Invoice inv = memory.state().invoices().stream().filter(x -> x.invoiceId().equals(invoiceId)).findFirst().orElseThrow();
            BigDecimal amount = inv.invoice().principal().add(inv.invoice().interest());
            String receipt = "rcv" + (++receipts);
            ObjectNode payment = NODES.objectNode();
            payment.put("service", "createPayment");
            payment.put("today", today.toString());
            ObjectNode p = payment.putObject("params");
            p.put("paymentTypeId", "CUSTOMER_PAYMENT");
            p.put("paymentMethodTypeId", "CASH");
            p.put("partyIdFrom", BORROWER);
            p.put("partyIdTo", ORG);
            p.put("amount", amount.toPlainString());
            p.put("currencyUomId", inv.invoice().currencyCode());
            p.put("statusId", "PMNT_RECEIVED");
            p.put("effectiveDate", today + " 00:00:00.0");
            payment.putObject("bind").put(receipt, "paymentId");
            steps.add(payment);
            ObjectNode apply = NODES.objectNode();
            apply.put("service", "createPaymentApplication");
            apply.put("today", today.toString());
            ObjectNode a = apply.putObject("params");
            a.put("paymentId", "$" + receipt);
            a.put("invoiceId", "$" + vars.get(invoiceId));
            a.put("amountApplied", amount.toPlainString());
            steps.add(apply);
            receiptOf.put(invoiceId, receipt);
            servicing.invoicePaid(new InvoicePaid(invoiceId, true));
        }

        /** The payment of an invoice is voided: OFBiz sets the invoice back to READY. */
        void unpay(String invoiceId) {
            String receipt = receiptOf.remove(invoiceId);
            if (receipt == null) return;
            ObjectNode step = NODES.objectNode();
            step.put("service", "voidPayment");
            step.put("today", today.toString());
            step.putObject("params").put("paymentId", "$" + receipt);
            steps.add(step);
            servicing.invoicePaid(new InvoicePaid(invoiceId, false));
        }

        /** Expected JSON with in-memory ids: a known one is its variable; a new one any value, bound at its path. */
        JsonNode translate(JsonNode node, String path, ObjectNode bind) {
            if (node.isObject()) {
                ObjectNode out = NODES.objectNode();
                node.fields().forEachRemaining(e -> out.set(e.getKey(), translate(e.getValue(), path + "." + e.getKey(), bind)));
                return out;
            }
            if (node.isArray()) {
                ArrayNode out = NODES.arrayNode();
                for (int i = 0; i < node.size(); i++) out.add(translate(node.get(i), path + "." + i, bind));
                return out;
            }
            if (node.isTextual() && MEMORY_ID.matcher(node.asText()).matches()) {
                String known = vars.get(node.asText());
                if (known != null) return NODES.textNode("$" + known);
                var m = MEMORY_ID.matcher(node.asText());
                m.matches();
                String var = m.group(1).toLowerCase() + Integer.parseInt(m.group(2));
                vars.put(node.asText(), var);
                bind.put(var, path);
                return NODES.textNode("*");
            }
            return node;
        }

        String ref(String memoryId) {
            if (memoryId == null) return null;
            String var = vars.get(memoryId);
            return var == null ? "*" : "$" + var;
        }

        /** The checks of the final state: the reference's in-memory state as the OFBiz rows the rules imply. */
        ArrayNode checks() {
            ArrayNode checks = NODES.arrayNode();
            InMemoryPorts.State s = memory.state();
            for (InMemoryPorts.LoanState loan : s.loans()) {
                ObjectNode row = NODES.objectNode();
                row.put("externalId", loan.externalId());
                row.put("statusId", enumIds.get("LoanStatus").get(loan.status().name()));
                row.put("accountId", ref(loan.accountId()));
                row.put("disbursementPaymentId", ref(loan.disbursementPaymentId()));
                row.put("disbursementTransactionId", ref(loan.disbursementTransactionId()));
                checks.add(check("Loan", where("loanId", loan.loanId()), List.of(), rows(row)));
                ArrayNode lines = NODES.arrayNode();
                for (LineServicing l : loan.lines()) {
                    ObjectNode r = lines.addObject();
                    r.put("lineNumber", l.number());
                    r.put("dueDate", l.due().toString());
                    r.put("principal", l.principal().toPlainString());
                    r.put("interest", l.interest().toPlainString());
                    r.put("invoiceId", ref(l.invoiceId()));
                    r.put("billedOn", l.billedOn() == null ? null : l.billedOn().toString());
                    r.put("collectionTransactionId", l.collectionTransactionId() == null ? null : "*");
                }
                checks.add(check("LoanServicingLine", where("loanId", loan.loanId()), List.of("lineSeqId"), lines));
            }
            for (InMemoryPorts.Account acc : s.accounts()) {
                ObjectNode row = NODES.objectNode();
                row.put("finAccountTypeId", "LOAN_ACCOUNT");
                row.put("statusId", "FNACT_ACTIVE");
                row.put("ownerPartyId", acc.account().ownerPartyId());
                row.put("organizationPartyId", acc.account().organizationPartyId());
                row.put("currencyUomId", acc.account().currencyCode());
                row.put("actualBalance", acc.balance().toPlainString());
                checks.add(check("FinAccount", where("finAccountId", ref(acc.accountId())), List.of(), rows(row)));
                ArrayNode trans = NODES.arrayNode();
                for (InMemoryPorts.Transaction t : s.transactions()) {
                    if (!t.transaction().accountId().equals(acc.accountId())) continue;
                    ObjectNode r = trans.addObject();
                    r.put("finAccountTransTypeId", t.transaction().type().name());
                    r.put("amount", t.transaction().amount().toPlainString());
                    r.put("transactionDate", t.transaction().date().toString());
                    r.put("statusId", t.cancelled() ? "FINACT_TRNS_CANCELED" : "FINACT_TRNS_APPROVED");
                    r.put("paymentId", t.transaction().type() == AccountTransactionType.WITHDRAWAL ? ref(t.transaction().paymentId()) : null);
                }
                checks.add(check("FinAccountTrans", where("finAccountId", ref(acc.accountId())), List.of("finAccountTransId"), trans));
            }
            for (InMemoryPorts.Invoice inv : s.invoices()) {
                String id = ref(inv.invoiceId());
                ObjectNode row = NODES.objectNode();
                row.put("invoiceTypeId", "SALES_INVOICE");
                row.put("statusId", receiptOf.containsKey(inv.invoiceId()) ? "INVOICE_PAID" : "INVOICE_READY");
                row.put("invoiceDate", inv.invoice().invoiceDate().toString());
                row.put("partyIdFrom", inv.invoice().fromPartyId());
                row.put("partyId", inv.invoice().toPartyId());
                row.put("currencyUomId", inv.invoice().currencyCode());
                checks.add(check("Invoice", where("invoiceId", id), List.of(), rows(row)));
                ArrayNode items = NODES.arrayNode();
                ArrayNode entries = NODES.arrayNode();
                BigDecimal p = inv.invoice().principal();
                BigDecimal i = inv.invoice().interest();
                if (p.signum() != 0) {
                    item(items, "LOAN_PRINCIPAL", p, RECEIVABLE_GL);
                    entry(entries, RECEIVABLE_GL, "C", p, inv.invoice().currencyCode());
                }
                if (i.signum() != 0) {
                    item(items, "INV_INTRST_CHRG", i, INTEREST_GL);
                    entry(entries, INTEREST_GL, "C", i, inv.invoice().currencyCode());
                }
                entry(entries, AR_GL, "D", p.add(i), inv.invoice().currencyCode());
                checks.add(check("InvoiceItem", where("invoiceId", id), List.of("invoiceItemSeqId"), items));
                ObjectNode posted = NODES.objectNode();
                posted.put("from", "AcctgTrans");
                posted.putObject("where").put("invoiceId", id).put("acctgTransTypeId", "SALES_INVOICE");
                posted.put("field", "acctgTransId");
                ObjectNode w = NODES.objectNode();
                w.set("acctgTransId", posted);
                checks.add(check("AcctgTransEntry", w, List.of("acctgTransEntrySeqId"), entries));
            }
            for (InMemoryPorts.Payment pay : s.payments()) {
                String id = ref(pay.paymentId());
                ObjectNode row = NODES.objectNode();
                row.put("paymentTypeId", "DISBURSEMENT");
                row.put("paymentMethodId", METHOD);
                row.put("statusId", "PMNT_SENT");
                row.put("amount", pay.payment().amount().toPlainString());
                row.put("currencyUomId", pay.payment().currencyCode());
                row.put("overrideGlAccountId", RECEIVABLE_GL);
                row.put("partyIdFrom", pay.payment().fromPartyId());
                row.put("partyIdTo", pay.payment().toPartyId());
                row.put("effectiveDate", pay.payment().date().toString());
                checks.add(check("Payment", where("paymentId", id), List.of(), rows(row)));
                ArrayNode entries = NODES.arrayNode();
                entry(entries, CASH_GL, "C", pay.payment().amount(), pay.payment().currencyCode());
                entry(entries, RECEIVABLE_GL, "D", pay.payment().amount(), pay.payment().currencyCode());
                ObjectNode posted = NODES.objectNode();
                posted.put("from", "AcctgTrans");
                posted.putObject("where").put("paymentId", id).put("acctgTransTypeId", "OUTGOING_PAYMENT");
                posted.put("field", "acctgTransId");
                ObjectNode w = NODES.objectNode();
                w.set("acctgTransId", posted);
                checks.add(check("AcctgTransEntry", w, List.of("acctgTransEntrySeqId"), entries));
            }
            return checks;
        }

        ObjectNode scenario(String origin) {
            ObjectNode sc = NODES.objectNode();
            sc.put("schema", "mlcpu.ofbiz-scenario.v1");
            sc.put("id", id);
            sc.put("origin", origin);
            ArrayNode r = sc.putArray("rules");
            rules.forEach(r::add);
            sc.set("steps", steps);
            sc.set("checks", checks());
            return sc;
        }
    }

    static ObjectNode where(String field, String value) {
        ObjectNode w = NODES.objectNode();
        w.put(field, value);
        return w;
    }

    static ArrayNode rows(ObjectNode row) {
        ArrayNode a = NODES.arrayNode();
        a.add(row);
        return a;
    }

    static ObjectNode check(String entity, ObjectNode where, List<String> orderBy, ArrayNode rows) {
        ObjectNode c = NODES.objectNode();
        c.put("entity", entity);
        c.set("where", where);
        if (!orderBy.isEmpty()) {
            ArrayNode o = c.putArray("orderBy");
            orderBy.forEach(o::add);
        }
        ArrayNode fields = c.putArray("fields");
        if (rows.size() > 0) rows.get(0).fieldNames().forEachRemaining(fields::add);
        c.set("rows", rows);
        return c;
    }

    static void item(ArrayNode items, String type, BigDecimal amount, String gl) {
        ObjectNode r = items.addObject();
        r.put("invoiceItemTypeId", type);
        r.put("amount", amount.toPlainString());
        r.put("overrideGlAccountId", gl);
    }

    static void entry(ArrayNode entries, String gl, String side, BigDecimal amount, String currency) {
        ObjectNode r = entries.addObject();
        r.put("glAccountId", gl);
        r.put("debitCreditFlag", side);
        r.put("origAmount", amount.toPlainString());
        r.put("origCurrencyUomId", currency);
    }

    // ---------------------------------------------------------------- the flows

    static BookLoan bookLoan(String externalId, ScheduleRequest terms) {
        return new BookLoan(externalId, BORROWER, ORG, PRODUCT, terms);
    }

    static Schedule scheduled(ScheduleRequest q) {
        return ((Scheduled) LoansApplication.quotes().schedule(q)).schedule();
    }

    static List<InstallmentLine> installments(ScheduleRequest q) {
        return scheduled(q).lines().stream().filter(l -> l instanceof InstallmentLine).map(l -> (InstallmentLine) l).toList();
    }

    /** The loan's whole life: booked, disbursed, each installment billed on its due date and paid, then the down payment. */
    Flow life(String id, ScheduleRequest q) {
        Flow f = new Flow(id, q.disbursementDate().minusDays(3), "OFB-02", "OFB-04", "OFB-05", "OFB-06", "OFB-07", "OFB-10");
        Booked b = (Booked) f.book(bookLoan("E-" + id, q));
        f.today(q.disbursementDate());
        Disbursed d = (Disbursed) f.disburse(new DisburseLoan(b.loanId(), "PM-1"));
        for (InstallmentLine i : installments(q)) {
            f.today(i.due());
            for (InstallmentBilled x : f.billDue(i.due()).billed()) f.pay(x.invoiceId());
        }
        if (d.downPaymentInvoiceId() != null) f.pay(d.downPaymentInvoiceId());
        return f;
    }

    /** Billed before disbursement (nothing), then everything at once after the last due date; every other invoice paid. */
    Flow batch(String id, ScheduleRequest q) {
        Flow f = new Flow(id, q.disbursementDate(), "OFB-05", "OFB-07", "OFB-08", "OFB-10");
        Booked b = (Booked) f.book(bookLoan("E-" + id, q));
        f.billDue(q.disbursementDate().plusYears(100));
        f.disburse(new DisburseLoan(b.loanId(), "PM-7"));
        List<InstallmentLine> lines = installments(q);
        LocalDate after = lines.get(lines.size() - 1).due().plusDays(1);
        f.today(after);
        BillingRun run = f.billDue(after);
        for (int k = 0; k < run.billed().size(); k += 2) f.pay(run.billed().get(k).invoiceId());
        return f;
    }

    /** The idempotent repeats: booked twice with one external id, disbursed twice, billed twice on one date (IDEM, OFB-08). */
    Flow repeats(String id, ScheduleRequest q) {
        Flow f = new Flow(id, q.disbursementDate(), "OFB-02", "OFB-08", "IDEM-01");
        Booked b = (Booked) f.book(bookLoan("E-" + id, q));
        f.book(bookLoan("E-" + id, ServicingPool.with(q, q.principal().add(BigDecimal.ONE), q.annualRatePercent())));
        f.disburse(new DisburseLoan(b.loanId(), "PM-1"));
        f.disburse(new DisburseLoan(b.loanId(), "PM-2"));
        LocalDate first = installments(q).get(0).due();
        f.today(first);
        BillingRun run = f.billDue(first);
        f.billDue(first);
        if (!run.billed().isEmpty()) f.pay(run.billed().get(0).invoiceId());
        return f;
    }

    /** Everything billed and paid, then the last payment voided, paid again, voided again: the loan ends active (OFB-07). */
    Flow voided(String id, ScheduleRequest q) {
        Flow f = new Flow(id, q.disbursementDate(), "OFB-07", "OFB-05");
        Booked b = (Booked) f.book(bookLoan("E-" + id, q));
        Disbursed d = (Disbursed) f.disburse(new DisburseLoan(b.loanId(), "PM-1"));
        List<InstallmentLine> lines = installments(q);
        LocalDate last = lines.get(lines.size() - 1).due();
        f.today(last);
        BillingRun run = f.billDue(last);
        List<String> invoices = new ArrayList<>();
        if (d.downPaymentInvoiceId() != null) invoices.add(d.downPaymentInvoiceId());
        run.billed().forEach(x -> invoices.add(x.invoiceId()));
        for (String inv : invoices) f.pay(inv);
        if (!invoices.isEmpty()) {
            String lastInvoice = invoices.get(invoices.size() - 1);
            f.today(last.plusDays(5));
            f.unpay(lastInvoice);
            f.today(last.plusDays(9));
            f.pay(lastInvoice);
            f.today(last.plusDays(12));
            f.unpay(lastInvoice);
        }
        return f;
    }

    /** A booking the schedule or the ledger refuses: the rejection, and nothing written (OFB-02, D-11, D-12). */
    Flow rejected(String id, ScheduleRequest q, String... rules) {
        Flow f = new Flow(id, q.disbursementDate(), rules);
        f.rules.add("OFB-02");
        f.book(bookLoan("E-" + id, q));
        return f;
    }

    /** Two loans billed in one run: each invoice belongs to its loan (OFB-05). */
    Flow two(String id, ScheduleRequest a, ScheduleRequest c) {
        LocalDate start = a.disbursementDate().isBefore(c.disbursementDate()) ? a.disbursementDate() : c.disbursementDate();
        Flow f = new Flow(id, start, "OFB-05", "OFB-07");
        Booked ba = (Booked) f.book(bookLoan("E-" + id + "-Z", a));
        Booked bc = (Booked) f.book(bookLoan("E-" + id + "-A", c));
        f.disburse(new DisburseLoan(ba.loanId(), "PM-1"));
        f.disburse(new DisburseLoan(bc.loanId(), "PM-1"));
        LocalDate last = Stream.of(a, c).map(x -> installments(x)).map(l -> l.get(l.size() - 1).due()).max(LocalDate::compareTo).orElseThrow();
        f.today(last);
        for (InstallmentBilled x : f.billDue(last).billed()) if (x.number() % 2 == 1) f.pay(x.invoiceId());
        return f;
    }

    /** A quote writes nothing (OFB-01): the schedule comes back, and no loan exists for its id. */
    ObjectNode quote(String id, ScheduleRequest q) {
        ObjectNode sc = NODES.objectNode();
        sc.put("schema", "mlcpu.ofbiz-scenario.v1");
        sc.put("id", id);
        sc.putArray("rules").add("OFB-01");
        ArrayNode steps = sc.putArray("steps");
        ObjectNode step = steps.addObject();
        step.put("service", "quoteLoanSchedule");
        step.set("params", params("quoteLoanSchedule", Canon.of(q)));
        Object outcome = LoansApplication.quotes().schedule(q);
        ObjectNode expect = step.putObject("expect");
        expect.put("outcome", outcome.getClass().getSimpleName());
        expect.set("result", Canon.of(outcome));
        ArrayNode checks = sc.putArray("checks");
        ObjectNode c = check("Loan", where("termsLoanId", q.loanId()), List.of(), NODES.arrayNode());
        c.put("count", 0);
        checks.add(c);
        return sc;
    }

    /** An unknown enumeration id is a service error naming the field, and nothing is written (OFB-03). */
    ObjectNode unknownId(String id, ScheduleRequest q) {
        ObjectNode sc = NODES.objectNode();
        sc.put("schema", "mlcpu.ofbiz-scenario.v1");
        sc.put("id", id);
        sc.putArray("rules").add("OFB-03");
        ObjectNode params = params("bookLoan", Canon.of(bookLoan("E-" + id, q)));
        params.put("frequencyEnumId", "LOAN_FREQ_FORTNIGHTS");
        ObjectNode step = sc.putArray("steps").addObject();
        step.put("service", "bookLoan");
        step.set("params", params);
        step.putObject("expect").put("success", false).put("errorContains", "frequencyEnumId");
        ObjectNode c = check("Loan", where("externalId", "E-" + id), List.of(), NODES.arrayNode());
        c.put("count", 0);
        sc.putArray("checks").add(c);
        return sc;
    }

    /** A user without accounting permission is refused, and nothing is written (OFB-09). */
    ObjectNode refused(String id, ScheduleRequest q) {
        ObjectNode sc = NODES.objectNode();
        sc.put("schema", "mlcpu.ofbiz-scenario.v1");
        sc.put("id", id);
        sc.putArray("rules").add("OFB-09");
        ObjectNode step = sc.putArray("steps").addObject();
        step.put("service", "bookLoan");
        step.put("userLogin", "DemoCustomer");
        step.set("params", params("bookLoan", Canon.of(bookLoan("E-" + id, q))));
        step.putObject("expect").put("success", false);
        ObjectNode c = check("Loan", where("externalId", "E-" + id), List.of(), NODES.arrayNode());
        c.put("count", 0);
        sc.putArray("checks").add(c);
        return sc;
    }

    /** The demo rates are valid from 2001-01-01; OFBiz counts a payment as applied only once it is effective (no later than now). */
    static final LocalDate WINDOW_FROM = LocalDate.of(2001, 1, 2);
    static final LocalDate WINDOW_TO = LocalDate.of(2026, 9, 1);

    /**
     * The loan moved by whole 28-year cycles so that its whole life (from its first date to 13 days after its last due date) lies in
     * the window, or null. Between 1901 and 2099 the calendar repeats every 28 years (weekdays and leap years), so the moved loan's
     * schedule is the same, its dates moved.
     */
    static ScheduleRequest inWindow(ScheduleRequest q, boolean scheduled) {
        LocalDate start = q.scheduleStart().isBefore(q.disbursementDate()) ? q.scheduleStart() : q.disbursementDate();
        LocalDate end = scheduled ? scheduled(q).lines().get(scheduled(q).lines().size() - 1).due().plusDays(13) : q.disbursementDate().plusDays(13);
        if (start.getYear() < 1901 || end.getYear() > 2099) return null;
        for (int k = -8; k <= 8; k++) {
            int years = 28 * k;
            LocalDate s2 = start.plusYears(years);
            LocalDate e2 = end.plusYears(years);
            if (s2.getYear() < 1901 || e2.getYear() > 2099 || s2.isBefore(WINDOW_FROM) || e2.isAfter(WINDOW_TO)) continue;
            return new ScheduleRequest(q.loanId(), q.scheduleStart().plusYears(years), q.currency(), q.principal(), q.disbursementDate().plusYears(years),
                    q.installments(), q.every(), q.frequency(), q.annualRatePercent(), q.downPaymentPercent(), q.downPaymentEnabled(), q.monthDays(),
                    q.yearDays(), q.fixedLength(), q.interestMethod(), q.fullTermTranche(), q.calculation());
        }
        return null;
    }

    static ScheduleRequest currency(ScheduleRequest q, String code) {
        CurrencyTerms c = q.currency();
        return new ScheduleRequest(q.loanId(), q.scheduleStart(), new CurrencyTerms(code, c.decimalPlaces(), c.roundingMultiple()), q.principal(),
                q.disbursementDate(), q.installments(), q.every(), q.frequency(), q.annualRatePercent(), q.downPaymentPercent(), q.downPaymentEnabled(),
                q.monthDays(), q.yearDays(), q.fixedLength(), q.interestMethod(), q.fullTermTranche(), q.calculation());
    }

    /**
     * The terms under a loan id of their own: a quote's id must not be one a booking stores (a booked loan's terms may keep the
     * command's id, which SRV leaves to the implementation), or OFB-01's check would see that loan.
     */
    static ScheduleRequest loanId(ScheduleRequest q, String id) {
        return new ScheduleRequest(id, q.scheduleStart(), q.currency(), q.principal(), q.disbursementDate(), q.installments(), q.every(), q.frequency(),
                q.annualRatePercent(), q.downPaymentPercent(), q.downPaymentEnabled(), q.monthDays(), q.yearDays(), q.fixedLength(), q.interestMethod(),
                q.fullTermTranche(), q.calculation());
    }

    public static void main(String[] args) throws IOException {
        Path pool = Path.of(args[0]);
        OfbizPool p = new OfbizPool(new ObjectMapper().readTree(Path.of(args[1]).toFile()));
        Path root = Path.of(args[2]);
        int perGroup = args.length > 3 ? Integer.parseInt(args[3]) : 2;
        // The loans: S5 loans of at most 12 installments, a few per shape (outcome, currency, frequency, down payment).
        Map<String, List<JsonNode>> groups = new TreeMap<>();
        Map<JsonNode, ScheduleRequest> placed = new HashMap<>();
        List<Path> files;
        try (Stream<Path> s = Files.list(pool)) {
            files = s.filter(f -> f.toString().endsWith(".jsonl")).sorted().toList();
        }
        for (Path f : files) {
            try (BufferedReader r = Files.newBufferedReader(f, StandardCharsets.UTF_8)) {
                String line;
                while ((line = r.readLine()) != null) {
                    JsonNode sc = JSON.readTree(line);
                    JsonNode in = sc.get("steps").get(0).get("input");
                    JsonNode exp = sc.get("steps").get(0).get("expected");
                    if (in.get("installments").asInt() > 12) continue;
                    // a rejection by its reason; a schedule by currency, frequency and down payment (the shapes the host sees differ in)
                    String shape = exp.path("@type").asText().equals("Rejected") ? "Rejected|" + exp.path("reason").asText()
                            : "Scheduled|" + in.get("currency").get("code").asText() + "|" + in.get("frequency").asText() + "|"
                            + in.get("downPaymentEnabled").asBoolean();
                    List<JsonNode> g = groups.computeIfAbsent(shape, k -> new ArrayList<>());
                    if (g.size() >= perGroup) continue;
                    ScheduleRequest moved = inWindow(PoolRunner.request(in), exp.path("@type").asText().equals("Scheduled"));
                    if (moved == null) continue;
                    placed.put(sc, moved);
                    g.add(sc);
                }
            }
        }
        Map<String, ObjectNode> scenarios = new LinkedHashMap<>();
        List<ScheduleRequest> booked = new ArrayList<>();
        int n = 0;
        List<String> problems = new ArrayList<>();
        for (var e : groups.entrySet()) {
            for (JsonNode sc : e.getValue()) {
                ScheduleRequest q = placed.get(sc);
                ScheduleRequest original = PoolRunner.request(sc.get("steps").get(0).get("input"));
                String origin = "the S5 loan " + sc.get("id").asText() + (q.disbursementDate().equals(original.disbursementDate()) ? ""
                        : ", moved " + (q.disbursementDate().getYear() - original.disbursementDate().getYear()) + " years into OFBiz's dated window")
                        + "; expected values: the reference over in-memory ports with the demo ledger";
                String id = String.format("ofb-%04d", ++n);
                try {
                    if (e.getKey().startsWith("Rejected")) {
                        scenarios.put(id, p.rejected(id, q, "D-1").scenario(origin));
                        continue;
                    }
                    String code = q.currency().code();
                    if (q.currency().decimalPlaces() > 2 || !(code.equals("USD") || RATES.contains(code))) {
                        scenarios.put(id, p.rejected(id, q, "D-11").scenario(origin + " (the ledger refuses the currency)"));
                        continue;
                    }
                    Flow f = switch (n % 4) {
                        case 0 -> p.life(id, q);
                        case 1 -> p.batch(id, q);
                        case 2 -> p.repeats(id, q);
                        default -> p.voided(id, q);
                    };
                    scenarios.put(id, f.scenario(origin));
                    booked.add(q);
                } catch (ClassCastException notBooked) {
                    scenarios.put(id, p.rejected(id, q, "D-11").scenario(origin + " (the ledger refuses it)"));
                } catch (RuntimeException ex) {
                    problems.add(id + ": " + ex);
                }
            }
        }
        // the ledger's other refusals and the host-only rules, on booked loans
        ScheduleRequest first = booked.get(0);
        scenarios.put("ofb-r001", p.rejected("ofb-r001", currency(first, "CHF"), "D-11").scenario("no rate from CHF to the base currency"));
        scenarios.put("ofb-r002", p.rejected("ofb-r002", ServicingPool.with(first, first.principal(), first.annualRatePercent().add(new BigDecimal("0.0000001"))),
                "D-12").scenario("a rate with 7 decimals"));
        scenarios.put("ofb-r003", p.rejected("ofb-r003", ServicingPool.with(first, first.principal().multiply(BigDecimal.valueOf(2_000_000_000L)),
                first.annualRatePercent()), "D-11").scenario("an amount beyond the ledger's capacity"));
        if (booked.size() > 1) scenarios.put("ofb-t001", p.two("ofb-t001", booked.get(0), booked.get(1)).scenario("two loans billed together"));
        scenarios.put("ofb-q001", p.quote("ofb-q001", loanId(first, "Q-ofb-q001")));
        scenarios.put("ofb-u001", p.unknownId("ofb-u001", first));
        scenarios.put("ofb-a001", p.refused("ofb-a001", first));
        // public: every fourth, and every host-only scenario (they show the services' contract); the rest hidden
        Files.createDirectories(root.resolve("public"));
        Files.createDirectories(root.resolve("hidden"));
        int k = 0;
        int pub = 0;
        for (var e : scenarios.entrySet()) {
            boolean isPublic = k++ % 4 == 0 || e.getKey().matches("ofb-[qua]\\d+");
            if (isPublic) pub++;
            JSON.writeValue(root.resolve(isPublic ? "public" : "hidden").resolve(e.getKey() + ".json").toFile(), e.getValue());
        }
        System.out.printf("ofbiz pool: %d scenarios (%d public, %d hidden) from %d loan shapes -> %s%n", scenarios.size(), pub, scenarios.size() - pub,
                groups.size(), root);
        problems.forEach(x -> System.out.println("  problem: " + x));
        if (!problems.isEmpty()) System.exit(1);
    }
}
