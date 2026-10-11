package com.mlcpu.cas.acceptance.sme;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.node.ArrayNode;
import com.fasterxml.jackson.databind.node.JsonNodeFactory;
import com.fasterxml.jackson.databind.node.ObjectNode;
import com.mlcpu.cas.acceptance.Json;
import java.io.IOException;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.Set;

/**
 * SME-authored scenarios: idempotency, interest cycles, ordering, the report over posted and generated transactions, and the
 * customer statement (which has no legacy oracle). Their data comes from the legacy drafts (CardDemo accounts, cards, category
 * balances, disclosure rates, transaction types and categories; the SAM customer master); expected values are filled in by the
 * reference implementation.
 */
final class Authored {
    private Authored() {}

    static final JsonNodeFactory F = JsonNodeFactory.instance;

    record Spec(String title, ObjectNode scenario, List<String> rules, List<String> extraStores) {}

    static JsonNode cardDemo, reference, sam;

    static List<Spec> all(Path drafts) throws IOException {
        cardDemo = Json.MAPPER.readTree(drafts.resolve("accounts/interest-01.json").toFile()).path("initial");
        reference = Json.MAPPER.readTree(drafts.resolve("reporting/report-01.json").toFile()).path("initial");
        sam = Json.MAPPER.readTree(drafts.resolve("customers/sam-002.json").toFile()).path("initial");
        List<Spec> specs = new ArrayList<>();
        accounts(specs);
        reporting(specs);
        statements(specs);
        customers(specs);
        return specs;
    }

    // ------------------------------------------------------------------------------------------------ builders

    static ObjectNode scenario(String id, String slice, String title) {
        ObjectNode s = F.objectNode();
        s.put("id", id);
        s.put("slice", slice);
        s.put("origin", "authored by the SME: " + title);
        s.putArray("rules");
        s.putObject("initial");
        s.putArray("steps");
        return s;
    }

    static Spec spec(ObjectNode s, String title, String... rules) {
        return new Spec(title, s, List.of(rules), null);
    }

    /** CardDemo accounts (with their cards and category balances), every disclosure rate, the transaction types and categories. */
    static ObjectNode cardDemo(ObjectNode s, long... ids) {
        ObjectNode init = (ObjectNode) s.get("initial");
        Set<Long> keep = new java.util.HashSet<>();
        for (long id : ids) keep.add(id);
        ArrayNode accounts = init.putArray("accounts"), cards = init.putArray("cards"), balances = init.putArray("categoryBalances");
        cardDemo.path("accounts").forEach(a -> { if (keep.contains(a.path("accountId").asLong())) accounts.add(a.deepCopy()); });
        cardDemo.path("cards").forEach(c -> { if (keep.contains(c.path("accountId").asLong())) cards.add(c.deepCopy()); });
        cardDemo.path("categoryBalances").forEach(b -> { if (keep.contains(b.path("accountId").asLong())) balances.add(b.deepCopy()); });
        init.set("disclosureRates", cardDemo.path("disclosureRates").deepCopy());
        init.set("transactionTypes", reference.path("transactionTypes").deepCopy());
        init.set("transactionCategories", reference.path("transactionCategories").deepCopy());
        init.putArray("ledger");
        return init;
    }

    static ObjectNode account(ObjectNode s, long id) {
        for (JsonNode a : s.path("initial").path("accounts")) if (a.path("accountId").asLong() == id) return (ObjectNode) a;
        throw new IllegalArgumentException("account " + id);
    }

    static String card(long accountId) {
        for (JsonNode c : cardDemo.path("cards")) if (c.path("accountId").asLong() == accountId) return c.path("cardNumber").asText();
        throw new IllegalArgumentException("card of " + accountId);
    }

    static ObjectNode tx(String id, long account, String type, int category, String amount, String origin) {
        ObjectNode t = F.objectNode();
        t.put("transactionId", id);
        t.put("typeCode", type);
        t.put("categoryCode", category);
        t.put("source", type.equals("03") ? "OPERATOR" : "POS TERM");
        t.put("description", (type.equals("03") ? "Return item at " : "Purchase at ") + "Store " + id.substring(id.length() - 3));
        t.put("amount", amount);
        t.put("merchantId", 800000000L);
        t.put("merchantName", "Store " + id.substring(id.length() - 3));
        t.put("merchantCity", "Springfield");
        t.put("merchantZip", "12345");
        t.put("cardNumber", account < 0 ? "9999999999999999" : card(account));
        if (origin == null) t.putNull("originTimestamp");
        else t.put("originTimestamp", origin);
        return t;
    }

