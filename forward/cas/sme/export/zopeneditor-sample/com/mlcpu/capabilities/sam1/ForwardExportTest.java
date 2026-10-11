package com.mlcpu.capabilities.sam1;

import static com.mlcpu.forwardexport.Jw.obj;
import static com.mlcpu.forwardexport.Jw.text;

import com.mlcpu.forwardexport.Jw;
import com.mlcpu.rt.StepEquivalence;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.math.BigDecimal;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.TreeMap;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/**
 * Forward-engineering export (forward/cas): SAM1 (with SAM2) run by the faithful programs on the oracle scenarios, their change-file
 * variations, every change of the all-transactions scenario alone, SME-authored single changes and SME-authored batches; the
 * customer master and the report decoded into the Customer Account Service's terms.
 */
class ForwardExportTest {
    static final String AT = "2022-07-05T10:11:12.000000";
    static final String CUST = "IBMUSER.SAMPLE.CUSTFILE", TRAN = "IBMUSER.SAMPLE.TRANFILE";

    @BeforeAll
    static void oracle() {
        StepEquivalence.pinOracleEnvironment(Sam1EquivalenceTest.CLOCK);
    }

    /** Single changes the SME adds to reach every update rule (applied alone to the all-transactions master). */
    static final List<String> AUTHORED = List.of(
            "UPDATE 00001A        REPLACE  BALANCE    01 999999999",
            "UPDATE 00001A        ADD      BALANCE    01 +999999999",
            "UPDATE 00001A        ADD      BALANCE    01 -999999999",
            "UPDATE 00001A        REPLACE  BALANCE    01 -000012345",
            "UPDATE 00001A        REPLACE  BALANCE    01 0000123AB",
            "UPDATE 00001A        REPLACE  BALANCE    01 +12345678",
            "UPDATE 00001A        REPLACE  BALANCE    01 +0000001.5",
            "UPDATE 00001A        REPLACE  BALANCE    01",
            "UPDATE 00001A        REPLACE  ORDERS     01 +000123456",
            "UPDATE 00001A        ADD      ORDERS     01 -000009999",
            "UPDATE 00001A        ADD      ORDERS     01 +999999999",
            "UPDATE 00001A        REPLACE  ORDERS     01 1234567AB",
            "UPDATE 00001A        ADD      ORDERS     01 000000099",
            "UPDATE 00001A        REPLACE  NAME       01 A VERY LONG CUSTOMER NAME THAT IS CUT",
            "UPDATE 00001A        ADD      NAME       01 ADDED NAME",
            "UPDATE 00001A        REPLACE  NAME       01",
            "UPDATE 00001A        DELETE   BALANCE    01 ABCDEFGHI",
            "UPDATE 00001A        DELETE   PHONE      01 5551212",
            "UPDATE 00001A        REPLACE  balance    01 +000000100",
            "UPDATE 00001A        MERGE    NAME       01 X",
            "UPDATE 99999C        REPLACE  NAME       01 NEW CONTACT NAME",
            "UPDATE 99999C        REPLACE  BALANCE    01 +000000100",
            "UPDATE 99999C        ADD      ORDERS     01 +000000100",
            "UPDATE 00006A        REPLACE  NAME       01 NOBODY",
            "ADD    00001A",
            "ADD    00006A",
            "ADD    99998C",
            "DELETE 00001A",
            "DELETE 99999C",
            "DELETE 00006A",
            "update 00001A        REPLACE  NAME       01 LOWER CASE",
            "REMOVE 00001A",
            "*      00001A        REPLACE  NAME       01 COMMENTED OUT");

