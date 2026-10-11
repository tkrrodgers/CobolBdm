package com.mlcpu.cas.acceptance;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.node.ArrayNode;
import com.fasterxml.jackson.databind.node.ObjectNode;
import com.mlcpu.cas.api.CustomerAccountService;
import com.mlcpu.cas.api.ReferenceDataMissing;
import com.mlcpu.cas.api.accounts.AccrueInterest;
import com.mlcpu.cas.api.accounts.PostBatch;
import com.mlcpu.cas.api.accounts.PostTransaction;
import com.mlcpu.cas.api.customers.ApplyCustomerChanges;
import com.mlcpu.cas.api.ports.AccountRecord;
import com.mlcpu.cas.api.ports.CardRecord;
import com.mlcpu.cas.api.ports.CategoryBalanceRecord;
import com.mlcpu.cas.api.ports.CustomerLink;
import com.mlcpu.cas.api.ports.DisclosureRate;
import com.mlcpu.cas.api.ports.LedgerEntry;
import com.mlcpu.cas.api.ports.MasterRecord;
import com.mlcpu.cas.api.ports.RejectionRecord;
import com.mlcpu.cas.api.ports.TransactionCategoryRecord;
import com.mlcpu.cas.api.ports.TransactionTypeRecord;
import com.mlcpu.cas.api.reporting.TransactionReportQuery;
import com.mlcpu.cas.api.statements.StatementQuery;
import java.lang.reflect.ParameterizedType;
import java.lang.reflect.Type;
import java.time.LocalDateTime;
import java.util.ArrayList;
import java.util.List;
import java.util.function.Function;

/**
 * One acceptance scenario (JSON): initial state, steps (time, command or query, expected result or exception) and the expected
 * state of the stores it names. {@link #run} executes it against a service and returns the differences.
 */
public record Scenario(String id, String slice, String visibility, JsonNode json) {

    static Type listOf(Class<?> c) {
        return new ParameterizedType() {
            public Type[] getActualTypeArguments() { return new Type[] {c}; }
            public Type getRawType() { return List.class; }
            public Type getOwnerType() { return null; }
        };
    }

    /** Loads the initial state into a fresh set of in-memory ports. */
    public InMemoryPorts load() {
        InMemoryPorts p = new InMemoryPorts();
        JsonNode s = json.path("initial");
        for (AccountRecord a : list(s, "accounts", AccountRecord.class)) p.accounts.put(a.accountId(), a);
        for (CardRecord c : list(s, "cards", CardRecord.class)) p.cards.put(c.cardNumber(), c);
        for (CategoryBalanceRecord b : list(s, "categoryBalances", CategoryBalanceRecord.class))
            p.categoryBalances.put(InMemoryPorts.balanceKey(b.accountId(), b.typeCode(), b.categoryCode()), b);
        for (DisclosureRate r : list(s, "disclosureRates", DisclosureRate.class))
            p.disclosureRates.put(InMemoryPorts.rateKey(r.groupId(), r.typeCode(), r.categoryCode()), r);
        for (TransactionTypeRecord t : list(s, "transactionTypes", TransactionTypeRecord.class)) p.transactionTypes.put(t.typeCode(), t);
        for (TransactionCategoryRecord t : list(s, "transactionCategories", TransactionCategoryRecord.class))
            p.transactionCategories.put(t.typeCode() + "|" + t.categoryCode(), t);
        for (LedgerEntry e : list(s, "ledger", LedgerEntry.class)) p.addLedger(e);
        p.rejections.addAll(list(s, "rejections", RejectionRecord.class));
        for (MasterRecord r : list(s, "customers", MasterRecord.class)) p.customers.put(r.key(), r);
        for (CustomerLink l : list(s, "customerLinks", CustomerLink.class)) p.customerLinks.put(l.customerId(), l);
        return p;
    }

    static <T> List<T> list(JsonNode s, String name, Class<T> type) {
        JsonNode n = s.get(name);
        return n == null || n.isNull() ? List.of() : Json.to(n, listOf(type));
    }

    /** What a step produced: its canonical result, or the exception it threw. */
    public static JsonNode execute(CustomerAccountService service, String command, JsonNode input) {
        try {
            Object result = switch (command) {
                case "postTransaction" -> service.postTransaction(Json.to(input, PostTransaction.class));
                case "postBatch" -> service.postBatch(Json.to(input, PostBatch.class));
                case "accrueInterest" -> service.accrueInterest(Json.to(input, AccrueInterest.class));
                case "applyCustomerChanges" -> service.applyCustomerChanges(Json.to(input, ApplyCustomerChanges.class));
                case "transactionReport" -> service.transactionReport(Json.to(input, TransactionReportQuery.class));
                case "customerStatement" -> service.customerStatement(Json.to(input, StatementQuery.class));
                default -> throw new IllegalArgumentException("unknown command " + command);
            };
            ObjectNode o = Json.F.objectNode();
            o.set("result", Json.of(result));
            return o;
        } catch (ReferenceDataMissing e) {
            ObjectNode o = Json.F.objectNode();
            ObjectNode t = o.putObject("throws");
            t.put("type", "ReferenceDataMissing");
            t.put("what", e.what());
            t.put("key", e.key());
            return o;
        } catch (RuntimeException e) {
            ObjectNode o = Json.F.objectNode();
            ObjectNode t = o.putObject("throws");
            t.put("type", e.getClass().getSimpleName());
            t.put("message", String.valueOf(e.getMessage()));
            StackTraceElement at = java.util.Arrays.stream(e.getStackTrace()).filter(f -> f.getClassName().startsWith("com.mlcpu.cas.")
                    && !f.getClassName().startsWith("com.mlcpu.cas.acceptance")).findFirst().orElse(null);
            if (at != null) t.put("at", at.getClassName() + "." + at.getMethodName() + "(" + at.getFileName() + ":" + at.getLineNumber() + ")");
            return o;
        }
    }

    /** The stores a scenario can check, in canonical form and canonical order. */
    public static ObjectNode state(InMemoryPorts p) {
        ObjectNode s = Json.F.objectNode();
        s.set("accounts", Json.of(List.copyOf(p.accounts.values())));
        s.set("categoryBalances", Json.of(List.copyOf(p.categoryBalances.values())));
        s.set("ledger", Json.of(List.copyOf(p.ledger)));
        s.set("rejections", Json.of(List.copyOf(p.rejections)));
        s.set("customers", Json.of(List.copyOf(p.customers.values())));
        return s;
    }

    /** Runs the scenario on a service built by {@code factory}; returns the first differences (empty when it passes). */
    public List<String> run(Function<InMemoryPorts, CustomerAccountService> factory, int limit) {
        InMemoryPorts p = load();
        CustomerAccountService service = factory.apply(p);
        List<String> out = new ArrayList<>();
        ArrayNode steps = (ArrayNode) json.path("steps");
        for (int i = 0; i < steps.size() && out.size() < limit; i++) {
            JsonNode step = steps.get(i);
            p.clock.set(LocalDateTime.parse(step.path("at").asText()));
            String command = step.path("command").asText();
            JsonNode actual = execute(service, command, step.path("input"));
            for (String d : Json.differences("step " + (i + 1) + " " + command, step.path("expect"), actual, limit - out.size())) out.add(d);
        }
        JsonNode expectState = json.path("expectState");
        if (out.size() < limit && expectState.isObject()) {
            ObjectNode actual = state(p);
            expectState.fieldNames().forEachRemaining(store -> {
                if (out.size() < limit)
                    out.addAll(Json.differences("state." + store, expectState.get(store), actual.get(store), limit - out.size()));
            });
        }
        return out;
    }
}
