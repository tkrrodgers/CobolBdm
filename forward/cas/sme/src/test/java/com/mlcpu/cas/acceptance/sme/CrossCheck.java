package com.mlcpu.cas.acceptance.sme;

import com.fasterxml.jackson.databind.JsonNode;
import java.math.BigDecimal;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

/**
 * The reference implementation's result for a legacy-derived scenario against the faithful program's decoded outcome. Each
 * difference is listed; the first line says whether the decisions of SPEC.md section 8 explain them (the SME reviews every one).
 */
final class CrossCheck {
    private CrossCheck() {}

    static List<String> of(JsonNode draft, JsonNode scenario) {
        List<String> d = new ArrayList<>();
        String name = draft.path("name").asText();
        JsonNode legacy = draft.path("legacy");
        JsonNode step = scenario.path("steps").get(0);
        JsonNode expect = step.path("expect");
        JsonNode state = scenario.path("expectState");
        String explanation = null;
        switch (draft.path("program").asText()) {
            case "CBTRN02C" -> {
                posting(d, legacy, expect, state, step.path("command").asText());
                if (name.contains("already posted")) explanation = "D-8 (the legacy's transaction file holds only the day, so it posts the id again; the service rejects it with 104)";
            }
            case "CBACT04C" -> explanation = interest(d, legacy, expect, state);
            case "CBTRN03C" -> explanation = report(d, legacy, expect);
            case "SAM1" -> explanation = customers(d, draft, legacy, expect, state);
            default -> d.add("no cross-check for " + draft.path("program").asText());
        }
        if (d.isEmpty()) return d;
        List<String> out = new ArrayList<>();
        out.add(explanation != null ? "explained by " + explanation : "UNEXPLAINED: review");
        out.addAll(d.subList(0, Math.min(d.size(), 25)));
        if (d.size() > 25) out.add("... " + (d.size() - 25) + " more");
        return out;
    }

    static String code(JsonNode o) {
        if (o.path("posted").asBoolean()) return "posted";
        if (o.has("reason")) return o.path("reason").isNumber() ? String.valueOf(o.path("reason").asInt()) : o.path("reason").asText();
        return "unknown";
    }

    static final Map<String, String> REASON_CODES = Map.of("INVALID_CARD", "100", "ACCOUNT_NOT_FOUND", "101", "OVERLIMIT", "102",
            "EXPIRED_ACCOUNT", "103", "DUPLICATE_TRANSACTION", "104");

    static void posting(List<String> d, JsonNode legacy, JsonNode expect, JsonNode state, String command) {
        if (legacy.path("returnCode").asInt() > 4) d.add("legacy ended with return code " + legacy.path("returnCode").asInt() + " (" + legacy.path("detail").asText() + ")");
        List<JsonNode> ref = new ArrayList<>();
        if (command.equals("postBatch")) expect.path("result").path("outcomes").forEach(ref::add);
        else ref.add(expect.path("result"));
        JsonNode lo = legacy.path("outcomes");
        for (int i = 0; i < Math.max(ref.size(), lo.size()); i++) {
            String l = i < lo.size() ? code(lo.get(i)) : "(none)";
            String r = i < ref.size() ? (ref.get(i).path("@type").asText().equals("Posted") ? "posted" : REASON_CODES.get(ref.get(i).path("reason").asText())) : "(none)";
            if (!l.equals(r)) d.add("transaction " + (i + 1) + " " + (i < lo.size() ? lo.get(i).path("transactionId").asText() : "") + ": legacy " + l + ", reference " + r);
        }
        compareById(d, "account", legacy.path("accounts"), state.path("accounts"), "accountId", List.of("lastClosedCycle"));
        compareByKey(d, "category balance", legacy.path("categoryBalances"), state.path("categoryBalances"));
        Map<String, JsonNode> refLedger = new HashMap<>();
        state.path("ledger").forEach(e -> refLedger.put(e.path("transactionId").asText(), e));
        for (JsonNode e : legacy.path("ledger")) {
            JsonNode r = refLedger.remove(e.path("transactionId").asText());
            if (r == null) {
                d.add("ledger " + e.path("transactionId").asText() + ": only in the legacy transaction file");
                continue;
            }
            for (String f : List.of("cardNumber", "typeCode", "categoryCode", "source", "description", "amount", "merchantId", "merchantName",
                    "merchantCity", "merchantZip", "originTimestamp", "processedTimestamp"))
                if (!same(e.path(f), r.path(f))) d.add("ledger " + e.path("transactionId").asText() + "." + f + ": legacy " + e.path(f) + ", reference " + r.path(f));
        }
        refLedger.keySet().forEach(k -> d.add("ledger " + k + ": only in the reference ledger"));
        List<String> lr = new ArrayList<>(), rr = new ArrayList<>();
        legacy.path("rejects").forEach(r -> lr.add(r.path("transactionId").asText() + ":" + r.path("reason").asInt()));
        state.path("rejections").forEach(r -> rr.add(r.path("transaction").path("transactionId").asText() + ":" + REASON_CODES.get(r.path("reason").asText())));
        if (!lr.equals(rr)) d.add("rejections: legacy " + lr + ", reference " + rr);
    }

