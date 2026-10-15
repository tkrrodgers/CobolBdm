package com.mlcpu.forward;

import java.io.File;
import java.io.IOException;
import java.lang.reflect.Method;
import java.lang.reflect.Modifier;
import java.lang.reflect.Parameter;
import java.lang.reflect.ParameterizedType;
import java.lang.reflect.RecordComponent;
import java.lang.reflect.Type;
import java.net.URL;
import java.net.URLClassLoader;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Comparator;
import java.util.List;
import java.util.TreeSet;
import java.util.jar.JarFile;
import java.util.stream.Stream;

/**
 * Reads a compiled fixed API into {@code mlcpu.api-model.v1} JSON: every public type under a root package, by kind.
 * <ul>
 * <li>record: its components in declaration order, with their generic types;</li>
 * <li>enum: its constants in declaration order;</li>
 * <li>interface: sealed ones with their permitted subtypes, the others with their abstract methods (parameter names need
 * {@code -parameters});</li>
 * <li>class: public classes with their public static methods (rare in a fixed API).</li>
 * </ul>
 * Types are sorted by fully qualified name and nothing time- or machine-dependent is written, so the same API gives the same
 * bytes. Usage: ApiImporter &lt;root package&gt; &lt;out.json&gt; &lt;classpath entry&gt;...
 */
public final class ApiImporter {

    public static void main(String[] args) throws Exception {
        if (args.length < 3) {
            System.err.println("usage: ApiImporter <root package> <out.json> <classpath entry>...");
            System.exit(2);
        }
        String root = args[0];
        Path out = Path.of(args[1]);
        List<Path> entries = Arrays.stream(args, 2, args.length).map(Path::of).toList();
        URL[] urls = new URL[entries.size()];
        for (int i = 0; i < urls.length; i++) urls[i] = entries.get(i).toUri().toURL();
        TreeSet<String> names = new TreeSet<>();
        for (Path e : entries) names.addAll(classNames(e, root));
        StringBuilder sb = new StringBuilder();
        try (URLClassLoader loader = new URLClassLoader(urls, ApiImporter.class.getClassLoader())) {
            List<Class<?>> types = new ArrayList<>();
            for (String n : names) {
                Class<?> c = Class.forName(n, false, loader);
                if (Modifier.isPublic(c.getModifiers()) && !c.isAnonymousClass() && !c.isSynthetic()) types.add(c);
            }
            types.sort(Comparator.comparing(Class::getName));
            sb.append("{\n  \"schema\": \"mlcpu.api-model.v1\",\n  \"package\": ").append(str(root)).append(",\n  \"types\": [");
            for (int i = 0; i < types.size(); i++) {
                sb.append(i == 0 ? "\n" : ",\n");
                type(sb, types.get(i), root);
            }
            sb.append("\n  ]\n}\n");
        }
        Files.createDirectories(out.toAbsolutePath().getParent());
        Files.writeString(out, sb.toString(), StandardCharsets.UTF_8);
        System.out.println("api model: " + names.size() + " classes under " + root + " -> " + out);
    }

    /** The binary names of the classes under the package, from a classes directory or a jar. */
    static List<String> classNames(Path entry, String root) throws IOException {
        String prefix = root.replace('.', '/') + "/";
        List<String> names = new ArrayList<>();
        if (Files.isDirectory(entry)) {
            Path base = entry.resolve(prefix);
            if (!Files.isDirectory(base)) return names;
            try (Stream<Path> files = Files.walk(base)) {
                files.filter(f -> f.toString().endsWith(".class")).forEach(f -> {
                    String rel = entry.relativize(f).toString().replace(File.separatorChar, '/');
                    names.add(rel.substring(0, rel.length() - ".class".length()).replace('/', '.'));
                });
            }
        } else {
            try (JarFile jar = new JarFile(entry.toFile())) {
                jar.stream().map(e -> e.getName()).filter(n -> n.startsWith(prefix) && n.endsWith(".class"))
                        .forEach(n -> names.add(n.substring(0, n.length() - ".class".length()).replace('/', '.')));
            }
        }
        names.removeIf(n -> n.endsWith("module-info") || n.endsWith("package-info"));
        return names;
    }

