package com.mlcpu.rt;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.TreeMap;

/**
 * Field-level comparison of a fixed-record output against its reference (§7.10, mlcpu.record-comparison.v1), driven by
 * replay/ReplayPlan.json: differing bytes are placed into layout fields; a record that differs only inside the plan's
 * volatile fields counts as equivalent. Records are paired positionally.
 * <pre>RecordCompare --plan replay/ReplayPlan.json --step STEP15 --dd TRANFILE --expected a.dat --actual b.dat [--out comparison-TRANFILE.json]</pre>
 */
public final class RecordCompare {
    private RecordCompare() {}

    public static final String SCHEMA = "mlcpu.record-comparison.v1";

    record Field(String name, String layout, int off, int len, boolean vol, List<String> writers) {}

    public static void main(String[] args) throws IOException {
        Map<String, String> a = new LinkedHashMap<>();
        for (int k = 0; k + 1 < args.length; k += 2) a.put(args[k].replaceFirst("^--", ""), args[k + 1]);
        Map<String, Object> report = compare(Path.of(a.get("plan")), a.get("step"), a.get("dd"), Path.of(a.get("expected")), Path.of(a.get("actual")));
        String json = MiniJson.write(report);
        if (a.containsKey("out")) Files.writeString(Path.of(a.get("out")), json, StandardCharsets.UTF_8);
        @SuppressWarnings("unchecked") Map<String, Object> sum = (Map<String, Object>) report.get("summary");
        System.out.println(a.get("dd") + ": " + report.get("verdict") + " " + sum);
        System.exit("DIFFERENT".equals(report.get("verdict")) ? 1 : 0);
    }

    @SuppressWarnings("unchecked")
    public static Map<String, Object> compare(Path plan, String step, String dd, Path expected, Path actual) throws IOException {
        Map<String, Object> root = (Map<String, Object>) MiniJson.parse(Files.readString(plan, StandardCharsets.UTF_8));
        Map<String, Object> st = ((List<Map<String, Object>>) root.get("steps")).stream()
                .filter(s -> step.equalsIgnoreCase(String.valueOf(s.get("step")))).findFirst()
                .orElseThrow(() -> new IllegalArgumentException("step " + step + " not in plan"));
        Map<String, Object> out = ((List<Map<String, Object>>) st.get("outputs")).stream()
                .filter(o -> dd.equalsIgnoreCase(String.valueOf(o.get("dd")))).findFirst()
                .orElseThrow(() -> new IllegalArgumentException("DD " + dd + " is not an output of " + step));
        List<String> volatileNames = (List<String>) out.getOrDefault("volatileFields", List.of());
        Map<String, Object> writers = (Map<String, Object>) out.getOrDefault("writers", Map.of());
        List<Field> fields = new ArrayList<>();
        int lrecl = 0;
        for (Map<String, Object> l : (List<Map<String, Object>>) out.get("layouts")) {
            String rec = String.valueOf(l.get("record"));
            lrecl = Math.max(lrecl, ((Number) l.get("length")).intValue());
            for (Map<String, Object> f : (List<Map<String, Object>>) l.get("fields")) {
                String qn = rec + "." + f.get("name");
                fields.add(new Field(String.valueOf(f.get("name")), rec, ((Number) f.get("offset")).intValue(), ((Number) f.get("length")).intValue(),
                        volatileNames.contains(qn), (List<String>) writers.getOrDefault(qn, List.of())));
            }
        }
        for (Map<String, Object> f : (List<Map<String, Object>>) st.get("files"))
            if (dd.equalsIgnoreCase(String.valueOf(f.get("dd")))) lrecl = Math.max(lrecl, ((Number) f.get("lrecl")).intValue());
        if (lrecl <= 0) throw new IllegalArgumentException("no record length for " + dd);

        byte[] e = Files.readAllBytes(expected), x = Files.readAllBytes(actual);
        int ne = e.length / lrecl, nx = x.length / lrecl, paired = Math.min(ne, nx);
        int identical = 0, volatileOnly = 0, differ = 0, unplaced = 0;
        Map<String, Map<String, Object>> stats = new TreeMap<>();
        for (int r = 0; r < paired; r++) {
            int base = r * lrecl;
            boolean any = false, real = false;
            for (int p = 0; p < lrecl; p++) {
                if (e[base + p] == x[base + p]) continue;
                any = true;
                List<Field> covering = new ArrayList<>();
                for (Field f : fields) if (p >= f.off && p < f.off + f.len) covering.add(f);
                if (covering.isEmpty()) { unplaced++; real = true; continue; }
                boolean vol = covering.stream().allMatch(Field::vol);
                if (!vol) real = true;
                for (Field f : covering) {
                    if (p != Math.max(f.off, firstDiff(e, x, base, f))) continue;
                    Map<String, Object> s = stats.computeIfAbsent(f.layout + "." + f.name, k -> {
                        Map<String, Object> m = new LinkedHashMap<>();
                        m.put("field", f.name);
                        m.put("layout", f.layout);
                        m.put("records", 0L);
                        m.put("volatile", f.vol);
                        m.put("assignedBy", f.writers);
                        m.put("examples", new ArrayList<Object>());
                        return m;
                    });
                    s.put("records", (Long) s.get("records") + 1);
                    List<Object> ex = (List<Object>) s.get("examples");
                    if (ex.size() < 3) {
                        Map<String, Object> m = new LinkedHashMap<>();
                        m.put("record", (long) r + 1);
                        m.put("expected", text(e, base + f.off, f.len));
                        m.put("actual", text(x, base + f.off, f.len));
                        ex.add(m);
                    }
                }
            }
            if (!any) identical++;
            else if (real) differ++;
            else volatileOnly++;
        }
        Map<String, Object> summary = new LinkedHashMap<>();
        summary.put("pairedRecords", (long) paired);
        summary.put("identical", (long) identical);
        summary.put("differOnlyInVolatileFields", (long) volatileOnly);
        summary.put("differ", (long) differ);
        summary.put("missingInActual", (long) Math.max(0, ne - nx));
        summary.put("extraInActual", (long) Math.max(0, nx - ne));
        String verdict = differ > 0 || ne != nx || e.length % lrecl != 0 || x.length % lrecl != 0 ? "DIFFERENT"
                : volatileOnly > 0 ? "EQUIVALENT_EXCEPT_VOLATILE" : "IDENTICAL";
        Map<String, Object> report = new LinkedHashMap<>();
        report.put("schema", SCHEMA);
        report.put("step", step);
        report.put("dd", dd);
        report.put("verdict", verdict);
        Map<String, Object> notes = new LinkedHashMap<>();
        notes.put("alignment", "positional, " + lrecl + "-byte records");
        notes.put("volatileFieldsBasis", out.get("volatileFieldsBasis"));
        report.put("notes", notes);
        report.put("summary", summary);
        report.put("fields", new ArrayList<>(stats.values()));
        report.put("unplacedDifferences", (long) unplaced);
        return report;
    }

    private static int firstDiff(byte[] e, byte[] x, int base, Field f) {
        for (int p = f.off; p < f.off + f.len; p++) if (e[base + p] != x[base + p]) return p;
        return f.off;
    }

    private static String text(byte[] b, int off, int len) {
        return new String(b, off, Math.min(len, b.length - off), StandardCharsets.ISO_8859_1);
    }
}
