package com.mlcpu.sidecar;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonArray;
import com.google.gson.JsonObject;
import com.mlcpu.rt.FrontierHits;
import com.mlcpu.rt.ProgramState;
import com.sun.net.httpserver.HttpExchange;
import com.sun.net.httpserver.HttpServer;
import java.io.IOException;
import java.io.OutputStream;
import java.math.BigDecimal;
import java.net.BindException;
import java.net.InetSocketAddress;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;
import java.util.stream.Stream;

/** The Java sidecar (§6.2): binds 127.0.0.1 only; emits, compiles and runs Java from IR. Never parses COBOL (I3). */
public final class Sidecar {
    static final Gson GSON = new GsonBuilder().disableHtmlEscaping().serializeNulls().create();
    static final int BUILD_BUDGET_SECONDS = Integer.parseInt(firstNonNull(System.getProperty("mlcpu.buildBudgetSeconds"),
            System.getenv("MLCPU_SIDECAR_BUILD_BUDGET_SECONDS"), "60"));

    static String firstNonNull(String... v) {
        for (String s : v) if (s != null && !s.isBlank()) return s;
        return null;
    }

    public static void main(String[] args) throws IOException {
        int port = args.length > 0 ? Integer.parseInt(args[0]) : 8079;
        HttpServer server;
        try {
            server = HttpServer.create(new InetSocketAddress("127.0.0.1", port), 0);
        } catch (BindException e) {
            System.err.println("port in use: 127.0.0.1:" + port);
            System.exit(2);
            return;
        }
        server.createContext("/health", ex -> handle(ex, () -> health()));
        server.createContext("/runtime", ex -> handle(ex, () -> runtime()));
        server.createContext("/compile", ex -> handle(ex, () -> compile(read(ex))));
        server.createContext("/transpile-verify", ex -> handle(ex, () -> transpileVerify(read(ex))));
        server.createContext("/run-transpiled", ex -> handle(ex, () -> error(501, "run-transpiled: use the generated project's StepRunner")));
        server.createContext("/parity-run", ex -> handle(ex, () -> error(501, "parity-run: use tools/carddemo parity harness")));
        server.setExecutor(Executors.newFixedThreadPool(4));
        server.start();
        System.out.println("MlCpu Java sidecar listening on http://127.0.0.1:" + port + " (java " + System.getProperty("java.version") + ")");
    }

    interface Handler {
        Object handle() throws Exception;
    }

    record HttpError(int code, Object body) {}

    static HttpError error(int code, String message) {
        JsonObject o = new JsonObject();
        o.addProperty("error", message);
        return new HttpError(code, o);
    }

    static String read(HttpExchange ex) throws IOException {
        return new String(ex.getRequestBody().readAllBytes(), StandardCharsets.UTF_8);
    }

    static void handle(HttpExchange ex, Handler h) throws IOException {
        int code = 200;
        Object body;
        try {
            body = h.handle();
            if (body instanceof HttpError he) { code = he.code(); body = he.body(); }
        } catch (Exception e) {
            code = 500;
            JsonObject o = new JsonObject();
            o.addProperty("error", e.toString());
            body = o;
        }
        byte[] bytes = GSON.toJson(body).getBytes(StandardCharsets.UTF_8);
        ex.getResponseHeaders().add("Content-Type", "application/json; charset=utf-8");
        ex.sendResponseHeaders(code, bytes.length);
        try (OutputStream os = ex.getResponseBody()) {
            os.write(bytes);
        }
    }

    static Object health() {
        JsonObject o = new JsonObject();
        o.addProperty("status", "ok");
        o.addProperty("java", System.getProperty("java.version"));
        o.addProperty("compiler", javax.tools.ToolProvider.getSystemJavaCompiler() != null);
        o.addProperty("pid", ProcessHandle.current().pid());
        return o;
    }

    static Object runtime() throws IOException {
        Path dir = Path.of("src/main/java/com/mlcpu/rt");
        if (!Files.isDirectory(dir)) dir = Path.of("app/src/main/java/com/mlcpu/rt");
        Map<String, String> files = new java.util.TreeMap<>();
        try (Stream<Path> s = Files.list(dir)) {
            for (Path p : s.filter(p -> p.toString().endsWith(".java")).toList())
                files.put(p.getFileName().toString(), Files.readString(p));
        }
        return Map.of("files", files);
    }

    static Object compile(String body) {
        JsonObject req = GSON.fromJson(body, JsonObject.class);
        String cls = req.has("className") ? req.get("className").getAsString() : "com.mlcpu.gen.Probe";
        JavaCompilerService.Result r = new JavaCompilerService().compile(cls, req.get("source").getAsString());
        return Map.of("compiled", r.compiled, "diagnostics", r.diagnostics);
    }