    /** Batches the SME adds: the decisions D-4 and D-7, contacts, and ordering. */
    static final Map<String, List<String>> BATCHES = new TreeMap<>(Map.of(
            "comment before a lower key (D-4)", List.of("* a comment whose text sorts high", "UPDATE 00001A        REPLACE  NAME       01 AFTER COMMENT",
                    "UPDATE 00002A        ADD      BALANCE    01 +000000100"),
            "added then changed in the same batch (D-7)", List.of("ADD    00004A", "UPDATE 00004A        REPLACE  NAME       01 NEWCO",
                    "UPDATE 00004A        ADD      BALANCE    01 +000010000", "DELETE 00005A", "ADD    00005A"),
            "contact added and named (D-6)", List.of("ADD    00001B", "UPDATE 00001B        REPLACE  NAME       01 FIRST CONTACT",
                    "UPDATE 00001B        ADD      BALANCE    01 +000000100"),
            "updates accumulate", List.of("UPDATE 00001A        ADD      BALANCE    01 +000001000", "UPDATE 00001A        ADD      BALANCE    01 +000001000",
                    "UPDATE 00001A        ADD      ORDERS     01 +000000150", "UPDATE 00001A        ADD      ORDERS     01 +000000150",
                    "UPDATE 00001A        REPLACE  NAME       01 TWICE"),
            "same key twice and a key going back", List.of("UPDATE 00002A        REPLACE  NAME       01 SECOND", "UPDATE 00002A        REPLACE  NAME       01 SECOND AGAIN",
                    "UPDATE 00001A        REPLACE  NAME       01 BACKWARDS", "UPDATE 00002A        REPLACE  NAME       01 FORWARD AGAIN"),
            "delete then add the same key", List.of("DELETE 00002A", "ADD    00002A", "UPDATE 00002A        REPLACE  NAME       01 REBORN")));

    @Test
    void export() throws IOException {
        String out = Jw.outDir() + "/customers";
        List<Object[]> runs = new ArrayList<>();
        for (var a : Sam1EquivalenceTest.scenarios().toList()) {
            String name = (String) a.get()[0];
            if (name.startsWith("CUSTFILE: records in reverse") || name.startsWith("CUSTFILE: every record twice")) continue;
            runs.add(new Object[] {name.trim(), a.get()[1]});
        }
        Map<String, byte[]> all = Sam1EquivalenceTest.inputs("all-transactions");
        List<String> lines = new ArrayList<>();
        for (byte[] r : StepEquivalence.records(all.get(TRAN), 80)) lines.add(new String(r, StandardCharsets.ISO_8859_1));
        for (int i = 0; i < lines.size(); i++)
            runs.add(new Object[] {"all-transactions / change " + (i + 1) + " alone", withChanges(all, List.of(lines.get(i)))});
        for (int i = 0; i < AUTHORED.size(); i++)
            runs.add(new Object[] {"authored change " + (i + 1) + ": " + AUTHORED.get(i).trim(), withChanges(all, List.of(AUTHORED.get(i)))});
        for (var b : BATCHES.entrySet()) runs.add(new Object[] {"authored batch: " + b.getKey(), withChanges(all, b.getValue())});
        int n = 0;
        for (Object[] r : runs) {
            @SuppressWarnings("unchecked") Map<String, byte[]> in = (Map<String, byte[]>) r[1];
            Jw.save(out, String.format("sam-%03d.json", ++n), draft((String) r[0], in));
        }
    }

    static Map<String, byte[]> withChanges(Map<String, byte[]> in, List<String> changes) {
        Map<String, byte[]> m = new TreeMap<>(in);
        ByteArrayOutputStream b = new ByteArrayOutputStream();
        for (String c : changes) b.writeBytes(String.format("%-80s", c).substring(0, 80).getBytes(StandardCharsets.ISO_8859_1));
        m.put(TRAN, b.toByteArray());
        return m;
    }

    static Map<String, Object> draft(String name, Map<String, byte[]> in) throws IOException {
        List<Object> changes = new ArrayList<>();
        if (in.get(TRAN) != null)
            for (byte[] r : StepEquivalence.records(in.get(TRAN), 80)) {
                String l = new String(r, StandardCharsets.ISO_8859_1);
                changes.add(obj("code", text(l.substring(0, 6)), "customerKey", text(l.substring(7, 13)), "action", text(l.substring(21, 29)),
                        "fieldName", text(l.substring(30, 40)), "value", text(l.substring(44, 80))));
            }
        StepEquivalence.Outcome o = StepEquivalence.run(Sam1EquivalenceTest.step(), in, "legacy");
        byte[] report = o.files().get("data/IBMUSER.SAMPLE.CUSTRPT");
        Map<String, Object> legacy = obj("returnCode", o.returnCode(), "detail", o.detail(),
                "customers", master(o.files().get("data/IBMUSER.SAMPLE.CUSTOUT")), "report", parse(report),
                "sysout", new String(o.files().getOrDefault("sysout/SYSOUT", new byte[0]), StandardCharsets.ISO_8859_1));
        return obj("name", name, "context", "customers", "program", "SAM1", "at", AT, "initial", obj("customers", master(in.get(CUST))),
                "command", "applyCustomerChanges", "input", obj("key", "changes-1", "changes", changes), "legacy", legacy);
    }

