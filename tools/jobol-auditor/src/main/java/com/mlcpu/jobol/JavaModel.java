package com.mlcpu.jobol;

import com.github.javaparser.ast.CompilationUnit;
import com.github.javaparser.ast.Modifier;
import com.github.javaparser.ast.Node;
import com.github.javaparser.ast.NodeList;
import com.github.javaparser.ast.body.BodyDeclaration;
import com.github.javaparser.ast.body.CallableDeclaration;
import com.github.javaparser.ast.body.ClassOrInterfaceDeclaration;
import com.github.javaparser.ast.body.CompactConstructorDeclaration;
import com.github.javaparser.ast.body.ConstructorDeclaration;
import com.github.javaparser.ast.body.EnumConstantDeclaration;
import com.github.javaparser.ast.body.EnumDeclaration;
import com.github.javaparser.ast.body.FieldDeclaration;
import com.github.javaparser.ast.body.MethodDeclaration;
import com.github.javaparser.ast.body.Parameter;
import com.github.javaparser.ast.body.RecordDeclaration;
import com.github.javaparser.ast.body.TypeDeclaration;
import com.github.javaparser.ast.body.VariableDeclarator;
import com.github.javaparser.ast.expr.AnnotationExpr;
import com.github.javaparser.ast.expr.BinaryExpr;
import com.github.javaparser.ast.expr.ConditionalExpr;
import com.github.javaparser.ast.expr.FieldAccessExpr;
import com.github.javaparser.ast.expr.InstanceOfExpr;
import com.github.javaparser.ast.expr.LambdaExpr;
import com.github.javaparser.ast.expr.MethodCallExpr;
import com.github.javaparser.ast.expr.MethodReferenceExpr;
import com.github.javaparser.ast.expr.NameExpr;
import com.github.javaparser.ast.expr.NullLiteralExpr;
import com.github.javaparser.ast.expr.ObjectCreationExpr;
import com.github.javaparser.ast.expr.SwitchExpr;
import com.github.javaparser.ast.expr.TextBlockLiteralExpr;
import com.github.javaparser.ast.expr.VariableDeclarationExpr;
import com.github.javaparser.ast.nodeTypes.NodeWithModifiers;
import com.github.javaparser.ast.stmt.CatchClause;
import com.github.javaparser.ast.stmt.DoStmt;
import com.github.javaparser.ast.stmt.ForEachStmt;
import com.github.javaparser.ast.stmt.ForStmt;
import com.github.javaparser.ast.stmt.IfStmt;
import com.github.javaparser.ast.stmt.ReturnStmt;
import com.github.javaparser.ast.stmt.SwitchEntry;
import com.github.javaparser.ast.stmt.WhileStmt;
import com.github.javaparser.ast.type.ClassOrInterfaceType;
import com.github.javaparser.ast.type.Type;
import com.github.javaparser.ast.type.VarType;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.TreeMap;
import java.util.regex.Pattern;

/**
 * A structural model of the types of a Java source tree, for comparing two trees: declarations, members, the
 * idioms used and size measures. Record and Lombok members the compiler would generate are added as members with
 * origin "record" or "lombok" (an approximation of the delomboked view: getters, setters, constructors, builder(),
 * equals/hashCode/toString).
 */
final class JavaModel {
    private JavaModel() {
    }

    static final class Field {
        String name;
        String type;
        String visibility;
        boolean isFinal;
        boolean isStatic;
        String origin = "source";
    }

    static final class Param {
        String name;
        String type;

        Param(String name, String type) {
            this.name = name;
            this.type = type;
        }
    }

    static final class Local {
        String name;
        String type;

        Local(String name, String type) {
            this.name = name;
            this.type = type;
        }
    }

    static final class Method {
        String name;
        boolean ctor;
        List<Param> params = new ArrayList<>();
        /** null for a constructor */
        String returnType;
        String visibility;
        boolean isStatic;
        boolean isAbstract;
        String origin = "source";
        int lines;
        int branches;
        List<Local> locals = new ArrayList<>();
        /** the body is only a JX-DEAD throw: J3S compiled a frame but no live code (the generator emits a stub) */
        boolean deadStub;
        /** a generic method's own type parameters */
        List<String> typeParameters = new ArrayList<>();
        /** idioms and watched type uses inside this method's body */
        Map<String, Integer> idioms = new TreeMap<>();
        Map<String, Integer> typeUses = new TreeMap<>();