    static ObjectNode step(ObjectNode s, String at, String command, ObjectNode input) {
        ObjectNode st = ((ArrayNode) s.get("steps")).addObject();
        st.put("at", at);
        st.put("command", command);
        st.set("input", input);
        return st;
    }

    static ObjectNode post(String key, ObjectNode tx) {
        ObjectNode i = F.objectNode();
        i.put("key", key);
        i.set("transaction", tx);
        return i;
    }

    static ObjectNode batch(String key, ObjectNode... txs) {
        ObjectNode i = F.objectNode();
        i.put("key", key);
        ArrayNode a = i.putArray("transactions");
        for (ObjectNode t : txs) a.add(t);
        return i;
    }

    static ObjectNode accrue(String key, String cycle) {
        ObjectNode i = F.objectNode();
        i.put("key", key);
        i.put("cycle", cycle);
        return i;
    }

    static ObjectNode report(String from, String to) {
        ObjectNode i = F.objectNode();
        i.put("startDate", from);
        i.put("endDate", to);
        return i;
    }

    static ObjectNode statement(String customer, String cycle) {
        ObjectNode i = F.objectNode();
        i.put("customerId", customer);
        i.put("cycle", cycle);
        return i;
    }

    static ObjectNode rate(String group, String type, int category, String rate) {
        ObjectNode r = F.objectNode();
        r.put("groupId", group);
        r.put("typeCode", type);
        r.put("categoryCode", category);
        r.put("annualRatePercent", rate);
        return r;
    }

    static ObjectNode ledgerEntry(String id, long account, String type, int category, String amount, String processed, String interestCycle) {
        ObjectNode e = F.objectNode();
        e.put("transactionId", id);
        e.put("accountId", account);
        e.put("cardNumber", card(account));
        e.put("typeCode", type);
        e.put("categoryCode", category);
        e.put("source", interestCycle != null ? "System" : "POS TERM");
        e.put("description", interestCycle != null ? String.format("Int. for a/c %011d", account) : "Migrated " + id);
        e.put("amount", amount);
        e.put("merchantId", 0L);
        e.put("merchantName", "");
        e.put("merchantCity", "");
        e.put("merchantZip", "");
        e.put("originTimestamp", processed);
        e.put("processedTimestamp", processed);
        if (interestCycle == null) e.putNull("interestCycle");
        else e.put("interestCycle", interestCycle);
        return e;
    }

    // ------------------------------------------------------------------------------------------------ accounts

