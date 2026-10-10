package com.mlcpu.sidecar;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.OutputStream;
import java.net.URI;
import java.util.ArrayList;
import java.util.HashMap;
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
import javax.tools.ToolProvider;

/** In-memory javac (JDK Compiler API) against the process classpath. */
public final class JavaCompilerService {

    public static final class Result {
        public boolean compiled;
        public List<String> diagnostics = new ArrayList<>();
        public Map<String, byte[]> classes = new HashMap<>();
    }

    public Result compile(String fqcn, String source) {
        return compileAll(Map.of(fqcn, source));
    }

    public Result compileAll(Map<String, String> sources) {
        Result r = new Result();
        JavaCompiler javac = ToolProvider.getSystemJavaCompiler();
        if (javac == null) {
            r.diagnostics.add("no system Java compiler (a JDK is required, not a JRE)");
            return r;
        }
        DiagnosticCollector<JavaFileObject> diags = new DiagnosticCollector<>();
        StandardJavaFileManager std = javac.getStandardFileManager(diags, Locale.ROOT, null);
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
        List<String> options = List.of("-classpath", System.getProperty("java.class.path") + File.pathSeparator + ".", "-proc:none", "-nowarn", "-encoding", "UTF-8");
        Boolean ok = javac.getTask(null, fm, diags, options, null, units).call();
        for (Diagnostic<? extends JavaFileObject> d : diags.getDiagnostics()) {
            if (d.getKind() == Diagnostic.Kind.ERROR)
                r.diagnostics.add("L" + d.getLineNumber() + ": " + d.getMessage(Locale.ROOT));
        }
        r.compiled = Boolean.TRUE.equals(ok);
        for (Map.Entry<String, ByteArrayOutputStream> e : out.entrySet()) r.classes.put(e.getKey(), e.getValue().toByteArray());
        return r;
    }

    /** A class loader over compiled bytes, parented on the sidecar (so com.mlcpu.rt is shared). */
    public static final class MemoryLoader extends ClassLoader {
        private final Map<String, byte[]> classes;

        public MemoryLoader(Map<String, byte[]> classes, ClassLoader parent) {
            super(parent);
            this.classes = classes;
        }

        @Override
        protected Class<?> findClass(String name) throws ClassNotFoundException {
            byte[] b = classes.get(name);
            if (b == null) throw new ClassNotFoundException(name);
            return defineClass(name, b, 0, b.length);
        }
    }
}