    /** Rule sources of every emitted program; shared files (the SPI) must be byte-identical. */
    static List<Map<String, Object>> mergeAdditionalSources(List<SkeletonEmitter.Emission> emissions) {
        Map<String, Map<String, Object>> byPath = new LinkedHashMap<>();
        for (SkeletonEmitter.Emission e : emissions)
            for (Map<String, Object> s : e.additionalSources) {
                Map<String, Object> prior = byPath.putIfAbsent((String) s.get("path"), s);
                if (prior != null && !prior.get("content").equals(s.get("content")))
                    throw new IllegalStateException("two programs generate different " + s.get("path"));
            }
        return new ArrayList<>(byPath.values());
    }

    /** Publishes the rule contracts, sources and ledgers and verifies them; false when the evidence fails. */
    @SuppressWarnings("unchecked")
    static boolean rules(Map<String, Object> resp, Map<String, Object> checks, Protocol.VerifyRequest req, Object[] built, boolean compiled) {
        List<SkeletonEmitter.Emission> emissions = (List<SkeletonEmitter.Emission>) built[3];
        List<Map<String, Object>> additional = (List<Map<String, Object>>) built[4];
        List<Map<String, Object>> contracts = new ArrayList<>();
        List<Map<String, Object>> ledgers = new ArrayList<>();
        for (int i = 0; i < emissions.size(); i++) {
            SkeletonEmitter.Emission e = emissions.get(i);
            contracts.addAll(e.ruleContracts);
            JsonObject irJson = i == 0 ? req.ir : req.callees.get(i - 1);
            e.extractionLedger.put("irSha256", RuleSet.sha256(GSON.toJson(irJson)));
            ledgers.add(e.extractionLedger);
        }
        RuleVerifier.Result v = RuleVerifier.verify(additional, contracts);
        boolean ok = compiled && v.isolatedCompile && v.casesPass && v.forbiddenFree;
        if (ok)
            for (Map<String, Object> d : contracts) d.put("evidence", List.of("faithful-runtime-cases", "jdk-only-compile", "generated-passes-cases"));
        checks.put("ruleSourcesJdkOnly", v.isolatedCompile && v.forbiddenFree);
        checks.put("ruleCasesPass", v.casesPass);
        resp.put("ruleCasesRun", v.casesRun);
        resp.put("ruleDiagnostics", v.diagnostics);
        resp.put("additionalSources", additional);
        resp.put("ruleContracts", contracts);
        resp.put("extractionLedger", ledgers.get(0));
        resp.put("extractionLedgers", ledgers);
        return ok;
    }