    static String interest(List<String> d, JsonNode legacy, JsonNode expect, JsonNode state) {
        boolean abended = legacy.path("returnCode").asInt() != 0 || legacy.path("detail").asText().toLowerCase().contains("abend");
        if (expect.has("throws")) {
            if (abended) {
                d.add("legacy abended (" + legacy.path("detail").asText() + ", return code " + legacy.path("returnCode").asInt() + "); the reference throws "
                      + expect.path("throws") + " and changes nothing");
                return "D-9 (all-or-nothing)";
            }
            d.add("the reference throws " + expect.path("throws") + " but the legacy ended normally");
            return null;
        }
        if (abended) d.add("legacy abended (" + legacy.path("detail").asText() + ") but the reference completed");
        List<JsonNode> charges = new ArrayList<>();
        state.path("ledger").forEach(e -> { if (!e.path("interestCycle").isNull()) charges.add(e); });
        JsonNode lt = legacy.path("transactions");
        for (int i = 0; i < Math.max(charges.size(), lt.size()); i++) {
            if (i >= charges.size() || i >= lt.size()) {
                d.add("interest transaction " + (i + 1) + ": only in the " + (i >= charges.size() ? "legacy" : "reference"));
                continue;
            }
            for (String f : List.of("cardNumber", "typeCode", "categoryCode", "source", "description", "amount", "merchantId"))
                if (!same(charges.get(i).path(f), lt.get(i).path(f)))
                    d.add("interest transaction " + (i + 1) + "." + f + ": legacy " + lt.get(i).path(f) + ", reference " + charges.get(i).path(f));
        }
        // D-1: the legacy never closes the last account of the run.
        String last = expect.path("result").path("accounts").isEmpty() ? null
                : expect.path("result").path("accounts").get(expect.path("result").path("accounts").size() - 1).path("accountId").asText();
        List<String> before = new ArrayList<>(d);
        compareById(d, "account", legacy.path("accounts"), state.path("accounts"), "accountId", List.of("lastClosedCycle"));
        boolean onlyLast = d.subList(before.size(), d.size()).stream().allMatch(x -> last != null && x.startsWith("account " + last + "."));
        if (d.size() > before.size() && onlyLast && before.isEmpty()) return "D-1 (the last account " + last + " is closed by the service, never by the legacy)";
        return null;
    }