        String paramTypes() {
            return String.join(",", params.stream().map(p -> p.type).toList());
        }

        String erasedParamTypes() {
            return String.join(",", params.stream().map(p -> erase(p.type).replace("...", "[]")).toList());
        }

        String signature() {
            return (ctor ? "" : returnType + " ") + name + "(" + paramTypes() + ")";
        }

        String key() {
            return name + "(" + erasedParamTypes() + ")";
        }
    }

    static final class TypeModel {
        String fqn;
        String name;
        String pkg;
        /** the fqn of the enclosing type, or null */
        String outer;
        String file;
        String kind;
        String visibility;
        boolean isFinal;
        boolean isStatic;
        boolean isSealed;
        boolean effectivelyPublic;
        List<String> extendsTypes = new ArrayList<>();
        List<String> implementsTypes = new ArrayList<>();
        List<Field> fields = new ArrayList<>();
        List<Method> methods = new ArrayList<>();
        List<String> enumConstants = new ArrayList<>();
        List<String> lombok = new ArrayList<>();
        /** the type's own type parameters (T, K extends Comparable...) */
        List<String> typeParameters = new ArrayList<>();
        boolean builder;
        Map<String, Integer> idioms = new TreeMap<>();
        Map<String, Integer> typeUses = new TreeMap<>();
        int loc;
        int branches;

        List<String> supertypes() {
            List<String> all = new ArrayList<>(extendsTypes);
            all.addAll(implementsTypes);
            return all;
        }

        List<Method> sourceMethods() {
            return methods.stream().filter(m -> m.origin.equals("source")).toList();
        }
    }

    static final Map<String, String> WATCHED = new HashMap<>();

    static {
        watch("optional", "Optional", "OptionalInt", "OptionalLong", "OptionalDouble");
        watch("stream", "Stream", "IntStream", "LongStream", "DoubleStream", "Collectors", "StreamSupport");
        watch("list", "List", "ArrayList", "LinkedList", "CopyOnWriteArrayList");
        watch("map", "Map", "HashMap", "TreeMap", "LinkedHashMap", "SortedMap", "NavigableMap", "EnumMap", "ConcurrentHashMap",
            "ConcurrentMap", "IdentityHashMap");
        watch("set", "Set", "HashSet", "TreeSet", "LinkedHashSet", "SortedSet", "NavigableSet", "EnumSet");
        watch("collection", "Collection", "Iterable", "Iterator", "Deque", "ArrayDeque", "Queue", "PriorityQueue", "Collections");
        watch("bigdecimal", "BigDecimal", "MathContext", "RoundingMode");
        watch("biginteger", "BigInteger");
        watch("time", "LocalDate", "LocalDateTime", "LocalTime", "Instant", "Duration", "Period", "ChronoUnit", "ChronoField",
            "YearMonth", "Year", "ZonedDateTime", "OffsetDateTime", "DayOfWeek", "Month", "ZoneId", "ZoneOffset", "TemporalAdjusters",
            "DateTimeFormatter", "Clock", "TemporalUnit", "Temporal");
        watch("functional", "Function", "BiFunction", "Supplier", "Consumer", "BiConsumer", "Predicate", "BiPredicate",
            "UnaryOperator", "BinaryOperator", "Comparator", "IntFunction", "ToIntFunction", "ToLongFunction", "IntPredicate");
    }

    private static void watch(String family, String... names) {
        for (String n : names) WATCHED.put(n, family);
    }

    private static final Pattern QUALIFIER = Pattern.compile("(?<![A-Za-z0-9_$])(?:[a-z_$][A-Za-z0-9_$]*\\.)+(?=[A-Za-z_$])");
    private static final Pattern ANNOTATION = Pattern.compile("@[A-Za-z0-9_.]+(?:\\([^)]*\\))?\\s*");
    /** Outer.Inner and Map.Entry keep only the last segment, so a nested type reads the same however it is qualified. */
    private static final Pattern OUTER_QUALIFIER = Pattern.compile("(?<![A-Za-z0-9_$])(?:[A-Z][A-Za-z0-9_$]*\\.)+(?=[A-Z])");
    private static final Pattern INNER_ARGS = Pattern.compile("<[^<>]*>");