    static void accounts(List<Spec> specs) {
        String day = "2022-07-05T10:11:12.000000", later = "2022-07-05T15:30:00.000000", origin = "2022-07-04T18:00:00.000000";
        {
            ObjectNode s = scenario("idem-post-replay", "accounts", "the same postTransaction key twice: the second call returns the first result and changes nothing");
            cardDemo(s, 1);
            step(s, day, "postTransaction", post("rt-1", tx("0000000100000001", 1, "01", 1, "125.40", origin)));
            step(s, later, "postTransaction", post("rt-1", tx("0000000100000001", 1, "01", 1, "125.40", origin)));
            specs.add(spec(s, "replayed key", "IDEM-01"));
        }
        {
            ObjectNode s = scenario("idem-post-duplicate-id", "accounts", "a transaction id already in the ledger, under a new key, is rejected with 104");
            cardDemo(s, 1);
            step(s, day, "postTransaction", post("rt-1", tx("0000000100000002", 1, "01", 1, "60.00", origin)));
            step(s, later, "postTransaction", post("rt-2", tx("0000000100000002", 1, "01", 1, "60.00", origin)));
            specs.add(spec(s, "duplicate id", "IDEM-04", "POST-01"));
        }
        {
            ObjectNode s = scenario("idem-batch-replay", "accounts", "the same postBatch key twice");
            cardDemo(s, 1, 2);
            ObjectNode b = batch("night-1", tx("0000000100000003", 1, "01", 1, "10.00", origin), tx("0000000100000004", 2, "01", 2, "20.00", origin),
                    tx("0000000100000005", -1, "01", 1, "30.00", origin));
            step(s, day, "postBatch", b);
            step(s, later, "postBatch", b.deepCopy());
            specs.add(spec(s, "replayed batch key", "IDEM-01"));
        }
        {
            ObjectNode s = scenario("post-batch-duplicate-within", "accounts", "a batch that carries the same transaction id twice: the second is a duplicate");
            cardDemo(s, 2);
            step(s, day, "postBatch", batch("night-2", tx("0000000100000006", 2, "01", 1, "15.00", origin), tx("0000000100000006", 2, "01", 1, "15.00", origin),
                    tx("0000000100000007", 2, "03", 1, "-5.00", origin)));
            specs.add(spec(s, "duplicate in batch", "POST-01", "POST-12"));
        }
        for (String mode : List.of("batch", "realtime")) {
            ObjectNode s = scenario("post-limit-sequence-" + mode, "accounts",
                    "the projected cycle balance (credit - debit + amount) over a sequence of credits and debits, " + mode);
            cardDemo(s, 3);
            ObjectNode a = account(s, 3);
            a.put("creditLimit", "1000.00");
            a.put("currentCycleCredit", "0.00");
            a.put("currentCycleDebit", "0.00");
            ObjectNode[] t = {tx("0000000100000011", 3, "01", 1, "600.00", origin), tx("0000000100000012", 3, "01", 1, "500.00", origin),
                    tx("0000000100000013", 3, "03", 1, "-200.00", origin), tx("0000000100000014", 3, "01", 1, "200.00", origin),
                    tx("0000000100000015", 3, "01", 1, "0.00", origin), tx("0000000100000016", 3, "01", 1, "0.01", origin)};
            if (mode.equals("batch")) step(s, day, "postBatch", batch("night-3", t));
            else for (int i = 0; i < t.length; i++) step(s, day, "postTransaction", post("rt-1" + i, t[i]));
            specs.add(spec(s, "limit sequence", "POST-04", "POST-08"));
        }
        {
            ObjectNode s = scenario("post-expiry-boundary", "accounts", "expiry: on the expiration day it posts, the day after it is refused, without an origin timestamp it posts");
            cardDemo(s, 4);
            ObjectNode a = account(s, 4);
            a.put("expirationDate", "2022-07-10");
            a.put("creditLimit", "99999.00");
            step(s, day, "postBatch", batch("night-4", tx("0000000100000021", 4, "01", 1, "11.00", "2022-07-10T23:59:59.000000"),
                    tx("0000000100000022", 4, "01", 1, "12.00", "2022-07-11T00:00:00.000000"), tx("0000000100000023", 4, "01", 1, "13.00", null)));
            specs.add(spec(s, "expiry boundary", "POST-05"));
        }
        {
            ObjectNode s = scenario("post-overlimit-and-expired", "accounts", "over the limit and expired at once: 103 wins");
            cardDemo(s, 5);
            ObjectNode a = account(s, 5);
            a.put("expirationDate", "2022-01-31");
            a.put("creditLimit", "10.00");
            step(s, day, "postTransaction", post("rt-5", tx("0000000100000031", 5, "01", 1, "500.00", origin)));
            step(s, day, "postTransaction", post("rt-6", tx("0000000100000032", 5, "03", 1, "-1.00", origin)));
            specs.add(spec(s, "both refusals", "POST-06", "POST-04", "POST-05"));
        }
        {
            ObjectNode s = scenario("post-new-category-balance", "accounts", "a category the account has no balance for yet is created with the amount");
            cardDemo(s, 6);
            account(s, 6).put("creditLimit", "99999.00");
            step(s, day, "postBatch", batch("night-6", tx("0000000100000041", 6, "01", 4, "250.00", origin), tx("0000000100000042", 6, "01", 4, "-50.00", origin),
                    tx("0000000100000043", 6, "02", 2, "-75.25", origin)));
            specs.add(spec(s, "new category balance", "POST-07"));
        }
        // ---- interest
        {
            ObjectNode s = scenario("idem-failed-not-recorded", "accounts", "an interest run that throws is not recorded: the same key throws again");
            cardDemo(s, 7, 8);
            ((ObjectNode) s.get("initial")).putArray("disclosureRates");
            step(s, "2022-08-01T02:00:00.000000", "accrueInterest", accrue("int-1", "2022-07"));
            step(s, "2022-08-01T02:05:00.000000", "accrueInterest", accrue("int-1", "2022-07"));
            specs.add(new Spec("failed command", s, List.of("IDEM-02", "INT-10", "INT-05"), List.of("accounts", "ledger")));
        }
        {
            ObjectNode s = scenario("interest-replay-same-key", "accounts", "the same accrueInterest key twice");
            cardDemo(s, 9, 10);
            step(s, "2022-08-01T02:00:00.000000", "accrueInterest", accrue("int-2", "2022-07"));
            step(s, "2022-08-01T03:00:00.000000", "accrueInterest", accrue("int-2", "2022-07"));
            specs.add(spec(s, "replayed interest key", "IDEM-01"));
        }
        {
            ObjectNode s = scenario("interest-new-key-same-cycle", "accounts", "a second run for the same cycle under a new key skips every account");
            cardDemo(s, 11, 12, 13);
            step(s, "2022-08-01T02:00:00.000000", "accrueInterest", accrue("int-3", "2022-07"));
            step(s, "2022-08-02T02:00:00.000000", "accrueInterest", accrue("int-4", "2022-07"));
            specs.add(spec(s, "same cycle twice", "IDEM-03", "INT-02"));
        }
        {
            ObjectNode s = scenario("interest-next-cycle", "accounts", "July, then August: ids restart per cycle, accounts are closed again");
            cardDemo(s, 14, 15);
            step(s, "2022-08-01T02:00:00.000000", "accrueInterest", accrue("int-5", "2022-07"));
            step(s, "2022-09-01T02:00:00.000000", "accrueInterest", accrue("int-6", "2022-08"));
            specs.add(spec(s, "two cycles", "INT-09", "INT-11"));
        }
        {
            ObjectNode s = scenario("interest-preclosed", "accounts", "accounts already closed for the cycle or a later one are skipped");
            cardDemo(s, 16, 17, 18);
            account(s, 16).put("lastClosedCycle", "2022-07");
            account(s, 18).put("lastClosedCycle", "2022-09");
            account(s, 17).put("lastClosedCycle", "2022-06");
            step(s, "2022-08-01T02:00:00.000000", "accrueInterest", accrue("int-7", "2022-07"));
            specs.add(spec(s, "pre-closed accounts", "INT-02", "IDEM-03"));
        }
        {
            ObjectNode s = scenario("interest-ids-continue", "accounts", "interest ids continue after the cycle's interest entries already in the ledger");
            ObjectNode init = cardDemo(s, 19, 20);
            ((ArrayNode) init.get("ledger")).add(ledgerEntry("IN20220700000001", 21, "01", 5, "3.10", "2022-08-01T01:00:00.000000", "2022-07"));
            ((ArrayNode) init.get("ledger")).add(ledgerEntry("IN20220700000002", 21, "01", 5, "1.05", "2022-08-01T01:00:00.000000", "2022-07"));
            ((ArrayNode) init.get("ledger")).add(ledgerEntry("IN20220600000009", 21, "01", 5, "2.00", "2022-07-01T01:00:00.000000", "2022-06"));
            ((ArrayNode) init.get("cards")).add(cardOf(21));
            step(s, "2022-08-01T02:00:00.000000", "accrueInterest", accrue("int-8", "2022-07"));
            specs.add(spec(s, "ids continue", "INT-11"));
        }
        {
            ObjectNode s = scenario("interest-all-or-nothing", "accounts", "the third account's rate is missing: the run throws and changes nothing");
            ObjectNode init = cardDemo(s, 22, 23, 24);
            ArrayNode rates = init.putArray("disclosureRates");
            for (JsonNode r : cardDemo.path("disclosureRates")) if (!(r.path("typeCode").asText().equals("03"))) rates.add(r.deepCopy());
            step(s, "2022-08-01T02:00:00.000000", "accrueInterest", accrue("int-9", "2022-07"));
            specs.add(new Spec("all or nothing", s, List.of("INT-10", "INT-05"), List.of("accounts", "ledger")));
        }
        {
            ObjectNode s = scenario("interest-skipped-without-card", "accounts", "a skipped account needs no card; a processed one does");
            ObjectNode init = cardDemo(s, 25, 26);
            ArrayNode cards = init.putArray("cards");
            cards.add(cardOf(26));
            account(s, 25).put("lastClosedCycle", "2022-07");
            step(s, "2022-08-01T02:00:00.000000", "accrueInterest", accrue("int-10", "2022-07"));
            ObjectNode s2 = scenario("interest-missing-card-second", "accounts", "the second account has no card: the run throws and changes nothing");
            ObjectNode init2 = cardDemo(s2, 25, 26);
            init2.putArray("cards").add(cardOf(25));
            step(s2, "2022-08-01T02:00:00.000000", "accrueInterest", accrue("int-11", "2022-07"));
            specs.add(spec(s, "skipped without card", "INT-02", "INT-04"));
            specs.add(new Spec("missing card", s2, List.of("INT-04", "INT-10"), List.of("accounts", "ledger")));
        }
        {
            ObjectNode s = scenario("interest-group-rate", "accounts", "accounts with a disclosure group use its rates; others and missing entries fall back to DEFAULT");
            ObjectNode init = cardDemo(s, 27, 28, 29);
            account(s, 27).put("groupId", "A000000000");
            account(s, 28).put("groupId", "PREMIUM");
            ArrayNode rates = (ArrayNode) init.get("disclosureRates");
            rates.add(rate("PREMIUM", "01", 1, "9.99"));
            step(s, "2022-08-01T02:00:00.000000", "accrueInterest", accrue("int-12", "2022-07"));
            specs.add(spec(s, "group rates", "INT-05"));
        }
        {
            ObjectNode s = scenario("interest-after-postings", "accounts", "postings during the cycle, then the cycle close: interest on the new balances, cycle totals reset");
            cardDemo(s, 30, 31);
            account(s, 30).put("creditLimit", "99999.00");
            account(s, 31).put("creditLimit", "99999.00");
            step(s, day, "postBatch", batch("night-7", tx("0000000100000051", 30, "01", 1, "1000.00", origin), tx("0000000100000052", 31, "03", 1, "-400.00", origin),
                    tx("0000000100000053", 31, "01", 2, "333.33", origin)));
            step(s, "2022-08-01T02:00:00.000000", "accrueInterest", accrue("int-13", "2022-07"));
            specs.add(spec(s, "postings then interest", "INT-09", "POST-08", "INT-07"));
        }
        {
            ObjectNode s = scenario("interest-truncation", "accounts", "interest truncated toward zero to the cent, for positive and negative balances and odd rates");
            ObjectNode init = cardDemo(s, 32);
            ArrayNode balances = init.putArray("categoryBalances");
            String[][] b = {{"01", "1", "1234.57"}, {"01", "2", "-999.99"}, {"01", "3", "0.00"}, {"01", "4", "0.07"}, {"04", "1", "5000.01"}};
            for (String[] x : b) {
                ObjectNode n = balances.addObject();
                n.put("accountId", 32);
                n.put("typeCode", x[0]);
                n.put("categoryCode", Integer.parseInt(x[1]));
                n.put("balance", x[2]);
            }
            ArrayNode rates = init.putArray("disclosureRates");
            rates.add(rate("DEFAULT", "01", 1, "17.99"));
            rates.add(rate("DEFAULT", "01", 2, "23.45"));
            rates.add(rate("DEFAULT", "01", 3, "15.00"));
            rates.add(rate("DEFAULT", "01", 4, "99.99"));
            rates.add(rate("DEFAULT", "04", 1, "0.00"));
            step(s, "2022-08-01T02:00:00.000000", "accrueInterest", accrue("int-14", "2022-07"));
            specs.add(spec(s, "interest truncation", "INT-07", "INT-06"));
        }
    }