    static String report(List<String> d, JsonNode legacy, JsonNode expect) {
        boolean abended = legacy.path("returnCode").asInt() != 0;
        if (expect.has("throws")) {
            if (abended) {
                d.add("legacy abended (return code " + legacy.path("returnCode").asInt() + "); the reference throws " + expect.path("throws"));
                return "the legacy abend on missing reference data (the service throws ReferenceDataMissing, SPEC section 4)";
            }
            d.add("the reference throws " + expect.path("throws") + " but the legacy ended normally");
            return null;
        }
        JsonNode rep = legacy.path("report");
        List<JsonNode> ref = new ArrayList<>();
        List<BigDecimal> refTotals = new ArrayList<>();
        for (JsonNode g : expect.path("result").path("cards")) {
            g.path("lines").forEach(ref::add);
            refTotals.add(new BigDecimal(g.path("accountTotal").asText()));
        }
        JsonNode ll = rep.path("lines");
        boolean prefix = ll.size() < ref.size();
        for (int i = 0; i < Math.max(ll.size(), ref.size()); i++) {
            if (i >= ll.size() || i >= ref.size()) {
                d.add("line " + (i + 1) + ": only in the " + (i >= ll.size() ? "reference" : "legacy") + (i < ref.size() ? " " + ref.get(i).path("transactionId").asText() : ""));
                if (d.size() > 5) break;
                continue;
            }
            JsonNode l = ll.get(i), r = ref.get(i);
            List<String> fields = new ArrayList<>();
            if (!l.path("transactionId").asText().equals(r.path("transactionId").asText())) fields.add("transactionId");
            if (l.path("accountId").asLong() != r.path("accountId").asLong()) fields.add("accountId");
            if (!l.path("typeCode").asText().equals(r.path("typeCode").asText())) fields.add("typeCode");
            if (!clip(r.path("typeDescription").asText(), 15).equals(l.path("typeDescription").asText())) fields.add("typeDescription");
            if (l.path("categoryCode").asInt() != r.path("categoryCode").asInt()) fields.add("categoryCode");
            if (!clip(r.path("categoryDescription").asText(), 29).equals(l.path("categoryDescription").asText())) fields.add("categoryDescription");
            if (!clip(r.path("source").asText(), 10).equals(l.path("source").asText())) fields.add("source");
            if (dec(l.path("amount")).compareTo(dec(r.path("amount"))) != 0) fields.add("amount");
            if (!fields.isEmpty()) {
                prefix = false;
                d.add("line " + (i + 1) + " differs in " + fields + ": legacy " + l + ", reference " + r);
            }
        }
        List<BigDecimal> lt = new ArrayList<>();
        rep.path("accountTotals").forEach(x -> lt.add(dec(x)));
        boolean totalsExplained = !refTotals.isEmpty() && lt.size() == refTotals.size() - 1 && sameValues(lt, refTotals.subList(0, lt.size()));
        if (!sameValues(lt, refTotals)) d.add("account totals: legacy " + lt + ", reference " + refTotals);
        BigDecimal grand = new BigDecimal(expect.path("result").path("grandTotal").asText());
        BigDecimal lastAmount = ref.isEmpty() ? BigDecimal.ZERO : new BigDecimal(ref.get(ref.size() - 1).path("amount").asText());
        if (!rep.path("grandTotal").isNull() && dec(rep.path("grandTotal")).compareTo(grand) != 0)
            d.add("grand total: legacy " + rep.path("grandTotal").asText() + ", reference " + grand + " (reference + last amount = " + grand.add(lastAmount) + ")");
        if (rep.path("grandTotal").isNull() && !ref.isEmpty()) d.add("grand total: none in the legacy report, reference " + grand);
        boolean grandExplained = rep.path("grandTotal").isNull() || dec(rep.path("grandTotal")).compareTo(grand.add(lastAmount)) == 0;
        if (prefix) return "D-3 (the legacy stops at the first transaction outside the period) and D-2";
        if (totalsExplained && grandExplained) return "D-2 (the legacy omits the last account total and adds the last amount twice)";
        return null;
    }

    /** A legacy edited amount; blank (zero edited by Z suppression) reads as 0. */
    static BigDecimal dec(JsonNode n) {
        return n == null || n.isNull() || n.asText().isBlank() ? BigDecimal.ZERO : new BigDecimal(n.asText());
    }

    static boolean sameValues(List<BigDecimal> a, List<BigDecimal> b) {
        if (a.size() != b.size()) return false;
        for (int i = 0; i < a.size(); i++) if (a.get(i).compareTo(b.get(i)) != 0) return false;
        return true;
    }

    static String clip(String s, int n) {
        return s.length() <= n ? s : s.substring(0, n).stripTrailing();
    }

    static final Map<String, String> MESSAGES = Map.of("TRANSACTION OUT OF SEQUENCE", "OUT_OF_SEQUENCE", "INVALID TRAN CODE:", "INVALID_CODE",
            "NO MATCHING KEY:", "NO_MATCHING_KEY", "DUPLICATE KEY:", "DUPLICATE_KEY", "DATA IS NOT NUMERIC", "NOT_NUMERIC",
            "INVALID ACTION CODE", "INVALID_ACTION", "FIELD NAME INVALID", "INVALID_FIELD");