    /** A type as written, without package qualifiers, annotations or spaces after commas: Map<LocalDate,Money>. */
    static String norm(String typeText) {
        String s = ANNOTATION.matcher(typeText).replaceAll("");
        s = QUALIFIER.matcher(s).replaceAll("");
        s = OUTER_QUALIFIER.matcher(s).replaceAll("");
        return s.replace(", ", ",").replace(" ,", ",").trim();
    }

    static String norm(Type t) {
        return norm(t.asString());
    }

    /** The type without its type arguments: Map<LocalDate,Money> is Map. */
    static String erase(String t) {
        String s = t;
        while (s.indexOf('<') >= 0) {
            String next = INNER_ARGS.matcher(s).replaceAll("");
            if (next.equals(s)) break;
            s = next;
        }
        return s;
    }

    /** Extracts every member type (nested included, local and anonymous classes excluded) of a compilation unit. */
    static List<TypeModel> extract(CompilationUnit cu, String file, String source) {
        boolean[] code = codeLines(source);
        String pkg = cu.getPackageDeclaration().map(p -> p.getNameAsString()).orElse("");
        boolean lombokImported = cu.getImports().stream().anyMatch(i -> i.getNameAsString().startsWith("lombok"));
        List<TypeModel> out = new ArrayList<>();
        for (TypeDeclaration<?> t : cu.getTypes()) collect(t, null, pkg, file, code, lombokImported, out);
        return out;
    }

    private static void collect(TypeDeclaration<?> t, TypeModel outer, String pkg, String file, boolean[] code, boolean lombok,
        List<TypeModel> out) {
        TypeModel m = model(t, outer, pkg, file, code, lombok);
        out.add(m);
        for (BodyDeclaration<?> b : t.getMembers()) {
            if (b instanceof TypeDeclaration<?> n) {
                collect(n, m, pkg, file, code, lombok, out);
                if (n.getNameAsString().endsWith("Builder")) m.builder = true;
            }
        }
    }

    private static TypeModel model(TypeDeclaration<?> t, TypeModel outer, String pkg, String file, boolean[] code, boolean lombok) {
        TypeModel m = new TypeModel();
        m.name = t.getNameAsString();
        m.pkg = pkg;
        m.outer = outer == null ? null : outer.fqn;
        m.fqn = outer != null ? outer.fqn + "." + m.name : pkg.isEmpty() ? m.name : pkg + "." + m.name;
        m.file = file;
        boolean inInterface = outer != null && (outer.kind.equals("interface") || outer.kind.equals("annotation"));
        boolean isInterface = t instanceof ClassOrInterfaceDeclaration c && c.isInterface();
        if (t instanceof RecordDeclaration) m.kind = "record";
        else if (t instanceof EnumDeclaration) m.kind = "enum";
        else if (isInterface) m.kind = "interface";
        else if (t.isAnnotationDeclaration()) m.kind = "annotation";
        else if (t.hasModifier(Modifier.Keyword.ABSTRACT)) m.kind = "abstract";
        else m.kind = "class";
        m.visibility = inInterface ? "public" : visibility(t);
        m.isFinal = t.hasModifier(Modifier.Keyword.FINAL) || m.kind.equals("record") || m.kind.equals("enum");
        m.isStatic = outer != null && (t.hasModifier(Modifier.Keyword.STATIC) || !m.kind.equals("class") && !m.kind.equals("abstract")
            || inInterface);
        m.isSealed = t.hasModifier(Modifier.Keyword.SEALED);
        boolean visible = m.visibility.equals("public") || m.visibility.equals("protected");
        m.effectivelyPublic = visible && (outer == null || outer.effectivelyPublic);
        if (t instanceof com.github.javaparser.ast.nodeTypes.NodeWithTypeParameters<?> g) {
            for (var tp : g.getTypeParameters()) m.typeParameters.add(tp.getNameAsString());
        }
        if (t instanceof ClassOrInterfaceDeclaration c) {
            for (ClassOrInterfaceType e : c.getExtendedTypes()) m.extendsTypes.add(norm(e));
            for (ClassOrInterfaceType e : c.getImplementedTypes()) m.implementsTypes.add(norm(e));
        } else if (t instanceof EnumDeclaration e) {
            for (ClassOrInterfaceType i : e.getImplementedTypes()) m.implementsTypes.add(norm(i));
            for (EnumConstantDeclaration c : e.getEntries()) m.enumConstants.add(c.getNameAsString());
        } else if (t instanceof RecordDeclaration r) {
            for (ClassOrInterfaceType i : r.getImplementedTypes()) m.implementsTypes.add(norm(i));
        }

        Map<Node, Method> byNode = new java.util.IdentityHashMap<>();
        for (BodyDeclaration<?> b : t.getMembers()) {
            if (b instanceof FieldDeclaration f) {
                for (VariableDeclarator v : f.getVariables()) {
                    Field x = new Field();
                    x.name = v.getNameAsString();
                    x.type = norm(v.getType());
                    x.visibility = isInterface ? "public" : visibility(f);
                    x.isFinal = f.isFinal() || isInterface;
                    x.isStatic = f.isStatic() || isInterface;
                    m.fields.add(x);
                }
            } else if (b instanceof MethodDeclaration md) {
                Method x = callable(md, t, m);
                x.name = md.getNameAsString();
                x.returnType = norm(md.getType());
                x.isStatic = md.isStatic();
                x.isAbstract = md.isAbstract() || isInterface && md.getBody().isEmpty() && !md.isStatic();
                x.visibility = isInterface ? (md.isPrivate() ? "private" : "public") : visibility(md);
                m.methods.add(x);
                byNode.put(md, x);
            } else if (b instanceof ConstructorDeclaration cd) {
                Method x = callable(cd, t, m);
                x.name = "<init>";
                x.ctor = true;
                x.visibility = m.kind.equals("enum") ? "private" : visibility(cd);
                m.methods.add(x);
                byNode.put(cd, x);
            } else if (b instanceof CompactConstructorDeclaration cc) {
                Method x = new Method();
                x.name = "<init>";
                x.ctor = true;
                x.visibility = visibility(cc);
                for (Parameter p : ((RecordDeclaration) t).getParameters()) x.params.add(new Param(p.getNameAsString(), paramType(p)));
                x.lines = lines(cc);
                x.branches = branches(cc, t);
                x.locals = locals(cc, t);
                m.methods.add(x);
                byNode.put(cc, x);
            }
        }
        if (t instanceof RecordDeclaration r) recordMembers(r, m);
        if (lombok) lombokMembers(t, m);
        if (m.methods.stream().anyMatch(x -> x.name.equals("builder") && x.isStatic && x.params.isEmpty())) m.builder = true;
        measure(t, m, code, byNode);
        return m;
    }