    static void type(StringBuilder sb, Class<?> c, String root) {
        String sub = c.getPackageName().length() > root.length() ? c.getPackageName().substring(root.length() + 1) : "";
        sb.append("    {\"name\": ").append(str(simpleName(c))).append(", \"fqn\": ").append(str(c.getName().replace('$', '.')))
                .append(", \"subpackage\": ").append(str(sub)).append(", \"kind\": ");
        if (c.isRecord()) {
            sb.append("\"record\", \"implements\": ").append(interfaces(c)).append(", \"components\": [");
            RecordComponent[] rc = c.getRecordComponents();
            for (int i = 0; i < rc.length; i++) {
                sb.append(i == 0 ? "" : ", ").append("{\"name\": ").append(str(rc[i].getName())).append(", \"type\": ");
                typeRef(sb, rc[i].getGenericType());
                sb.append('}');
            }
            sb.append("]}");
        } else if (c.isEnum()) {
            sb.append("\"enum\", \"constants\": [");
            Object[] cs = c.getEnumConstants();
            for (int i = 0; i < cs.length; i++) sb.append(i == 0 ? "" : ", ").append(str(((Enum<?>) cs[i]).name()));
            sb.append("]}");
        } else if (c.isInterface() && c.isSealed()) {
            sb.append("\"sealed\", \"permits\": [");
            Class<?>[] p = c.getPermittedSubclasses();
            Arrays.sort(p, Comparator.comparing(Class::getName));
            for (int i = 0; i < p.length; i++) sb.append(i == 0 ? "" : ", ").append(str(simpleName(p[i])));
            sb.append("]}");
        } else if (c.isInterface()) {
            sb.append("\"interface\", \"methods\": ");
            methods(sb, Arrays.stream(c.getMethods()).filter(m -> Modifier.isAbstract(m.getModifiers()) && m.getDeclaringClass() == c).toList());
            sb.append('}');
        } else {
            sb.append("\"class\", \"methods\": ");
            methods(sb, Arrays.stream(c.getMethods()).filter(m -> Modifier.isStatic(m.getModifiers()) && m.getDeclaringClass() == c).toList());
            sb.append('}');
        }
    }

    static void methods(StringBuilder sb, List<Method> methods) {
        List<Method> ms = new ArrayList<>(methods);
        ms.sort(Comparator.comparing(Method::getName).thenComparing(m -> m.getParameterCount()));
        sb.append('[');
        for (int i = 0; i < ms.size(); i++) {
            Method m = ms.get(i);
            sb.append(i == 0 ? "" : ", ").append("{\"name\": ").append(str(m.getName())).append(", \"returns\": ");
            typeRef(sb, m.getGenericReturnType());
            sb.append(", \"params\": [");
            Parameter[] ps = m.getParameters();
            for (int j = 0; j < ps.length; j++) {
                sb.append(j == 0 ? "" : ", ").append("{\"name\": ").append(str(ps[j].getName())).append(", \"type\": ");
                typeRef(sb, ps[j].getParameterizedType());
                sb.append('}');
            }
            sb.append("]}");
        }
        sb.append(']');
    }

    static String interfaces(Class<?> c) {
        StringBuilder sb = new StringBuilder("[");
        Class<?>[] is = c.getInterfaces();
        for (int i = 0; i < is.length; i++) sb.append(i == 0 ? "" : ", ").append(str(simpleName(is[i])));
        return sb.append(']').toString();
    }

    /** A type reference: {"fqn": ..., "simple": ..., "args": [...]} (args only for a parameterized type). */
    static void typeRef(StringBuilder sb, Type t) {
        if (t instanceof ParameterizedType p) {
            Class<?> raw = (Class<?>) p.getRawType();
            sb.append("{\"fqn\": ").append(str(raw.getName())).append(", \"simple\": ").append(str(simpleName(raw))).append(", \"args\": [");
            Type[] a = p.getActualTypeArguments();
            for (int i = 0; i < a.length; i++) {
                if (i > 0) sb.append(", ");
                typeRef(sb, a[i]);
            }
            sb.append("]}");
        } else if (t instanceof Class<?> c) {
            sb.append("{\"fqn\": ").append(str(c.getName().replace('$', '.'))).append(", \"simple\": ").append(str(simpleName(c)))
                    .append(c.isEnum() ? ", \"enum\": true" : "").append(c.isPrimitive() ? ", \"primitive\": true" : "").append('}');
        } else {
            sb.append("{\"fqn\": ").append(str(t.getTypeName())).append(", \"simple\": ").append(str(t.getTypeName())).append('}');
        }
    }

    static String simpleName(Class<?> c) {
        return c.getEnclosingClass() == null ? c.getSimpleName() : simpleName(c.getEnclosingClass()) + "." + c.getSimpleName();
    }

    static String str(String s) {
        StringBuilder sb = new StringBuilder("\"");
        for (char ch : s.toCharArray()) {
            switch (ch) {
                case '"' -> sb.append("\\\"");
                case '\\' -> sb.append("\\\\");
                case '\n' -> sb.append("\\n");
                default -> sb.append(ch);
            }
        }
        return sb.append('"').toString();
    }
}