    static ObjectNode cardOf(long accountId) {
        for (JsonNode c : cardDemo.path("cards")) if (c.path("accountId").asLong() == accountId) return (ObjectNode) c.deepCopy();
        throw new IllegalArgumentException("card of " + accountId);
    }

    // ------------------------------------------------------------------------------------------------ reporting

    static void reporting(List<Spec> specs) {
        String origin = "2022-07-04T18:00:00.000000";
        {
            ObjectNode s = scenario("report-after-posting", "reporting", "today's postings reported next to older ledger entries");
            ObjectNode init = cardDemo(s, 33, 34);
            ((ArrayNode) init.get("ledger")).add(ledgerEntry("0000000099000001", 34, "01", 1, "77.70", "2022-07-01T09:00:00.000000", null));
            ((ArrayNode) init.get("ledger")).add(ledgerEntry("0000000099000002", 33, "01", 2, "12.00", "2022-06-30T09:00:00.000000", null));
            account(s, 33).put("creditLimit", "99999.00");
            account(s, 34).put("creditLimit", "99999.00");
            step(s, "2022-07-05T10:11:12.000000", "postBatch", batch("night-8", tx("0000000100000061", 34, "01", 1, "45.00", origin),
                    tx("0000000100000062", 33, "03", 1, "-9.99", origin), tx("0000000100000063", 34, "01", 1, "5.00", origin)));
            step(s, "2022-07-06T08:00:00.000000", "transactionReport", report("2022-07-01", "2022-07-05"));
            specs.add(spec(s, "report after posting", "RPT-01", "RPT-02"));
        }
        {
            ObjectNode s = scenario("report-ledger-order", "reporting", "the ledger holds entries out of card and id order: the report sorts them");
            ObjectNode init = cardDemo(s, 35, 36, 37);
            ArrayNode ledger = (ArrayNode) init.get("ledger");
            ledger.add(ledgerEntry("0000000099000105", 37, "01", 1, "5.05", "2022-07-02T09:00:00.000000", null));
            ledger.add(ledgerEntry("0000000099000101", 36, "02", 1, "-1.01", "2022-07-02T09:00:00.000000", null));
            ledger.add(ledgerEntry("0000000099000103", 37, "01", 2, "3.03", "2022-07-02T09:00:00.000000", null));
            ledger.add(ledgerEntry("0000000099000102", 35, "05", 1, "-2.02", "2022-07-02T09:00:00.000000", null));
            ledger.add(ledgerEntry("0000000099000104", 36, "07", 1, "4.04", "2022-07-03T09:00:00.000000", null));
            step(s, "2022-07-06T08:00:00.000000", "transactionReport", report("2022-07-01", "2022-07-31"));
            specs.add(spec(s, "report sorting", "RPT-02", "RPT-07"));
        }
        {
            ObjectNode s = scenario("report-with-interest", "reporting", "interest transactions are reported on their processed date");
            cardDemo(s, 38, 39);
            step(s, "2022-08-01T02:00:00.000000", "accrueInterest", accrue("int-15", "2022-07"));
            step(s, "2022-08-02T08:00:00.000000", "transactionReport", report("2022-08-01", "2022-08-01"));
            specs.add(spec(s, "interest in the report", "RPT-01", "INT-08"));
        }
        {
            ObjectNode s = scenario("report-lookups-in-period-only", "reporting", "an entry outside the period with an unknown type is never looked up");
            ObjectNode init = cardDemo(s, 40);
            ArrayNode ledger = (ArrayNode) init.get("ledger");
            ledger.add(ledgerEntry("0000000099000201", 40, "09", 1, "8.00", "2022-05-02T09:00:00.000000", null));
            ledger.add(ledgerEntry("0000000099000202", 40, "01", 1, "9.00", "2022-07-02T09:00:00.000000", null));
            ObjectNode unprocessed = ledgerEntry("0000000099000203", 40, "08", 1, "7.00", "2022-07-02T09:00:00.000000", null);
            unprocessed.putNull("processedTimestamp");
            ledger.add(unprocessed);
            step(s, "2022-07-06T08:00:00.000000", "transactionReport", report("2022-07-01", "2022-07-31"));
            specs.add(spec(s, "lookups only in the period", "RPT-04", "RPT-01"));
        }
        {
            ObjectNode s = scenario("report-unknown-category-in-period", "reporting", "an in-period entry with an unknown category stops the report");
            ObjectNode init = cardDemo(s, 41);
            ((ArrayNode) init.get("ledger")).add(ledgerEntry("0000000099000301", 41, "01", 9, "8.00", "2022-07-02T09:00:00.000000", null));
            step(s, "2022-07-06T08:00:00.000000", "transactionReport", report("2022-07-01", "2022-07-31"));
            specs.add(spec(s, "unknown category", "RPT-05"));
        }
    }

