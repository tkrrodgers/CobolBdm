package com.mlcpu.sidecar;

import java.io.ByteArrayOutputStream;
import java.io.OutputStream;
import java.lang.reflect.Method;
import java.net.URI;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import javax.tools.Diagnostic;
import javax.tools.DiagnosticCollector;
import javax.tools.FileObject;
import javax.tools.ForwardingJavaFileManager;
import javax.tools.JavaCompiler;
import javax.tools.JavaFileManager;
import javax.tools.JavaFileObject;
import javax.tools.SimpleJavaFileObject;
import javax.tools.StandardJavaFileManager;
import javax.tools.StandardLocation;
import javax.tools.ToolProvider;

/**
 * Evidence for generated rule sources: they compile with an empty class path (the JDK only: no runtime, program or
 * storage type is reachable) and, loaded by a class loader that sees only the platform, every generated
 * implementation returns each faithful-runtime witness case's expected value.
 */
final class RuleVerifier {

    /** Text the integration seam must never contain (generated rule code depends on the JDK only). */
    static final List<String> FORBIDDEN = List.of("com.mlcpu.rt.", "com.mlcpu.core.", "com.mlcpu.gen.", "com.mlcpu.app.", "org.springframework.",
            "CobolBase", "State_", "Prog_", "java.io.File", "java.nio.file");

    static final class Result {
        boolean isolatedCompile;
        boolean casesPass;
        boolean forbiddenFree = true;
        int casesRun;
        List<String> diagnostics = new ArrayList<>();
        Map<String, byte[]> classes = new HashMap<>();
    }

    private RuleVerifier() {}

    static Result verify(List<Map<String, Object>> sources, List<Map<String, Object>> contracts) {
        Result r = new Result();
        Map<String, String> units = new LinkedHashMap<>();
        for (Map<String, Object> s : sources) {
            String content = (String) s.get("content");
            for (String f : FORBIDDEN)
                if (content.contains(f)) {
                    r.forbiddenFree = false;
                    r.diagnostics.add(s.get("path") + " contains " + f);
                }
            units.put((String) s.get("className"), content);
        }
        if (units.isEmpty()) {
            r.isolatedCompile = true;
            r.casesPass = true;
            return r;
        }
        compileIsolated(units, r);
        if (!r.isolatedCompile) return r;
        ClassLoader loader = new JavaCompilerService.MemoryLoader(r.classes, ClassLoader.getPlatformClassLoader());
        r.casesPass = true;
        for (Map<String, Object> d : contracts) {
            try {
                Class<?> input = Class.forName((String) d.get("input"), true, loader);
                Object impl = Class.forName((String) d.get("generatedImplementation"), true, loader).getConstructor().newInstance();
                Method evaluate = Class.forName((String) d.get("contract"), true, loader).getMethod("evaluate", input);
                List<?> cases = (List<?>) Class.forName((String) d.get("cases"), true, loader).getMethod("cases").invoke(null);
                for (Object c : cases) {
                    Object in = c.getClass().getMethod("input").invoke(c);
                    Object expected = c.getClass().getMethod("expected").invoke(c);
                    Object actual = evaluate.invoke(impl, in);
                    r.casesRun++;
                    if (!expected.equals(actual)) {
                        r.casesPass = false;
                        r.diagnostics.add(d.get("ruleId") + " case " + c.getClass().getMethod("id").invoke(c) + ": expected " + expected + " got " + actual);
                    }
                }
            } catch (ReflectiveOperationException | RuntimeException e) {
                r.casesPass = false;
                r.diagnostics.add(d.get("ruleId") + ": " + e);
            }
        }
        return r;
    }

    /** javac over the sources with an empty user class path: anything outside the JDK fails to resolve. */
    static void compileIsolated(Map<String, String> sources, Result r) {
        JavaCompiler javac = ToolProvider.getSystemJavaCompiler();
        if (javac == null) {
            r.diagnostics.add("no system Java compiler");
            return;
        }
        DiagnosticCollector<JavaFileObject> diags = new DiagnosticCollector<>();
        StandardJavaFileManager std = javac.getStandardFileManager(diags, Locale.ROOT, null);
        try {
            std.setLocation(StandardLocation.CLASS_PATH, List.of());
        } catch (java.io.IOException e) {
            r.diagnostics.add(e.toString());
            return;
        }
        Map<String, ByteArrayOutputStream> out = new HashMap<>();
        JavaFileManager fm = new ForwardingJavaFileManager<>(std) {
            @Override
            public JavaFileObject getJavaFileForOutput(Location location, String className, JavaFileObject.Kind kind, FileObject sibling) {
                return new SimpleJavaFileObject(URI.create("mem:///" + className.replace('.', '/') + kind.extension), kind) {
                    @Override
                    public OutputStream openOutputStream() {
                        ByteArrayOutputStream bos = new ByteArrayOutputStream();
                        out.put(className, bos);
                        return bos;
                    }
                };
            }
        };
        List<JavaFileObject> units = new ArrayList<>();
        for (Map.Entry<String, String> e : sources.entrySet()) {
            String src = e.getValue();
            units.add(new SimpleJavaFileObject(URI.create("string:///" + e.getKey().replace('.', '/') + ".java"), JavaFileObject.Kind.SOURCE) {
                @Override
                public CharSequence getCharContent(boolean ignoreEncodingErrors) { return src; }
            });
        }
        Boolean ok = javac.getTask(null, fm, diags, List.of("-proc:none", "-nowarn", "-encoding", "UTF-8", "-Xlint:none"), null, units).call();
        for (Diagnostic<? extends JavaFileObject> d : diags.getDiagnostics())
            if (d.getKind() == Diagnostic.Kind.ERROR) r.diagnostics.add(d.getSource() == null ? "" : d.getSource().getName() + " L" + d.getLineNumber() + ": " + d.getMessage(Locale.ROOT));
        r.isolatedCompile = Boolean.TRUE.equals(ok);
        for (Map.Entry<String, ByteArrayOutputStream> e : out.entrySet()) r.classes.put(e.getKey(), e.getValue().toByteArray());
    }
}