    private static Method callable(CallableDeclaration<?> c, TypeDeclaration<?> owner, TypeModel m) {
        Method x = new Method();
        for (var tp : c.getTypeParameters()) x.typeParameters.add(tp.getNameAsString());
        List<com.github.javaparser.ast.stmt.Statement> body = c instanceof MethodDeclaration md
            ? md.getBody().map(bs -> (List<com.github.javaparser.ast.stmt.Statement>) bs.getStatements()).orElse(List.of())
            : c instanceof ConstructorDeclaration cd ? cd.getBody().getStatements() : List.of();
        x.deadStub = body.size() == 1 && body.get(0) instanceof com.github.javaparser.ast.stmt.ThrowStmt th
            && th.getExpression() instanceof ObjectCreationExpr oc && oc.getArguments().size() == 1
            && oc.getArgument(0) instanceof com.github.javaparser.ast.expr.StringLiteralExpr sl && sl.getValue().startsWith("JX-DEAD");
        for (Parameter p : c.getParameters()) x.params.add(new Param(p.getNameAsString(), paramType(p)));
        x.lines = lines(c);
        x.branches = branches(c, owner);
        x.locals = locals(c, owner);
        if (c.getAnnotations().stream().anyMatch(a -> simple(a).equals("Builder"))) {
            m.builder = true;
            if (!m.lombok.contains("Builder")) m.lombok.add("Builder");
        }
        return x;
    }

    static String paramType(Parameter p) {
        return norm(p.getType()) + (p.isVarArgs() ? "..." : "");
    }

    private static String visibility(NodeWithModifiers<?> n) {
        if (n.hasModifier(Modifier.Keyword.PUBLIC)) return "public";
        if (n.hasModifier(Modifier.Keyword.PROTECTED)) return "protected";
        if (n.hasModifier(Modifier.Keyword.PRIVATE)) return "private";
        return "package";
    }