    static Object transpileVerify(String body) throws Exception {
        Protocol.VerifyRequest req = GSON.fromJson(body, Protocol.VerifyRequest.class);
        if (req.ir == null) return error(400, "missing ir");
        if (req.extractRules && !req.composeStorage) return error(400, "extractRules requires composeStorage");
        Ir ir = GSON.fromJson(req.ir, Ir.class);
        List<Ir> callees = new ArrayList<>();
        if (req.callees != null) for (JsonObject c : req.callees) callees.add(GSON.fromJson(c, Ir.class));

        // Bounded build: emit + compile on a daemon thread under the budget.
        var exec = Executors.newSingleThreadExecutor(r -> { Thread t = new Thread(r, "build"); t.setDaemon(true); return t; });
        Future<Object[]> build = exec.submit(() -> {
            SkeletonEmitter.Emission main = new SkeletonEmitter().emit(ir, req.composeStorage, req.extractRules);
            String faithful = req.composeStorage ? new SkeletonEmitter().emit(ir).source : null;
            Map<String, String> sources = new LinkedHashMap<>();
            List<SkeletonEmitter.Emission> emissions = new ArrayList<>();
            emissions.add(main);
            sources.put(main.className, main.source);
            for (Ir c : callees) {
                SkeletonEmitter.Emission e = new SkeletonEmitter().emit(c, req.composeStorage, req.extractRules);
                sources.put(e.className, e.source);
                emissions.add(e);
            }
            List<Map<String, Object>> additional = mergeAdditionalSources(emissions);
            for (Map<String, Object> s : additional) sources.put((String) s.get("className"), (String) s.get("content"));
            return new Object[]{main, new JavaCompilerService().compileAll(sources), faithful, emissions, additional};
        });
        Object[] built;
        try {
            built = build.get(BUILD_BUDGET_SECONDS, TimeUnit.SECONDS);
        } catch (java.util.concurrent.TimeoutException e) {
            JsonObject o = new JsonObject();
            o.addProperty("programId", ir.program.id);
            o.addProperty("compiled", false);
            o.addProperty("verdict", "ERROR");
            o.addProperty("error", "build budget of " + BUILD_BUDGET_SECONDS + "s exceeded");
            o.add("diagnostics", new JsonArray());
            return o;
        } catch (java.util.concurrent.ExecutionException e) {
            JsonObject o = new JsonObject();
            o.addProperty("programId", ir.program == null ? null : ir.program.id);
            o.addProperty("compiled", false);
            o.addProperty("verdict", "FAIL");
            o.addProperty("error", e.getCause().toString());
            o.add("diagnostics", new JsonArray());
            return o;
        } finally {
            exec.shutdownNow();
        }
        SkeletonEmitter.Emission em = (SkeletonEmitter.Emission) built[0];
        JavaCompilerService.Result comp = (JavaCompilerService.Result) built[1];

        Map<String, Object> resp = new LinkedHashMap<>();
        resp.put("irVersion", ir.irVersion);
        resp.put("semanticSchema", ir.semanticSchema);
        resp.put("programId", ir.program.id);
        resp.put("className", em.className);
        resp.put("renderer", "semantic-tree");
        resp.put("storageComposition", req.composeStorage);
        resp.put("loweredStatements", em.loweredStatements);
        resp.put("abstainedStatements", em.abstainedStatements);
        resp.put("abstained", em.abstained);
        resp.put("frontierCount", em.frontierCount);
        resp.put("paragraphCount", em.paragraphCount);
        resp.put("fieldCount", em.fieldCount);
        resp.put("linkageTypedCalls", em.linkageTypedCalls);
        resp.put("linkageWholeStateCalls", em.linkageWholeStateCalls);
        resp.put("compiled", comp.compiled);
        resp.put("diagnostics", comp.diagnostics);

        boolean structureOk = false;
        Map<String, Object> checks = new LinkedHashMap<>();
        if (comp.compiled) {
            Class<?> c = Class.forName(em.className, false, new JavaCompilerService.MemoryLoader(comp.classes, Sidecar.class.getClassLoader()));
            TreeApiChecker.Structure st = new TreeApiChecker().scan(c, req.composeStorage);
            boolean methods = st.methods.size() >= em.paragraphCount;
            boolean fields = st.fields.size() >= em.fieldCount;
            boolean identity = ir.program.id.equals(st.cobolProgramId);
            checks.put("paragraphMethods", methods);
            checks.put("businessFields", fields);
            checks.put("programIdentity", identity);
            structureOk = methods && fields && identity;
        }
        if (req.extractRules) structureOk &= rules(resp, checks, req, built, comp.compiled);
        resp.put("checks", checks);

        String verdict;
        if (!comp.compiled || !structureOk) verdict = "FAIL";
        else if (req.transpileOnly) verdict = "TRANSPILED";
        else {
            List<Map<String, Object>> rows = new ArrayList<>();
            List<Protocol.BatteryRow> battery = req.battery == null || req.battery.isEmpty() ? List.of(new Protocol.BatteryRow()) : req.battery;
            boolean allPass = true;
            boolean ran = true;
            String runError = null;
            for (Protocol.BatteryRow row : battery) {
                Runner.Outcome out = new Runner().run(comp.classes, em.className, row.inputs);
                Map<String, Object> r = new LinkedHashMap<>();
                r.put("ran", out.ran);
                r.put("abend", out.abend);
                r.put("error", out.error);
                List<String> mismatches = new ArrayList<>();
                if (out.ran && out.state != null && row.expect != null) {
                    for (Map.Entry<String, Object> e : row.expect.entrySet())
                        if (!valueEquals(out.state.get(e.getKey()), e.getValue())) mismatches.add(e.getKey() + ": expected " + e.getValue() + " got " + out.state.get(e.getKey()));
                }
                r.put("mismatches", mismatches);
                r.put("display", out.state == null ? List.of() : out.state.get("@DISPLAY"));
                r.put("pass", out.ran && mismatches.isEmpty());
                rows.add(r);
                allPass &= out.ran && mismatches.isEmpty();
                if (!out.ran) { ran = false; runError = out.error; }
            }
            resp.put("battery", rows);
            resp.put("ran", ran);
            resp.put("runError", runError);
            List<String> cuts = FrontierHits.loopCuts();
            resp.put("loopsTruncated", !cuts.isEmpty());
            verdict = !allPass ? "FAIL" : (!cuts.isEmpty() || em.abstainedStatements > 0) ? "INCOMPLETE" : "PASS";
        }
        resp.put("verdict", verdict);
        boolean include = req.includeJavaSource == null || req.includeJavaSource;
        if (req.javaSourceDir != null) {
            Path dir = Path.of(req.javaSourceDir);
            Files.createDirectories(dir);
            Path p = dir.resolve(em.className.substring(em.className.lastIndexOf('.') + 1) + ".java");
            Files.writeString(p, em.source);
            resp.put("javaSourcePath", p.toString());
            resp.put("javaSourceBytes", Files.size(p));
        }
        if (include) {
            resp.put("javaSource", em.source);
            if (req.composeStorage) resp.put("faithfulJavaSource", built[2]);
        }
        return resp;
    }

    static boolean valueEquals(Object actual, Object expected) {
        if (actual == null || expected == null) return actual == expected;
        if (actual instanceof BigDecimal a) {
            try { return a.compareTo(new BigDecimal(expected.toString())) == 0; } catch (NumberFormatException e) { return false; }
        }
        return actual.toString().stripTrailing().equals(expected.toString().stripTrailing());
    }

    static ProgramState unused() { return null; }
}