    // ------------------------------------------------------------------------------------------------ statements

    static ObjectNode statementBase(ObjectNode s) {
        ObjectNode init = cardDemo(s, 42, 43, 44);
        for (long id : new long[] {42, 43, 44}) account(s, id).put("creditLimit", "99999.00");
        account(s, 44).put("creditLimit", "100.00");
        init.set("customers", sam.path("customers").deepCopy());
        ArrayNode links = init.putArray("customerLinks");
        links.add(link("00001", 42));
        links.add(link("00002", 43, 44));
        links.add(link("00005", 42, 99));
        links.add(link("00007"));
        return init;
    }

    static ObjectNode link(String customer, long... accounts) {
        ObjectNode l = F.objectNode();
        l.put("customerId", customer);
        ArrayNode a = l.putArray("accountIds");
        for (long x : accounts) a.add(x);
        return l;
    }

    /** June, July and August activity for accounts 42 to 44, July's and August's interest (accrued at the start of the next month). */
    static void timeline(ObjectNode s) {
        String origin = "2022-06-01T12:00:00.000000";
        step(s, "2022-06-28T10:00:00.000000", "postTransaction", post("t-june", tx("0000000200000001", 42, "01", 1, "100.00", origin)));
        step(s, "2022-07-03T10:00:00.000000", "postBatch", batch("night-july-3",
                tx("0000000200000002", 42, "01", 1, "250.10", origin), tx("0000000200000003", 42, "01", 2, "60.00", origin),
                tx("0000000200000004", 42, "03", 1, "-30.05", origin), tx("0000000200000005", 43, "01", 1, "19.99", origin),
                tx("0000000200000006", 44, "01", 1, "500.00", origin), tx("0000000200000007", -1, "01", 1, "1.00", origin)));
        step(s, "2022-07-20T10:00:00.000000", "postTransaction", post("t-july-20", tx("0000000200000008", 42, "01", 1, "40.00", origin)));
        step(s, "2022-07-25T09:00:00.000000", "postTransaction", post("t-july-25", tx("0000000200000009", 44, "01", 4, "300.00", origin)));
        step(s, "2022-07-25T08:00:00.000000", "postTransaction", post("t-july-25b", tx("0000000200000010", 44, "01", 4, "250.00", origin)));
        step(s, "2022-08-01T02:00:00.000000", "accrueInterest", accrue("int-july", "2022-07"));
        step(s, "2022-08-05T10:00:00.000000", "postTransaction", post("t-aug-5", tx("0000000200000011", 42, "01", 1, "75.00", origin)));
        step(s, "2022-08-06T10:00:00.000000", "postTransaction", post("t-aug-6", tx("0000000200000012", 44, "01", 1, "999.00", origin)));
        step(s, "2022-09-01T02:00:00.000000", "accrueInterest", accrue("int-aug", "2022-08"));
    }