    private static void recordMembers(RecordDeclaration r, TypeModel m) {
        List<Param> components = new ArrayList<>();
        for (Parameter p : r.getParameters()) components.add(new Param(p.getNameAsString(), paramType(p)));
        int at = 0;
        for (Param c : components) {
            Field f = new Field();
            f.name = c.name;
            f.type = c.type;
            f.visibility = "private";
            f.isFinal = true;
            f.origin = "record";
            m.fields.add(at++, f);
            if (find(m, c.name, 0) == null) {
                Method a = new Method();
                a.name = c.name;
                a.returnType = c.type;
                a.visibility = "public";
                a.origin = "record";
                m.methods.add(a);
            }
        }
        String canonical = String.join(",", components.stream().map(p -> erase(p.type)).toList());
        boolean hasCanonical = m.methods.stream().anyMatch(x -> x.ctor && x.erasedParamTypes().equals(canonical));
        if (!hasCanonical) {
            Method c = new Method();
            c.name = "<init>";
            c.ctor = true;
            c.visibility = "public";
            c.origin = "record";
            c.params.addAll(components);
            m.methods.add(c);
        }
        objectMethods(m, "record");
    }

    private static void objectMethods(TypeModel m, String origin) {
        if (find(m, "equals", 1) == null) m.methods.add(synthetic("equals", "boolean", origin, new Param("o", "Object")));
        if (find(m, "hashCode", 0) == null) m.methods.add(synthetic("hashCode", "int", origin));
        if (find(m, "toString", 0) == null) m.methods.add(synthetic("toString", "String", origin));
    }

    private static Method synthetic(String name, String ret, String origin, Param... ps) {
        Method x = new Method();
        x.name = name;
        x.returnType = ret;
        x.visibility = "public";
        x.origin = origin;
        x.params.addAll(List.of(ps));
        return x;
    }

    private static Method find(TypeModel m, String name, int arity) {
        for (Method x : m.methods) if (!x.ctor && x.name.equals(name) && x.params.size() == arity) return x;
        return null;
    }

    private static String simple(AnnotationExpr a) {
        String n = a.getNameAsString();
        return n.startsWith("lombok.") ? n.substring(n.lastIndexOf('.') + 1) : n;
    }

    /** The AccessLevel named in a Lombok annotation, as a visibility, or the default. */
    private static String access(AnnotationExpr a, String dflt) {
        String s = a.toString();
        for (String lvl : List.of("PUBLIC", "PROTECTED", "PRIVATE", "PACKAGE", "NONE", "MODULE")) {
            if (s.contains("AccessLevel." + lvl)) {
                return switch (lvl) {
                    case "PACKAGE", "MODULE" -> "package";
                    default -> lvl.toLowerCase(Locale.ROOT);
                };
            }
        }
        return dflt;
    }

    private static AnnotationExpr ann(NodeList<AnnotationExpr> as, String... names) {
        for (AnnotationExpr a : as) {
            String s = simple(a);
            for (String n : names) if (s.equals(n)) return a;
        }
        return null;
    }