    /** CUSTFILE / CUSTOUT records as profiles (type A) and contacts. */
    static List<Object> master(byte[] file) {
        List<Object> out = new ArrayList<>();
        if (file == null) return out;
        for (byte[] r : StepEquivalence.records(file, 0)) {
            String s = String.format("%-400s", new String(r, StandardCharsets.ISO_8859_1));
            String id = s.substring(0, 5), type = s.substring(5, 6);
            if (type.equals("A")) {
                List<Object> months = new ArrayList<>();
                for (int i = 0; i < 12; i++) months.add(number(s.substring(84 + 9 * i, 93 + 9 * i), 2));
                out.add(obj("@type", "CustomerProfile", "customerId", id, "name", text(s.substring(6, 23)), "accountBalance", number(s.substring(23, 32), 2),
                        "ordersYtd", number(s.substring(32, 37), 0).intValueExact(), "address", text(s.substring(37, 57)), "city", text(s.substring(57, 71)),
                        "state", text(s.substring(71, 73)), "country", text(s.substring(73, 84)), "monthlyAmounts", months,
                        "occupation", text(s.substring(192, 222)), "notes", text(s.substring(222, 342)), "data1", text(s.substring(342, 347)),
                        "data2", text(s.substring(347, 387))));
            } else {
                out.add(obj("@type", "CustomerContact", "customerId", id, "recordType", type, "name", text(s.substring(6, 23)),
                        "description", text(s.substring(23, 33)), "contactInfo", text(s.substring(33, 53)), "data3", text(s.substring(53, 58)),
                        "data4", text(s.substring(58, 63)), "data5", text(s.substring(63, 68)), "data6", text(s.substring(68, 69))));
            }
        }
        return out;
    }

    /** An unsigned DISPLAY number; a field that is not all digits is reported as text in the export (the SME reviews it). */
    static BigDecimal number(String digits, int scale) {
        if (!digits.matches("\\d+")) return BigDecimal.valueOf(-1);
        return new BigDecimal(digits).movePointLeft(scale);
    }

    /** CUSTRPT: the errors (message, data, the change's text) in order, and the totals per change type. */
    static Map<String, Object> parse(byte[] report) {
        List<Object> errors = new ArrayList<>(), totals = new ArrayList<>(), processed = new ArrayList<>();
        if (report != null) {
            List<String> recs = new ArrayList<>();
            for (String piece : new String(report, StandardCharsets.ISO_8859_1).replace("\r", "").split("[\n\f]"))
                for (int i = 0; i < piece.length(); i += 132) recs.add(piece.substring(i, Math.min(piece.length(), i + 132)));
            for (int i = 0; i < recs.size(); i++) {
                String l = String.format("%-132s", recs.get(i));
                if (l.startsWith("Error Processing Transaction.")) {
                    String tran = i + 3 < recs.size() ? String.format("%-132s", recs.get(i + 3)).substring(21, 101) : "";
                    errors.add(obj("message", l.substring(31, 66).trim(), "data", l.substring(66, 132).trim(), "change", tran.stripTrailing()));
                } else if (l.matches("^(ADD|DELETE|UPDATE)\\s+[\\d,]+\\s+[\\d,]+\\s+[\\d,]+\\s*$")) {
                    String[] f = l.trim().split("\\s+");
                    totals.add(obj("changeType", f[0], "requested", Integer.parseInt(f[1].replace(",", "")),
                            "processed", Integer.parseInt(f[2].replace(",", "")), "inError", Integer.parseInt(f[3].replace(",", ""))));
                } else if (l.startsWith("       Transaction processed: ")) processed.add(l.substring(31, 111).stripTrailing());
            }
        }
        return obj("errors", errors, "totals", totals, "processed", processed);
    }
}