    static String customers(List<String> d, JsonNode draft, JsonNode legacy, JsonNode expect, JsonNode state) {
        JsonNode rep = legacy.path("report");
        List<String> le = new ArrayList<>(), re = new ArrayList<>();
        rep.path("errors").forEach(e -> le.add(e.path("change").asText().substring(7, Math.min(13, e.path("change").asText().length())).trim() + ":"
                                                + MESSAGES.getOrDefault(e.path("message").asText(), e.path("message").asText())));
        expect.path("result").path("errors").forEach(e -> re.add(e.path("customerKey").asText() + ":" + e.path("reason").asText()));
        if (!le.equals(re)) d.add("errors: legacy " + le + ", reference " + re);
        if (!sameTree(rep.path("totals"), expect.path("result").path("totals"))) d.add("totals: legacy " + rep.path("totals") + ", reference " + expect.path("result").path("totals"));
        compareByKey(d, "customer", legacy.path("customers"), state.path("customers"));
        if (d.isEmpty()) return null;
        JsonNode changes = draft.path("input").path("changes");
        List<String> reasons = new ArrayList<>();
        boolean comment = false, contactNumber = false, contactAdd = false, sameBatch = false, profileAdd = false;
        java.util.Set<String> added = new java.util.HashSet<>();
        for (JsonNode c : changes) {
            String code = c.path("code").asText(), key = c.path("customerKey").asText(), field = c.path("fieldName").asText();
            if (code.startsWith("*")) comment = true;
            if (code.equals("UPDATE") && !key.endsWith("A") && (field.equals("BALANCE") || field.equals("ORDERS"))) contactNumber = true;
            if (code.equals("ADD") && !key.endsWith("A")) contactAdd = true;
            if (code.equals("ADD") && key.endsWith("A")) profileAdd = true;
            if ((code.equals("UPDATE") || code.equals("DELETE") || code.equals("ADD")) && added.contains(key)) sameBatch = true;
            if (code.equals("ADD")) added.add(key);
            if (code.equals("DELETE")) added.remove(key);
        }
        if (comment) reasons.add("D-4 (a comment line sets the legacy's previous key)");
        if (contactNumber) reasons.add("D-5 (a BALANCE or ORDERS update of a contact)");
        if (contactAdd) reasons.add("D-6 (an added contact)");
        if (sameBatch) reasons.add("D-7 (a record added earlier in the batch)");
        if (profileAdd) reasons.add("D-11 (an added profile's occupation)");
        return reasons.isEmpty() ? null : String.join(", ", reasons);
    }

    /** Equal values: numbers by value (an int and a long node of the same number are equal). */
    static boolean same(JsonNode a, JsonNode b) {
        if (a.isNumber() && b.isNumber()) return a.decimalValue().compareTo(b.decimalValue()) == 0;
        return a.equals(b);
    }

    static boolean sameTree(JsonNode a, JsonNode b) {
        return com.mlcpu.cas.acceptance.Json.differences("", a, b, 1).isEmpty();
    }

    static void compareById(List<String> d, String what, JsonNode legacy, JsonNode ref, String id, List<String> ignore) {
        Map<String, JsonNode> r = new HashMap<>();
        ref.forEach(x -> r.put(x.path(id).asText(), x));
        for (JsonNode l : legacy) {
            JsonNode x = r.remove(l.path(id).asText());
            if (x == null) {
                d.add(what + " " + l.path(id).asText() + ": only in the legacy");
                continue;
            }
            l.fieldNames().forEachRemaining(f -> {
                if (!ignore.contains(f) && !same(l.path(f), x.path(f)))
                    d.add(what + " " + l.path(id).asText() + "." + f + ": legacy " + l.path(f) + ", reference " + x.path(f));
            });
        }
        r.keySet().forEach(k -> d.add(what + " " + k + ": only in the reference"));
    }

    static String key(JsonNode n) {
        if (n.has("customerId")) return n.path("customerId").asText() + (n.has("recordType") ? n.path("recordType").asText() : "A");
        return n.path("accountId").asText() + "|" + n.path("typeCode").asText() + "|" + n.path("categoryCode").asText();
    }

    static void compareByKey(List<String> d, String what, JsonNode legacy, JsonNode ref) {
        Map<String, JsonNode> r = new HashMap<>();
        ref.forEach(x -> r.put(key(x), x));
        for (JsonNode l : legacy) {
            JsonNode x = r.remove(key(l));
            if (x == null) {
                d.add(what + " " + key(l) + ": only in the legacy");
                continue;
            }
            l.fieldNames().forEachRemaining(f -> {
                if (!same(l.path(f), x.path(f))) d.add(what + " " + key(l) + "." + f + ": legacy " + l.path(f) + ", reference " + x.path(f));
            });
        }
        r.keySet().forEach(k -> d.add(what + " " + k + ": only in the reference"));
    }
}