    private static void lombokMembers(TypeDeclaration<?> t, TypeModel m) {
        NodeList<AnnotationExpr> cls = t.getAnnotations();
        Set<String> used = new LinkedHashSet<>();
        for (AnnotationExpr a : cls) {
            String s = simple(a);
            if (Set.of("Getter", "Setter", "Data", "Value", "Builder", "SuperBuilder", "AllArgsConstructor", "NoArgsConstructor",
                "RequiredArgsConstructor", "EqualsAndHashCode", "ToString", "Accessors", "With").contains(s)) used.add(s);
        }
        Map<String, FieldDeclaration> decl = new HashMap<>();
        for (FieldDeclaration f : t.getFields()) {
            for (VariableDeclarator v : f.getVariables()) decl.put(v.getNameAsString(), f);
            for (AnnotationExpr a : f.getAnnotations()) {
                String s = simple(a);
                if (s.equals("Getter") || s.equals("Setter")) used.add(s + "(field)");
            }
        }
        if (used.isEmpty()) return;
        m.lombok.addAll(used.stream().filter(s -> !m.lombok.contains(s)).toList());
        AnnotationExpr accessors = ann(cls, "Accessors");
        boolean fluent = accessors != null && accessors.toString().replace(" ", "").contains("fluent=true");
        boolean chain = fluent || accessors != null && accessors.toString().replace(" ", "").contains("chain=true");
        boolean value = ann(cls, "Value") != null;
        boolean data = ann(cls, "Data") != null;
        AnnotationExpr classGetter = ann(cls, "Getter");
        AnnotationExpr classSetter = ann(cls, "Setter");
        if (value) {
            m.isFinal = true;
            for (Field f : m.fields) {
                if (!f.isStatic) {
                    f.isFinal = true;
                    if (f.visibility.equals("package")) f.visibility = "private";
                }
            }
        }
        List<Field> instance = m.fields.stream().filter(f -> !f.isStatic && f.origin.equals("source")).toList();
        for (Field f : instance) {
            FieldDeclaration fd = decl.get(f.name);
            AnnotationExpr fg = fd == null ? null : ann(fd.getAnnotations(), "Getter");
            String gv = fg != null ? access(fg, "public") : classGetter != null ? access(classGetter, "public") : data || value ? "public" : null;
            if (gv != null && !gv.equals("none")) {
                boolean bool = f.type.equals("boolean");
                String name = fluent ? f.name : bool ? (f.name.matches("is[A-Z].*") ? f.name : "is" + cap(f.name)) : "get" + cap(f.name);
                if (find(m, name, 0) == null) {
                    Method g = synthetic(name, f.type, "lombok");
                    g.visibility = gv;
                    m.methods.add(g);
                }
            }
            AnnotationExpr fs = fd == null ? null : ann(fd.getAnnotations(), "Setter");
            String sv = fs != null ? access(fs, "public") : classSetter != null && !f.isFinal ? access(classSetter, "public")
                : data && !f.isFinal ? "public" : null;
            if (sv != null && !sv.equals("none")) {
                String base = f.type.equals("boolean") && f.name.matches("is[A-Z].*") ? f.name.substring(2) : f.name;
                String name = fluent ? f.name : "set" + cap(base);
                if (find(m, name, 1) == null) {
                    Method s = synthetic(name, chain ? m.name : "void", "lombok", new Param(f.name, f.type));
                    s.visibility = sv;
                    m.methods.add(s);
                }
            }
        }
        boolean explicitCtor = m.methods.stream().anyMatch(x -> x.ctor);
        AnnotationExpr all = ann(cls, "AllArgsConstructor");
        AnnotationExpr req = ann(cls, "RequiredArgsConstructor");
        AnnotationExpr none = ann(cls, "NoArgsConstructor");
        if (none != null) ctor(m, access(none, "public"), List.of());
        if (all != null || value && !explicitCtor && req == null && none == null) {
            List<Field> fs = instance.stream().filter(f -> !(f.isFinal && initialized(decl.get(f.name), f.name))).toList();
            ctor(m, all != null ? access(all, "public") : "public", fs);
        }
        if (req != null || data && !explicitCtor && all == null && none == null) {
            List<Field> fs = instance.stream().filter(f -> f.isFinal && !initialized(decl.get(f.name), f.name)
                || decl.get(f.name) != null && ann(decl.get(f.name).getAnnotations(), "NonNull") != null).toList();
            ctor(m, req != null ? access(req, "public") : "public", fs);
        }
        if (ann(cls, "Builder", "SuperBuilder") != null) {
            m.builder = true;
            if (find(m, "builder", 0) == null) {
                Method b = synthetic("builder", m.name + "Builder", "lombok");
                b.isStatic = true;
                m.methods.add(b);
            }
        }
        if (data || value || ann(cls, "EqualsAndHashCode") != null) {
            if (find(m, "equals", 1) == null) m.methods.add(synthetic("equals", "boolean", "lombok", new Param("o", "Object")));
            if (find(m, "hashCode", 0) == null) m.methods.add(synthetic("hashCode", "int", "lombok"));
            if (find(m, "canEqual", 1) == null) {
                Method c = synthetic("canEqual", "boolean", "lombok", new Param("other", "Object"));
                c.visibility = "protected";
                m.methods.add(c);
            }
        }
        if ((data || value || ann(cls, "ToString") != null) && find(m, "toString", 0) == null) {
            m.methods.add(synthetic("toString", "String", "lombok"));
        }
    }

    private static boolean initialized(FieldDeclaration fd, String name) {
        if (fd == null) return false;
        for (VariableDeclarator v : fd.getVariables()) if (v.getNameAsString().equals(name)) return v.getInitializer().isPresent();
        return false;
    }

    private static void ctor(TypeModel m, String visibility, List<Field> fields) {
        String key = String.join(",", fields.stream().map(f -> erase(f.type)).toList());
        if (m.methods.stream().anyMatch(x -> x.ctor && x.erasedParamTypes().equals(key))) return;
        Method c = new Method();
        c.name = "<init>";
        c.ctor = true;
        c.visibility = visibility;
        c.origin = "lombok";
        for (Field f : fields) c.params.add(new Param(f.name, f.type));
        m.methods.add(c);
    }