    static void statements(List<Spec> specs) {
        String q = "2022-09-02T08:00:00.000000";
        String[][] cases = {
                {"stmt-july-one-account", "00001", "2022-07", "July for a customer with one account: postings by category, interest charged in August for July, later activity excluded"},
                {"stmt-august-one-account", "00001", "2022-08", "August for the same customer"},
                {"stmt-june", "00001", "2022-06", "June: one posting, no interest"},
                {"stmt-before-activity", "00001", "2022-05", "a cycle before any activity: opening equals closing"},
                {"stmt-two-accounts-july", "00002", "2022-07", "two linked accounts in link order, rejections of the cycle"},
                {"stmt-two-accounts-august", "00002", "2022-08", "two linked accounts in August: an over-limit rejection"},
                {"stmt-no-accounts", "00007", "2022-07", "a link with no accounts"},
                {"stmt-unknown-customer", "00009", "2022-07", "no such customer"},
                {"stmt-contact-only", "99999", "2022-07", "the id has a contact but no profile"},
                {"stmt-no-link", "00003", "2022-07", "a customer without a link"},
                {"stmt-missing-account", "00005", "2022-07", "a linked account that does not exist"}};
        for (String[] c : cases) {
            ObjectNode s = scenario(c[0], "statements", c[3]);
            statementBase(s);
            timeline(s);
            step(s, q, "customerStatement", statement(c[1], c[2]));
            specs.add(spec(s, c[3]));
        }
        {
            ObjectNode s = scenario("stmt-monthly-average", "statements", "the 12-month average sums whole units and truncates");
            ObjectNode init = statementBase(s);
            String[] months = {"1000.99", "2000.50", "0.99", "15.00", "7.49", "123456.78", "0.00", "1.01", "999.99", "45.45", "10.10", "3.33"};
            for (JsonNode c : init.path("customers"))
                if (c.path("customerId").asText().equals("00002") && c.path("@type").asText().equals("CustomerProfile")) {
                    ArrayNode m = ((ObjectNode) c).putArray("monthlyAmounts");
                    for (String x : months) m.add(x);
                }
            step(s, q, "customerStatement", statement("00002", "2022-07"));
            specs.add(spec(s, "monthly average", "AVG-01", "STMT-03"));
        }
        {
            ObjectNode s = scenario("stmt-after-customer-changes", "statements", "the statement reflects customer changes applied before it");
            statementBase(s);
            ObjectNode in = F.objectNode();
            in.put("key", "changes-s1");
            ArrayNode ch = in.putArray("changes");
            ch.add(change("UPDATE", "00001A", "REPLACE", "NAME", "IBM CORPORATION"));
            ch.add(change("UPDATE", "00001A", "ADD", "ORDERS", "000001250"));
            step(s, "2022-07-10T09:00:00.000000", "applyCustomerChanges", in);
            step(s, q, "customerStatement", statement("00001", "2022-07"));
            specs.add(spec(s, "statement after changes", "STMT-03"));
        }
        {
            ObjectNode s = scenario("stmt-migrated-ledger", "statements", "migrated ledger entries: unprocessed ones are ignored, processed ones count in their month");
            ObjectNode init = statementBase(s);
            ArrayNode ledger = (ArrayNode) init.get("ledger");
            ledger.add(ledgerEntry("0000000099000401", 42, "01", 1, "10.00", "2022-07-02T09:00:00.000000", null));
            ObjectNode none = ledgerEntry("0000000099000402", 42, "01", 1, "20.00", "2022-07-02T09:00:00.000000", null);
            none.putNull("processedTimestamp");
            ledger.add(none);
            ledger.add(ledgerEntry("0000000099000403", 42, "01", 5, "3.00", "2022-08-01T01:00:00.000000", "2022-07"));
            ledger.add(ledgerEntry("0000000099000404", 42, "02", 1, "-50.00", "2022-08-03T09:00:00.000000", null));
            step(s, q, "customerStatement", statement("00001", "2022-07"));
            specs.add(spec(s, "migrated ledger", "STMT-04", "STMT-06", "STMT-07"));
        }
    }

    static ObjectNode change(String code, String key, String action, String field, String value) {
        ObjectNode c = F.objectNode();
        c.put("code", code);
        c.put("customerKey", key);
        c.put("action", action);
        c.put("fieldName", field);
        c.put("value", value);
        return c;
    }

    // ------------------------------------------------------------------------------------------------ customers

    static void customers(List<Spec> specs) {
        ObjectNode s = scenario("idem-customers-replay", "customers", "the same applyCustomerChanges key twice");
        ((ObjectNode) s.get("initial")).set("customers", sam.path("customers").deepCopy());
        ObjectNode in = F.objectNode();
        in.put("key", "changes-r1");
        ArrayNode ch = in.putArray("changes");
        ch.add(change("UPDATE", "00001A", "ADD", "BALANCE", "000010000"));
        ch.add(change("ADD", "00004A", "", "", ""));
        step(s, "2022-07-05T10:11:12.000000", "applyCustomerChanges", in);
        step(s, "2022-07-05T11:00:00.000000", "applyCustomerChanges", in.deepCopy());
        specs.add(spec(s, "replayed customer changes", "IDEM-01"));
    }
}
