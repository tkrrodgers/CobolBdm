package com.mlcpu.cas.acceptance.sme;

import com.fasterxml.jackson.databind.JsonNode;
import java.util.Set;
import java.util.TreeSet;

/** The rules a scenario exercises, from its steps' commands and expected results (and, for a few, its title). */
final class Tags {
    private Tags() {}

    static Set<String> of(JsonNode s, String title) {
        Set<String> t = new TreeSet<>();
        String name = title.toLowerCase();
        for (JsonNode step : s.path("steps")) {
            String command = step.path("command").asText();
            JsonNode result = step.path("expect").path("result");
            JsonNode thrown = step.path("expect").path("throws");
            switch (command) {
                case "postTransaction" -> posting(t, result);
                case "postBatch" -> {
                    t.add("POST-12");
                    t.add("POST-13");
                    for (JsonNode o : result.path("outcomes")) posting(t, o);
                }
                case "accrueInterest" -> interest(t, result, thrown);
                case "applyCustomerChanges" -> customers(t, step.path("input").path("changes"), result);
                case "transactionReport" -> report(t, result, thrown);
                case "customerStatement" -> statement(t, result, thrown);
                default -> { }
            }
        }
        if (name.contains("q-03") || name.contains("103 wins")) t.add("POST-06");
        if (name.contains("expir") || name.contains("blank origin")) t.add("POST-05");
        if (name.contains("limit")) t.add("POST-04");
        if (name.contains("credits and debits")) t.add("POST-08");
        if (name.contains("no category balances yet")) t.add("POST-07");
        if (name.contains("default")) t.add("INT-05");
        if (name.contains("zero rates")) t.add("INT-06");
        if (name.contains("negative")) t.add(name.contains("rate") || name.contains("category balances") ? "INT-07" : "RPT-08");
        if (name.contains("period") || name.contains("boundar") || name.contains("blank processed")) t.add("RPT-01");
        return t;
    }

    static void posting(Set<String> t, JsonNode o) {
        switch (o.path("@type").asText()) {
            case "Posted" -> {
                t.add("POST-07");
                t.add("POST-08");
                t.add("POST-09");
                t.add("POST-10");
            }
            case "Rejected" -> {
                t.add("POST-11");
                t.add(switch (o.path("reason").asText()) {
                    case "DUPLICATE_TRANSACTION" -> "POST-01";
                    case "INVALID_CARD" -> "POST-02";
                    case "ACCOUNT_NOT_FOUND" -> "POST-03";
                    case "OVERLIMIT" -> "POST-04";
                    default -> "POST-05";
                });
            }
            default -> { }
        }
    }

    static void interest(Set<String> t, JsonNode r, JsonNode thrown) {
        if (thrown.isObject()) {
            t.add("INT-10");
            t.add(switch (thrown.path("what").asText()) {
                case "account" -> "INT-03";
                case "card" -> "INT-04";
                default -> "INT-05";
            });
            return;
        }
        t.add("INT-01");
        t.add("INT-12");
        if (!r.path("charges").isEmpty()) {
            t.add("INT-07");
            t.add("INT-08");
            t.add("INT-11");
        }
        if (!r.path("accounts").isEmpty()) t.add("INT-09");
        if (!r.path("skippedAccounts").isEmpty()) t.add("INT-02");
    }

    static void customers(Set<String> t, JsonNode changes, JsonNode r) {
        t.add("CUST-08");
        t.add("CUST-10");
        Set<Integer> failed = new TreeSet<>();
        int read = 0;
        for (JsonNode e : r.path("errors")) {
            failed.add(e.path("index").asInt());
            t.add("CUST-09");
            String code = changes.path(e.path("index").asInt()).path("code").asText();
            t.add(switch (e.path("reason").asText()) {
                case "OUT_OF_SEQUENCE" -> "CUST-03";
                case "INVALID_CODE" -> "CUST-04";
                case "DUPLICATE_KEY" -> "CUST-05";
                case "NO_MATCHING_KEY" -> code.equals("DELETE") ? "CUST-06" : "CUST-07";
                case "NOT_NUMERIC" -> "UPD-03";
                case "INVALID_ACTION" -> "UPD-05";
                default -> "UPD-06";
            });
            if (e.path("reason").asText().equals("INVALID_ACTION")) {
                String field = changes.path(e.path("index").asInt()).path("fieldName").asText();
                if (field.equals("BALANCE") || field.equals("ORDERS")) t.add("UPD-04");
            }
        }
        for (int i = 0; i < changes.size(); i++) {
            JsonNode c = changes.get(i);
            if (c.path("code").asText().startsWith("*")) {
                t.add("CUST-02");
                continue;
            }
            read++;
            String field = c.path("fieldName").asText();
            String value = String.format("%-36s", c.path("value").asText());
            boolean numeric = field.equals("BALANCE") || field.equals("ORDERS");
            if (c.path("code").asText().equals("UPDATE") && numeric) {
                if (!Character.isDigit(value.charAt(7)) || !Character.isDigit(value.charAt(8))) t.add("UPD-01");
                if (value.charAt(0) == '-' || value.charAt(0) == '+') t.add("UPD-02");
            }
            if (failed.contains(i)) continue;
            switch (c.path("code").asText()) {
                case "ADD" -> t.add("CUST-05");
                case "DELETE" -> t.add("CUST-06");
                case "UPDATE" -> {
                    t.add("CUST-07");
                    t.add(switch (field) {
                        case "NAME" -> "UPD-07";
                        case "BALANCE" -> "UPD-08";
                        default -> "UPD-09";
                    });
                    if (numeric) t.add("UPD-03");
                }
                default -> { }
            }
        }
        if (read > 1) t.add("CUST-01");
    }

    static void report(Set<String> t, JsonNode r, JsonNode thrown) {
        if (thrown.isObject()) {
            t.add(switch (thrown.path("what").asText()) {
                case "card" -> "RPT-03";
                case "transaction type" -> "RPT-04";
                default -> "RPT-05";
            });
            return;
        }
        t.add("RPT-01");
        t.add("RPT-09");
        if (!r.path("cards").isEmpty()) {
            t.add("RPT-02");
            t.add("RPT-03");
            t.add("RPT-04");
            t.add("RPT-05");
            t.add("RPT-06");
            t.add("RPT-07");
            t.add("RPT-08");
        }
    }

    static void statement(Set<String> t, JsonNode r, JsonNode thrown) {
        if (thrown.isObject()) {
            t.add(thrown.path("what").asText().equals("customer") ? "STMT-01" : "STMT-02");
            return;
        }
        t.add("STMT-01");
        t.add("STMT-02");
        t.add("STMT-03");
        t.add("STMT-10");
        t.add("AVG-01");
        for (JsonNode a : r.path("accounts")) {
            t.add("STMT-04");
            t.add("STMT-07");
            t.add("STMT-08");
            if (!a.path("activity").isEmpty()) t.add("STMT-05");
            if (!a.path("interest").asText().equals("0.00")) t.add("STMT-06");
            if (!a.path("rejections").isEmpty()) t.add("STMT-09");
        }
    }
}