    static String cap(String s) {
        return s.isEmpty() ? s : Character.toUpperCase(s.charAt(0)) + s.substring(1);
    }

    /** The member type that owns a node: the closest enclosing type declaration whose parent is a type or the unit. */
    static TypeDeclaration<?> owner(Node n) {
        Node p = n.getParentNode().orElse(null);
        while (p != null) {
            if (p instanceof TypeDeclaration<?> t) {
                Node pp = t.getParentNode().orElse(null);
                if (pp instanceof CompilationUnit || pp instanceof TypeDeclaration<?>) return t;
            }
            p = p.getParentNode().orElse(null);
        }
        return null;
    }

    private static boolean isBranch(Node n) {
        if (n instanceof IfStmt || n instanceof ConditionalExpr || n instanceof ForStmt || n instanceof ForEachStmt
            || n instanceof WhileStmt || n instanceof DoStmt || n instanceof CatchClause) return true;
        if (n instanceof SwitchEntry e) return !e.getLabels().isEmpty();
        return n instanceof BinaryExpr b && (b.getOperator() == BinaryExpr.Operator.AND || b.getOperator() == BinaryExpr.Operator.OR);
    }

    private static int branches(Node callable, TypeDeclaration<?> owner) {
        int[] n = {0};
        callable.walk(x -> {
            if (x != callable && isBranch(x) && owner(x) == owner) n[0]++;
        });
        return n[0];
    }

    private static List<Local> locals(Node callable, TypeDeclaration<?> owner) {
        List<Local> out = new ArrayList<>();
        callable.walk(VariableDeclarationExpr.class, v -> {
            if (owner(v) != owner) return;
            for (VariableDeclarator d : v.getVariables()) out.add(new Local(d.getNameAsString(), norm(d.getType())));
        });
        return out;
    }

    /** The callable (method, constructor, compact constructor) of type t a node sits in, or null for field initializers etc. */
    private static Node callableOf(Node n, TypeDeclaration<?> t) {
        Node p = n.getParentNode().orElse(null);
        while (p != null && p != t) {
            if (p instanceof CallableDeclaration<?> || p instanceof CompactConstructorDeclaration) return p;
            p = p.getParentNode().orElse(null);
        }
        return null;
    }

    /**
     * Counts idioms and watched type uses for the type and, where the node sits in a callable, for that method too, so
     * a comparison can leave out the methods that have no counterpart (in practice: those J3S never compiled).
     */
    private static void measure(TypeDeclaration<?> t, TypeModel m, boolean[] code, Map<Node, Method> byNode) {
        t.walk(n -> {
            if (n == t || owner(n) != t) return;
            Node c = callableOf(n, t);
            Method x = c == null ? null : byNode.get(c);
            Map<String, Integer> mi = x == null ? null : x.idioms;
            Map<String, Integer> mu = x == null ? null : x.typeUses;
            if (isBranch(n)) m.branches++;
            if (n instanceof ForStmt || n instanceof ForEachStmt || n instanceof WhileStmt || n instanceof DoStmt) bump(m.idioms, mi, "loop");
            if (n instanceof ClassOrInterfaceType ct) use(m, mi, mu, ct.getNameAsString());
            else if (n instanceof NameExpr ne && ne.getParentNode().map(p -> p instanceof MethodCallExpr mc && mc.getScope().orElse(null) == ne
                || p instanceof FieldAccessExpr fa && fa.getScope() == ne).orElse(false)) use(m, mi, mu, ne.getNameAsString());
            else if (n instanceof MethodCallExpr mc && (mc.getNameAsString().equals("stream") || mc.getNameAsString().equals("parallelStream"))) {
                bump(m.idioms, mi, "stream");
            } else if (n instanceof LambdaExpr) bump(m.idioms, mi, "lambda");
            else if (n instanceof MethodReferenceExpr) bump(m.idioms, mi, "methodRef");
            else if (n instanceof SwitchExpr) bump(m.idioms, mi, "switchExpr");
            else if (n instanceof ObjectCreationExpr oc && oc.getType().getNameAsString().matches(".*(Exception|Error)")) {
                bump(m.idioms, mi, oc.getArguments().isEmpty() ? "exceptionBare" : "exceptionWithArgs");
            } else if (n instanceof VarType) bump(m.idioms, mi, "var");
            else if (n instanceof TextBlockLiteralExpr) bump(m.idioms, mi, "textBlock");
            else if (n instanceof InstanceOfExpr io && io.getPattern().isPresent()) bump(m.idioms, mi, "patternMatch");
            else if (n instanceof ReturnStmt r && r.getExpression().map(e -> e instanceof NullLiteralExpr).orElse(false)) {
                bump(m.idioms, mi, "returnNull");
            } else if (n instanceof BinaryExpr b && (b.getOperator() == BinaryExpr.Operator.EQUALS
                || b.getOperator() == BinaryExpr.Operator.NOT_EQUALS) && (b.getLeft() instanceof NullLiteralExpr
                || b.getRight() instanceof NullLiteralExpr)) bump(m.idioms, mi, "nullCheck");
        });
        if (m.kind.equals("record")) bump(m.idioms, "record");
        if (m.kind.equals("enum")) bump(m.idioms, "enum");
        if (m.kind.equals("interface")) bump(m.idioms, "interface");
        m.loc = ownCodeLines(t, code);
    }

    private static void use(TypeModel m, Map<String, Integer> mi, Map<String, Integer> mu, String name) {
        String family = WATCHED.get(name);
        if (family == null) return;
        bump(m.idioms, mi, family);
        bump(m.typeUses, mu, name);
    }

    private static void bump(Map<String, Integer> a, Map<String, Integer> b, String k) {
        bump(a, k);
        if (b != null) bump(b, k);
    }

    private static void bump(Map<String, Integer> map, String k) {
        map.merge(k, 1, Integer::sum);
    }

    static int lines(Node n) {
        return n.getRange().map(r -> r.end.line - r.begin.line + 1).orElse(0);
    }

    private static int ownCodeLines(TypeDeclaration<?> t, boolean[] code) {
        if (t.getRange().isEmpty()) return 0;
        int b = t.getRange().get().begin.line;
        int e = t.getRange().get().end.line;
        boolean[] mask = new boolean[e - b + 1];
        for (BodyDeclaration<?> m : t.getMembers()) {
            if (m instanceof TypeDeclaration<?> && m.getRange().isPresent()) {
                for (int l = m.getRange().get().begin.line; l <= m.getRange().get().end.line; l++) if (l >= b && l <= e) mask[l - b] = true;
            }
        }
        int n = 0;
        for (int l = b; l <= e; l++) if (!mask[l - b] && l < code.length && code[l]) n++;
        return Math.max(n, 1);
    }

    /** code[line] (1-based) is true when the line holds any character outside comments. */
    static boolean[] codeLines(String s) {
        int lineCount = 1;
        for (int i = 0; i < s.length(); i++) if (s.charAt(i) == '\n') lineCount++;
        boolean[] code = new boolean[lineCount + 2];
        int line = 1;
        int state = 0; // 0 code, 1 line comment, 2 block comment, 3 string, 4 char, 5 text block
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            char d = i + 1 < s.length() ? s.charAt(i + 1) : '\0';
            if (c == '\n') {
                line++;
                if (state == 1) state = 0;
                continue;
            }
            switch (state) {
                case 0 -> {
                    if (c == '/' && d == '/') {
                        state = 1;
                        i++;
                    } else if (c == '/' && d == '*') {
                        state = 2;
                        i++;
                    } else {
                        if (!Character.isWhitespace(c)) code[line] = true;
                        if (c == '"' && d == '"' && i + 2 < s.length() && s.charAt(i + 2) == '"') {
                            state = 5;
                            i += 2;
                        } else if (c == '"') {
                            state = 3;
                        } else if (c == '\'') {
                            state = 4;
                        }
                    }
                }
                case 2 -> {
                    if (c == '*' && d == '/') {
                        state = 0;
                        i++;
                    }
                }
                case 3, 4 -> {
                    code[line] = true;
                    if (c == '\\') i++;
                    else if (c == (state == 3 ? '"' : '\'')) state = 0;
                }
                case 5 -> {
                    if (!Character.isWhitespace(c)) code[line] = true;
                    if (c == '\\') i++;
                    else if (c == '"' && d == '"' && i + 2 < s.length() && s.charAt(i + 2) == '"') {
                        state = 0;
                        i += 2;
                    }
                }
                default -> {
                }
            }
        }
        return code;
    }
}
