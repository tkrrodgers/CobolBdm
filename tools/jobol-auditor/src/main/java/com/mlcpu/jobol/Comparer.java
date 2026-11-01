package com.mlcpu.jobol;

import com.github.javaparser.JavaParser;
import com.github.javaparser.ParseResult;
import com.github.javaparser.ParserConfiguration;
import com.github.javaparser.ast.CompilationUnit;
import com.mlcpu.jobol.CompareReport.Change;
import com.mlcpu.jobol.CompareReport.Gap;
import com.mlcpu.jobol.CompareReport.Pair;
import com.mlcpu.jobol.CompareReport.Pair2;
import com.mlcpu.jobol.CompareReport.Rename;
import com.mlcpu.jobol.CompareReport.TypeDiff;
import com.mlcpu.jobol.JavaModel.Field;
import com.mlcpu.jobol.JavaModel.Local;
import com.mlcpu.jobol.JavaModel.Method;
import com.mlcpu.jobol.JavaModel.TypeModel;
import java.io.IOException;
import java.nio.ByteBuffer;
import java.nio.charset.CharacterCodingException;
import java.nio.charset.CodingErrorAction;
import java.nio.charset.StandardCharsets;
import java.nio.file.FileVisitResult;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.SimpleFileVisitor;
import java.nio.file.attribute.BasicFileAttributes;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.HashMap;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.TreeMap;
import java.util.TreeSet;
import java.util.function.Function;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * Compares an original Java tree with the modern Java produced from its COBOL translation. Types are aligned by
 * fully qualified name (then by unique simple name: moved or relocated), members by name and arity, then signature.
 * Deterministic: inputs are visited in sorted order and every output list is sorted.
 */
public final class Comparer {
    /** Distance weights; documented in the Markdown report and COMPARE-GAPS.md. */
    static final double W_KIND = 5, W_TYPE_VIS = 2, W_MODIFIER = 1, W_SUPER_LOST = 3, W_SUPER_ADDED = 1, W_METHOD_MISSING_API = 3,
        W_METHOD_MISSING = 2, W_METHOD_EXTRA = 1, W_METHOD_EXTRA_PRIVATE = 0.5, W_SIGNATURE = 2, W_MEMBER_FLAG = 1,
        W_FIELD_MISSING = 2, W_FIELD_EXTRA = 0.5, W_FIELD_TYPE = 1, W_FIELD_FLAG = 0.5, W_CONSTANT_MISSING = 2,
        W_CONSTANT_EXTRA = 1, W_MEMBER_NAME = 1, W_LOCAL_NAME = 0.25, W_BUILDER = 3, W_IDIOM = 0.5, IDIOM_CAP = 10;
    static final List<String> SCORED_IDIOMS = List.of("optional", "stream", "lambda", "methodRef", "switchExpr", "record",
        "interface", "functional", "patternMatch");

    private static final Pattern CLASS_HEADER = Pattern.compile("^ {6}\\*\\s*CLASS\\s*:\\s?(.*)$");
    private static final Pattern FRAME = Pattern.compile("^ {6}\\* FRAME OF M\\d+-\\S+ (.*)$");
    private static final Pattern FRAME_METHOD = Pattern.compile("([A-Za-z_$][\\w$]*)\\.(<init>|[A-Za-z_$][\\w$]*)\\(");
    private static final Set<String> JDK_INTERFACES = Set.of("Comparable", "Serializable", "Cloneable", "Iterable", "AutoCloseable",
        "Closeable", "Runnable", "Callable", "Supplier", "Function", "Comparator", "CharSequence", "Externalizable");
    private static final Set<String> COLLECTION_FAMILIES = Set.of("list", "set", "map", "collection");

    private final Config cfg;
    private final JavaParser parser;
    private final List<java.util.regex.Pattern> infrastructure = new ArrayList<>();

    public Comparer(Config cfg) {
        this.cfg = cfg;
        this.parser = new JavaParser(new ParserConfiguration().setLanguageLevel(ParserConfiguration.LanguageLevel.JAVA_21));
        for (String g : cfg.compareInfrastructureGlobs) infrastructure.add(Glob.compile(g));
    }

    /** What J3S compiled: classes (program headers) and methods (frame comments) by simple owner name. */
    static final class Reach {
        final Set<String> classes = new TreeSet<>();
        final Map<String, Set<String>> methods = new TreeMap<>();
        boolean known;

        /**
         * A frame for the owner's simple name, the method name and parameter types. Frames print each parameter type by
         * its last dotted segment, so a generic parameter survives only as its last type argument (Map&lt;LocalDate,
         * Money&gt; reads Money&gt;) and varargs read as arrays.
         */
        boolean reached(String ownerSimple, Method x) {
            if (!known || x.origin.equals("record") && x.ctor) return true;
            Set<String> s = methods.get(ownerSimple);
            if (s == null) return false;
            for (String f : s) {
                int open = f.indexOf('(');
                if (!f.substring(0, open).equals(x.name)) continue;
                List<String> tokens = frameParams(f.substring(open + 1, f.length() - 1));
                if (tokens.size() != x.params.size()) continue;
                boolean all = true;
                for (int i = 0; i < tokens.size() && all; i++) all = frameMatches(tokens.get(i), x.params.get(i).type);
                if (all) return true;
            }
            return false;
        }
    }

    static List<String> frameParams(String params) {
        List<String> out = new ArrayList<>();
        if (params.isBlank()) return out;
        int depth = 0;
        int start = 0;
        for (int i = 0; i < params.length(); i++) {
            char c = params.charAt(i);
            if (c == '<') depth++;
            else if (c == '>') depth = Math.max(0, depth - 1);
            else if (c == ',' && depth == 0) {
                out.add(params.substring(start, i).trim());
                start = i + 1;
            }
        }
        out.add(params.substring(start).trim());
        return out;
    }

    static boolean frameMatches(String token, String originalType) {
        String t = token.replace("...", "[]");
        String o = originalType.replace("...", "[]");
        if (t.endsWith(">")) return o.contains("<") && o.endsWith(t);
        String e = JavaModel.erase(o);
        if (e.equals(t)) return true;
        return e.matches("[A-Z][A-Z0-9]?(\\[])*") && t.startsWith("Object");
    }

    static Reach readCobol(Path root) throws IOException {
        Reach r = new Reach();
        if (root == null) return r;
        r.known = true;
        List<Path> files = new ArrayList<>();
        try (var s = Files.walk(root)) {
            s.filter(Files::isRegularFile).filter(p -> p.toString().toLowerCase().matches(".*\\.(cbl|cob|cpy)$")).forEach(files::add);
        }
        files.sort(Comparator.comparing(Path::toString));
        for (Path p : files) {
            List<String> lines = List.of(read(p).split("\r?\n"));
            StringBuilder cls = null;
            for (int i = 0; i < lines.size(); i++) {
                String l = cut(lines.get(i));
                Matcher h = CLASS_HEADER.matcher(l);
                if (h.matches()) {
                    if (cls == null) cls = new StringBuilder();
                    cls.append(h.group(1).trim());
                    continue;
                }
                if (cls != null) {
                    if (!cls.isEmpty()) r.classes.add(cls.toString().replace('$', '.'));
                    cls = null;
                }
                Matcher f = FRAME.matcher(l);
                if (!f.matches()) continue;
                StringBuilder text = new StringBuilder(f.group(1));
                int j = i + 1;
                while (depth(text) > 0 && j < lines.size() && cut(lines.get(j)).startsWith("      * ")) {
                    text.append(cut(lines.get(j)).substring(8));
                    j++;
                }
                Matcher fm = FRAME_METHOD.matcher(text);
                if (fm.find()) {
                    String owner = fm.group(1);
                    owner = owner.substring(owner.lastIndexOf('$') + 1);
                    int open = fm.end();
                    int close = text.lastIndexOf(")");
                    String params = close > open ? text.substring(open, close) : "";
                    r.methods.computeIfAbsent(owner, k -> new TreeSet<>()).add(fm.group(2) + "(" + String.join(",", frameParams(params)) + ")");
                }
            }
        }
        return r;
    }

    private static String cut(String l) {
        return l.length() > 72 ? l.substring(0, 72) : l;
    }

    private static int depth(CharSequence s) {
        int d = 0;
        boolean opened = false;
        for (int i = 0; i < s.length(); i++) {
            if (s.charAt(i) == '(') {
                d++;
                opened = true;
            } else if (s.charAt(i) == ')') {
                d--;
            }
        }
        return opened ? d : 1;
    }

    /** Expands '*' path segments (C:/x/fineract/*&#47;src/main/java) into the existing directories, sorted. */
    static List<Path> expand(String spec) {
        String s = spec.replace('\\', '/');
        int star = s.indexOf('*');
        if (star < 0) return List.of(Path.of(spec));
        int cut = s.lastIndexOf('/', star);
        Path base = Path.of(s.substring(0, cut < 0 ? 0 : cut + 1).isEmpty() ? "." : s.substring(0, cut + 1));
        int next = s.indexOf('/', star);
        String seg = s.substring(cut + 1, next < 0 ? s.length() : next);
        String rest = next < 0 ? "" : s.substring(next + 1);
        Pattern p = Glob.compile(seg);
        List<Path> out = new ArrayList<>();
        try (var st = Files.list(base)) {
            List<Path> kids = st.filter(Files::isDirectory).filter(k -> p.matcher(k.getFileName().toString()).matches())
                .sorted(Comparator.comparing(Path::toString)).toList();
            for (Path k : kids) {
                if (rest.isEmpty()) out.add(k);
                else for (Path e : expand(k.toString().replace('\\', '/') + "/" + rest)) if (Files.isDirectory(e)) out.add(e);
            }
        } catch (IOException e) {
            return out;
        }
        return out;
    }

    private record Loaded(Map<String, TypeModel> types, Map<String, String> fileOf, List<String> errors, List<String> skipped) {
    }

    /** Out-of-scope files are listed by name up to this many; above it (a whole vendor tree) only counted. */
    static final int LIST_OUT_OF_SCOPE = 500;

    private Loaded load(List<Path> roots, Function<String, Boolean> keepFile) throws IOException {
        Map<String, TypeModel> types = new LinkedHashMap<>();
        Map<String, String> fileOf = new HashMap<>();
        List<String> errors = new ArrayList<>();
        List<String> skipped = new ArrayList<>();
        Set<String> skip = new HashSet<>(cfg.excludeDirs);
        for (Path root : roots) {
            List<Path> files = new ArrayList<>();
            Files.walkFileTree(root, new SimpleFileVisitor<>() {
                @Override
                public FileVisitResult preVisitDirectory(Path dir, BasicFileAttributes attrs) {
                    Path n = dir.getFileName();
                    return !dir.equals(root) && n != null && skip.contains(n.toString()) ? FileVisitResult.SKIP_SUBTREE : FileVisitResult.CONTINUE;
                }

                @Override
                public FileVisitResult visitFile(Path file, BasicFileAttributes attrs) {
                    String n = file.getFileName().toString();
                    if (n.endsWith(".java") && !n.equals("package-info.java") && !n.equals("module-info.java")) files.add(file);
                    return FileVisitResult.CONTINUE;
                }
            });
            files.sort(Comparator.comparing(p -> p.toString().replace('\\', '/')));
            for (Path p : files) {
                String rel = root.relativize(p).toString().replace('\\', '/');
                if (!keepFile.apply(rel)) {
                    skipped.add(fileFqn(rel));
                    continue;
                }
                String src = read(p);
                ParseResult<CompilationUnit> pr = parser.parse(src);
                if (!pr.isSuccessful() || pr.getResult().isEmpty()) {
                    errors.add(rel + ": " + (pr.getProblems().isEmpty() ? "unparseable" : Analyzer.abbreviate(pr.getProblems().get(0).getMessage(), 160)));
                    continue;
                }
                for (TypeModel t : JavaModel.extract(pr.getResult().get(), rel, src)) {
                    if (types.containsKey(t.fqn)) {
                        errors.add(rel + ": duplicate type " + t.fqn + " (kept " + fileOf.get(t.fqn) + ")");
                        continue;
                    }
                    types.put(t.fqn, t);
                    fileOf.put(t.fqn, rel);
                }
            }
        }
        return new Loaded(types, fileOf, errors, skipped);
    }

    /** The fqn a file declares by convention (pkg/dirs/Name.java). */
    private static String fileFqn(String rel) {
        return rel.substring(0, rel.length() - ".java".length()).replace('/', '.');
    }

    private static String simpleOf(String fqn) {
        return fqn.substring(fqn.lastIndexOf('.') + 1);
    }

    public CompareReport compare(String estate, List<Path> originalRoots, Path modernRoot, Path cobolRoot, String scope) throws IOException {
        CompareReport r = new CompareReport();
        r.estate = estate;
        for (Path p : originalRoots) r.originalRoots.add(p.toAbsolutePath().normalize().toString().replace('\\', '/'));
        r.modernRoot = modernRoot.toAbsolutePath().normalize().toString().replace('\\', '/');
        r.cobolRoot = cobolRoot == null ? null : cobolRoot.toAbsolutePath().normalize().toString().replace('\\', '/');
        Reach reach = readCobol(cobolRoot);
        r.scope = scope != null ? scope : reach.known ? "reached" : "all";
        boolean all = r.scope.equals("all");

        Loaded modern = load(List.of(modernRoot), rel -> true);
        vocabulary = vocabulary(modern.types.values());
        Set<String> ms = new HashSet<>();
        for (TypeModel t : modern.types.values()) ms.add(t.name);
        modernSimple = ms;
        Set<String> modernTop = new HashSet<>();
        Set<String> modernTopSimple = new HashSet<>();
        Set<String> infra = new HashSet<>();
        for (TypeModel t : modern.types.values()) {
            if (infrastructureReason(modern.fileOf.get(t.fqn)) != null) {
                infra.add(t.fqn);
                continue;
            }
            if (t.outer == null) {
                modernTop.add(t.fqn);
                modernTopSimple.add(t.name);
            }
        }
        Loaded orig = load(originalRoots, rel -> {
            if (all) return true;
            String fqn = fileFqn(rel);
            return reach.classes.contains(fqn) || modernTop.contains(fqn) || modernTopSimple.contains(simpleOf(fqn));
        });
        for (String e : modern.errors) r.parseErrors.add("modern " + e);
        for (String e : orig.errors) r.parseErrors.add("original " + e);

        // scope of the original side
        Map<String, Integer> origSimpleCount = new HashMap<>();
        for (TypeModel t : orig.types.values()) if (t.outer == null) origSimpleCount.merge(t.name, 1, Integer::sum);
        Set<String> inScopeTop = new TreeSet<>();
        for (TypeModel t : orig.types.values()) {
            if (t.outer != null) continue;
            boolean in = all || reach.classes.contains(t.fqn) || modernTop.contains(t.fqn)
                || modernTopSimple.contains(t.name) && !modern.types.containsKey(t.fqn) && origSimpleCount.get(t.name) == 1
                && !modernTop.contains(t.fqn);
            if (in) inScopeTop.add(t.fqn);
            else r.outOfScope.add(t.fqn);
        }
        Map<String, TypeModel> o = new TreeMap<>();
        for (TypeModel t : orig.types.values()) if (inScopeTop.contains(top(t, orig.types))) o.put(t.fqn, t);
        Map<String, TypeModel> m = new TreeMap<>(modern.types);
        Map<String, TypeModel> mDomain = new TreeMap<>(m);
        mDomain.keySet().removeAll(infra);

        Map<String, TypeModel> bySimple = new HashMap<>();
        Set<String> dupSimple = new HashSet<>();
        for (TypeModel t : o.values()) if (bySimple.put(t.name, t) != null) dupSimple.add(t.name);
        bySimple.keySet().removeAll(dupSimple);
        origBySimple = bySimple;

        // align types
        Map<String, String> align = new TreeMap<>();
        Map<String, String> how = new HashMap<>();
        for (String fqn : o.keySet()) {
            if (m.containsKey(fqn)) {
                align.put(fqn, fqn);
                how.put(fqn, "fqn");
            }
        }
        Set<String> mUsed = new HashSet<>(align.values());
        bySimpleName(o, mDomain, align, mUsed, how, true);
        // nested types of moved outers follow them
        for (String fqn : new ArrayList<>(align.keySet())) {
            if (!how.get(fqn).equals("moved")) continue;
            String target = align.get(fqn);
            for (String n : o.keySet()) {
                if (n.startsWith(fqn + ".") && !align.containsKey(n)) {
                    String cand = target + n.substring(fqn.length());
                    if (m.containsKey(cand) && !mUsed.contains(cand)) {
                        align.put(n, cand);
                        mUsed.add(cand);
                        how.put(n, "moved");
                    }
                }
            }
        }
        bySimpleName(o, mDomain, align, mUsed, how, false);

        // modern-only
        for (TypeModel t : m.values()) {
            if (mUsed.contains(t.fqn)) continue;
            String rel = modern.fileOf.get(t.fqn);
            String reason = infrastructureReason(rel);
            boolean isInfra = reason != null;
            // the explicit form of an original Lombok @Builder (the original side models only builder())
            String outerOriginal = t.outer == null ? null : reverse(align, t.outer);
            boolean lombokBuilder = outerOriginal != null && t.name.equals(simpleOf(t.outer) + "Builder")
                && o.get(outerOriginal).lombok.contains("Builder");
            r.modernOnly.add(new CompareReport.ModernOnly(t.fqn, t.kind, rel,
                isInfra ? "pipeline-infrastructure" : lombokBuilder ? "lombok-expansion" : "other",
                isInfra ? reason : lombokBuilder ? "the delomboked builder class of @Builder on " + outerOriginal : "no original with this name"));
            if (isInfra) r.summary.modernOnlyInfrastructure++;
            else if (lombokBuilder) r.summary.modernOnlyLombok++;
            else {
                r.summary.modernOnlyOther++;
                r.gaps.add(new Gap("TYPE.MODERN_ONLY_" + t.kind.toUpperCase(), t.fqn, "", "", t.fqn, t.kind));
            }
        }

        // original-only
        CompareReport.Summary s = r.summary;
        s.originalTypes = o.size();
        s.outOfScope = r.outOfScope.size() + orig.skipped.size();
        if (orig.skipped.size() <= LIST_OUT_OF_SCOPE) r.outOfScope.addAll(orig.skipped);
        else r.notes.add(orig.skipped.size() + " original files outside the J3S scope were not parsed (not listed)");
        r.outOfScope.sort(null);
        for (TypeModel t : o.values()) {
            if (align.containsKey(t.fqn)) continue;
            int api = 0;
            int members = t.fields.size() + t.methods.size() + t.enumConstants.size();
            if (t.effectivelyPublic) {
                for (Field f : t.fields) if (isApi(f.visibility)) api++;
                for (Method x : t.methods) if (isApi(x.visibility)) api++;
                api += t.enumConstants.size();
            }
            String topFqn = top(t, orig.types);
            boolean reached = reach.classes.contains(t.fqn) || reach.classes.contains(topFqn);
            r.originalOnly.add(new CompareReport.Missing(t.fqn, t.kind, orig.fileOf.get(t.fqn), reached, api, members));
            String outerNote = t.outer != null ? "nested in " + simpleOf(t.outer) : "top-level";
            boolean anyReached = !reach.known || reach.methods.containsKey(t.name);
            for (int i = 0; i < api; i++) {
                s.api.add(false);
                if (anyReached) s.apiReached.add(false);
            }
            for (Method x : t.methods) {
                s.signatures.add(false);
                if (anyReached) s.signaturesReached.add(false);
            }
            r.gaps.add(new Gap((anyReached ? "TYPE.MISSING_" : "TYPE.UNREACHED_") + t.kind.toUpperCase(), t.fqn, "", t.fqn, "",
                outerNote + ", " + members + " members"));
        }
        s.originalOnly = r.originalOnly.size();

        // matched
        for (Map.Entry<String, String> e : align.entrySet()) {
            TypeModel a = o.get(e.getKey());
            TypeModel b = m.get(e.getValue());
            TypeDiff d = diff(a, b, how.get(e.getKey()), reach, r.gaps, s);
            d.originalFile = orig.fileOf.get(a.fqn);
            d.modernFile = modern.fileOf.get(b.fqn);
            r.matched.add(d);
            if (d.alignment.equals("fqn")) s.matchedByFqn++;
            else s.matchedMoved++;
        }
        s.matched = r.matched.size();

        // structure, idioms
        for (TypeModel t : o.values()) structure(t, s.originalStructure);
        for (TypeModel t : m.values()) if (mUsed.contains(t.fqn)) structure(t, s.modernStructure);
        for (TypeDiff d : r.matched) {
            merge(s.idioms, d.idioms);
            merge(s.typeUses, d.typeUses);
        }
        for (TypeModel t : o.values()) {
            if (align.containsKey(t.fqn)) continue;
            t.idioms.forEach((k, v) -> s.idioms.computeIfAbsent(k, x -> new Pair()).originalAll += v);
            t.typeUses.forEach((k, v) -> s.typeUses.computeIfAbsent(k, x -> new Pair()).originalAll += v);
        }
        close(s.originalStructure);
        close(s.modernStructure);
        s.api.close();
        s.apiReached.close();
        s.names.close();
        s.signatures.close();
        s.signaturesReached.close();
        for (Gap g : r.gaps) s.gapCounts.merge(g.pattern(), 1, Integer::sum);

        r.matched.sort(Comparator.comparing((TypeDiff d) -> d.original));
        r.originalOnly.sort(Comparator.comparing(CompareReport.Missing::fqn));
        r.modernOnly.sort(Comparator.comparing(CompareReport.ModernOnly::category).thenComparing(CompareReport.ModernOnly::fqn));
        r.gaps.sort(Comparator.comparing(Gap::pattern).thenComparing(Gap::type).thenComparing(Gap::member)
            .thenComparing(Gap::original).thenComparing(Gap::modern).thenComparing(Gap::detail));
        List<TypeDiff> ranked = new ArrayList<>(r.matched);
        ranked.sort(Comparator.comparingDouble((TypeDiff d) -> d.distance).reversed().thenComparing(d -> d.original));
        for (TypeDiff d : ranked.subList(0, Math.min(20, ranked.size()))) {
            r.topByDistance.add(new CompareReport.Ranked(d.original, d.modern, d.distance, d.distancePerMember, topReasons.getOrDefault(d, "")));
        }
        if (!reach.known) r.notes.add("no --cobol: every original type is in scope and missing methods are not split by J3S reach");
        if (orig.types.values().stream().anyMatch(t -> !t.lombok.isEmpty())) {
            r.notes.add("Lombok: the original side is an approximate delomboked view (getters, setters, constructors, builder(), "
                + "equals/hashCode/toString added as members with origin 'lombok'); no delombok run was available");
        }
        return r;
    }

    /** Why a modern file is pipeline infrastructure (its lane, or a compareInfrastructureGlobs entry), or null. */
    String infrastructureReason(String rel) {
        Zone z = cfg.zoneOf(rel);
        if (z != Zone.MODERN) return "lane:" + z.id();
        for (int i = 0; i < infrastructure.size(); i++) {
            if (infrastructure.get(i).matcher(rel).matches()) return "glob:" + cfg.compareInfrastructureGlobs.get(i);
        }
        return null;
    }

    private static String reverse(Map<String, String> align, String modernFqn) {
        for (Map.Entry<String, String> e : align.entrySet()) if (e.getValue().equals(modernFqn)) return e.getKey();
        return null;
    }

    private static String top(TypeModel t, Map<String, TypeModel> all) {
        TypeModel x = t;
        while (x.outer != null && all.containsKey(x.outer)) x = all.get(x.outer);
        return x.fqn;
    }

    private static void bySimpleName(Map<String, TypeModel> o, Map<String, TypeModel> m, Map<String, String> align, Set<String> mUsed,
        Map<String, String> how, boolean topOnly) {
        Map<String, List<String>> os = new TreeMap<>();
        Map<String, List<String>> ms = new TreeMap<>();
        for (TypeModel t : o.values()) if (!align.containsKey(t.fqn) && (!topOnly || t.outer == null)) os.computeIfAbsent(t.name, k -> new ArrayList<>()).add(t.fqn);
        for (TypeModel t : m.values()) if (!mUsed.contains(t.fqn) && (!topOnly || t.outer == null)) ms.computeIfAbsent(t.name, k -> new ArrayList<>()).add(t.fqn);
        for (Map.Entry<String, List<String>> e : os.entrySet()) {
            List<String> cand = ms.get(e.getKey());
            if (e.getValue().size() != 1 || cand == null || cand.size() != 1) continue;
            align.put(e.getValue().get(0), cand.get(0));
            mUsed.add(cand.get(0));
            how.put(e.getValue().get(0), topOnly ? "moved" : "relocated");
        }
    }

    private static boolean isApi(String visibility) {
        return visibility.equals("public") || visibility.equals("protected");
    }

    private static int rank(String visibility) {
        return switch (visibility) {
            case "public" -> 3;
            case "protected" -> 2;
            case "package" -> 1;
            default -> 0;
        };
    }

    /** Original types by simple name (unique names only), for INTERFACE_TO_IMPL. */
    private Map<String, TypeModel> origBySimple = Map.of();
    private final Map<TypeDiff, String> topReasons = new java.util.IdentityHashMap<>();

    /** Penalty accumulator for the distance score. */
    private static final class Score {
        double total;
        final Map<String, Double> by = new TreeMap<>();

        void add(String why, double w) {
            total += w;
            by.merge(why, w, Double::sum);
        }

        String top() {
            List<Map.Entry<String, Double>> es = new ArrayList<>(by.entrySet());
            es.sort(Map.Entry.<String, Double>comparingByValue().reversed().thenComparing(Map.Entry.comparingByKey()));
            StringBuilder b = new StringBuilder();
            for (int i = 0; i < Math.min(3, es.size()); i++) {
                if (i > 0) b.append(", ");
                b.append(es.get(i).getKey()).append(' ').append(round1(es.get(i).getValue()));
            }
            return b.toString();
        }
    }

    static double round1(double v) {
        return Math.round(v * 10.0) / 10.0;
    }

    private TypeDiff diff(TypeModel a, TypeModel b, String alignment, Reach reach, List<Gap> gaps, CompareReport.Summary s) {
        TypeDiff d = new TypeDiff();
        Score sc = new Score();
        d.original = a.fqn;
        d.modern = b.fqn;
        d.alignment = alignment;
        String type = a.fqn;
        if (!alignment.equals("fqn")) {
            gaps.add(new Gap("TYPE." + alignment.toUpperCase(), type, "", a.fqn, b.fqn, a.pkg.equals(b.pkg) ? "nesting changed" : "package " + a.pkg + " -> " + b.pkg));
        }
        d.kind = new Pair2(a.kind, b.kind);
        d.visibility = new Pair2(a.visibility, b.visibility);
        d.modifiers = new Pair2(mods(a), mods(b));
        d.lombok.addAll(a.lombok);
        if (!d.kind.same()) {
            gaps.add(new Gap("TYPE.KIND_" + a.kind.toUpperCase() + "_TO_" + b.kind.toUpperCase(), type, "", a.kind, b.kind, ""));
            sc.add("kind", W_KIND);
        }
        if (!d.visibility.same()) {
            gaps.add(new Gap(rank(b.visibility) > rank(a.visibility) ? "TYPE.VISIBILITY_WIDENED" : "TYPE.VISIBILITY_NARROWED", type, "",
                a.visibility, b.visibility, ""));
            sc.add("visibility", W_TYPE_VIS);
        }
        boolean implicitFinal = Set.of("record", "enum").contains(a.kind) || Set.of("record", "enum").contains(b.kind);
        if (a.isFinal != b.isFinal && !implicitFinal) {
            gaps.add(new Gap(a.isFinal ? "TYPE.FINAL_LOST" : "TYPE.FINAL_ADDED", type, "", mods(a), mods(b), ""));
            sc.add("modifiers", W_MODIFIER);
        }
        if (a.outer != null && b.outer != null && a.isStatic != b.isStatic) {
            gaps.add(new Gap(a.isStatic ? "TYPE.STATIC_LOST" : "TYPE.STATIC_ADDED", type, "", mods(a), mods(b), ""));
            sc.add("modifiers", W_MODIFIER);
        }
        if (!a.typeParameters.isEmpty() && b.typeParameters.size() < a.typeParameters.size()) {
            gaps.add(new Gap("GENERICS.TYPE_PARAMETERS_LOST", type, "", "<" + String.join(",", a.typeParameters) + ">",
                b.typeParameters.isEmpty() ? "" : "<" + String.join(",", b.typeParameters) + ">", "type"));
            sc.add("generics", W_SIGNATURE);
        }
        if (a.isSealed != b.isSealed) {
            gaps.add(new Gap(a.isSealed ? "TYPE.SEALED_LOST" : "TYPE.SEALED_ADDED", type, "", mods(a), mods(b), ""));
            sc.add("modifiers", W_MODIFIER);
        }

        // supertypes, by erasure
        d.supertypesOriginal.addAll(a.supertypes());
        d.supertypesModern.addAll(b.supertypes());
        Map<String, String> sa = new TreeMap<>();
        Map<String, String> sb = new TreeMap<>();
        for (String x : a.supertypes()) sa.put(JavaModel.erase(x), x);
        for (String x : b.supertypes()) sb.put(JavaModel.erase(x), x);
        for (Map.Entry<String, String> e : sa.entrySet()) {
            String other = sb.get(e.getKey());
            if (other == null) {
                d.supertypesLost.add(e.getValue());
                boolean ext = a.extendsTypes.contains(e.getValue()) && !a.kind.equals("interface");
                String code = ext ? "SUPERTYPE.EXTENDS_LOST" : JDK_INTERFACES.contains(e.getKey()) ? "SUPERTYPE.JDK_INTERFACE_LOST" : "SUPERTYPE.INTERFACE_LOST";
                gaps.add(new Gap(code, type, "", e.getValue(), "", ""));
                sc.add("supertypes", W_SUPER_LOST);
            } else if (!other.equals(e.getValue())) {
                gaps.add(new Gap("SUPERTYPE.ARGS_CHANGED", type, "", e.getValue(), other, typeDelta(e.getValue(), other)));
                sc.add("supertypes", W_SUPER_ADDED);
            }
        }
        for (Map.Entry<String, String> e : sb.entrySet()) {
            if (!sa.containsKey(e.getKey())) {
                d.supertypesAdded.add(e.getValue());
                gaps.add(new Gap("SUPERTYPE.ADDED", type, "", "", e.getValue(), ""));
                sc.add("supertypes", W_SUPER_ADDED);
            }
        }

        boolean api = a.effectivelyPublic;
        boolean modernApi = b.effectivelyPublic;
        Map<String, Integer> names = d.names;

        // fields
        d.fields.original = a.fields.size();
        d.fields.modern = b.fields.size();
        List<int[]> fp = alignNames(a.fields.stream().map(f -> f.name).toList(), a.fields.stream().map(f -> JavaModel.erase(f.type)).toList(),
            b.fields.stream().map(f -> f.name).toList(), b.fields.stream().map(f -> JavaModel.erase(f.type)).toList());
        boolean[] fUsedA = new boolean[a.fields.size()];
        boolean[] fUsedB = new boolean[b.fields.size()];
        for (int[] p : fp) {
            Field x = a.fields.get(p[0]);
            Field y = b.fields.get(p[1]);
            fUsedA[p[0]] = true;
            fUsedB[p[1]] = true;
            d.fields.matched++;
            List<String> ch = new ArrayList<>();
            name("field", x.name, y.name, JavaModel.erase(y.type), type, x.name, d, gaps, s, sc, W_MEMBER_NAME);
            if (!x.type.equals(y.type)) {
                String delta = known(typeDelta(x.type, y.type, origBySimple), x.type);
                ch.add("type " + x.type + " -> " + y.type);
                gaps.add(new Gap("TYPEREF." + delta, type, x.name, x.type, y.type, "field"));
                sc.add("field types", W_FIELD_TYPE);
            }
            if (x.isFinal != y.isFinal) {
                ch.add(x.isFinal ? "final lost" : "final added");
                String setter = "set" + JavaModel.cap(x.name);
                boolean setterUnreached = a.methods.stream().anyMatch(q -> q.name.equals(setter) && q.params.size() == 1 && !reach.reached(a.name, q));
                gaps.add(new Gap(x.isFinal ? "FIELD.FINAL_LOST" : setterUnreached ? "FIELD.FINAL_ADDED_SETTER_UNREACHED" : "FIELD.FINAL_ADDED",
                    type, x.name, "", "", ""));
                sc.add("field flags", W_FIELD_FLAG);
            }
            if (x.isStatic != y.isStatic) {
                ch.add(x.isStatic ? "static lost" : "static added");
                gaps.add(new Gap("FIELD.STATIC_CHANGED", type, x.name, x.isStatic ? "static" : "instance", y.isStatic ? "static" : "instance", ""));
                sc.add("field flags", W_FIELD_FLAG);
            }
            if (!x.visibility.equals(y.visibility)) {
                ch.add("visibility " + x.visibility + " -> " + y.visibility);
                gaps.add(new Gap((rank(y.visibility) > rank(x.visibility) ? "MEMBER.VISIBILITY_WIDENED_" : "MEMBER.VISIBILITY_NARROWED_")
                    + (x.isStatic ? "STATIC_FIELD" : "FIELD"), type, x.name, x.visibility, y.visibility, "field"));
                sc.add("field flags", W_FIELD_FLAG);
            }
            if (!ch.isEmpty() || !x.name.equals(y.name)) {
                if (!x.name.equals(y.name)) ch.add(0, "name " + x.name + " -> " + y.name);
                d.fields.changed.add(new Change(fieldText(x), fieldText(y), ch));
            }
            if (api && isApi(x.visibility)) {
                api(s, d, modernApi && ch.isEmpty(), true);
            }
        }
        for (int i = 0; i < a.fields.size(); i++) {
            if (fUsedA[i]) continue;
            Field x = a.fields.get(i);
            d.fields.missing.add(fieldText(x));
            String fcode = x.name.equals("serialVersionUID") ? "FIELD.MISSING_SERIAL_VERSION" : x.isStatic && x.isFinal ? "FIELD.MISSING_CONSTANT" : "FIELD.MISSING";
            gaps.add(new Gap(fcode, type, x.name, fieldText(x), "", x.origin));
            sc.add("missing fields", W_FIELD_MISSING);
            if (api && isApi(x.visibility)) {
                api(s, d, false, true);
            }
        }
        for (int i = 0; i < b.fields.size(); i++) {
            if (fUsedB[i]) continue;
            Field y = b.fields.get(i);
            d.fields.extra.add(fieldText(y));
            gaps.add(new Gap(y.isStatic && y.isFinal ? "FIELD.EXTRA_CONSTANT" : "FIELD.EXTRA", type, y.name, "", fieldText(y), y.origin));
            sc.add("extra fields", W_FIELD_EXTRA);
        }

        // enum constants
        d.enumConstants.original = a.enumConstants.size();
        d.enumConstants.modern = b.enumConstants.size();
        List<String> none = java.util.Collections.nCopies(Math.max(a.enumConstants.size(), b.enumConstants.size()), "");
        List<int[]> cp = alignNames(a.enumConstants, none.subList(0, a.enumConstants.size()), b.enumConstants,
            none.subList(0, b.enumConstants.size()), false);
        boolean[] cUsedA = new boolean[a.enumConstants.size()];
        boolean[] cUsedB = new boolean[b.enumConstants.size()];
        for (int[] p : cp) {
            cUsedA[p[0]] = cUsedB[p[1]] = true;
            String on = a.enumConstants.get(p[0]);
            String mn = b.enumConstants.get(p[1]);
            d.enumConstants.matched++;
            name("constant", on, mn, null, type, on, d, gaps, s, sc, W_MEMBER_NAME);
            if (api) api(s, d, on.equals(mn) && modernApi, true);
        }
        for (int i = 0; i < a.enumConstants.size(); i++) {
            if (cUsedA[i]) continue;
            String c = a.enumConstants.get(i);
            d.enumConstants.missing.add(c);
            gaps.add(new Gap("ENUM.CONSTANT_MISSING", type, c, c, "", ""));
            sc.add("enum constants", W_CONSTANT_MISSING);
            if (api) api(s, d, false, true);
        }
        for (int i = 0; i < b.enumConstants.size(); i++) {
            if (cUsedB[i]) continue;
            String c = b.enumConstants.get(i);
            d.enumConstants.extra.add(c);
            gaps.add(new Gap("ENUM.CONSTANT_EXTRA", type, c, "", c, ""));
            sc.add("enum constants", W_CONSTANT_EXTRA);
        }

        // methods
        d.methods.original = a.methods.size();
        d.methods.modern = b.methods.size();
        List<Method[]> pairs = alignMethods(a.methods, b.methods, type, gaps, x -> reach.reached(a.name, x));
        Set<Method> pa = java.util.Collections.newSetFromMap(new java.util.IdentityHashMap<>());
        Set<Method> pb = java.util.Collections.newSetFromMap(new java.util.IdentityHashMap<>());
        for (Method[] p : pairs) {
            Method x = p[0];
            Method y = p[1];
            pa.add(x);
            pb.add(y);
            d.methods.matched++;
            List<String> ch = new ArrayList<>();
            String member = x.key();
            if (!x.name.equals(y.name)) name("method", x.name, y.name, null, type, member, d, gaps, s, sc, W_MEMBER_NAME);
            else count(names, s, "method", Names.Kind.EXACT);
            boolean sigSame = x.name.equals(y.name) && x.paramTypes().equals(y.paramTypes()) && java.util.Objects.equals(x.returnType, y.returnType)
                && x.isStatic == y.isStatic;
            if (x.params.size() != y.params.size()) {
                ch.add("arity " + x.params.size() + " -> " + y.params.size());
                gaps.add(new Gap("SIGNATURE.ARITY_CHANGED", type, member, x.signature(), y.signature(), ""));
                sc.add("signatures", W_SIGNATURE);
            } else {
                boolean typesChanged = false;
                for (int i = 0; i < x.params.size(); i++) {
                    String ot = x.params.get(i).type;
                    String mt = y.params.get(i).type;
                    if (!ot.equals(mt)) {
                        typesChanged = true;
                        gaps.add(new Gap("TYPEREF." + known(typeDelta(ot, mt, origBySimple), ot), type, member, ot, mt, "param " + (i + 1)));
                    }
                    name("param", x.params.get(i).name, y.params.get(i).name, JavaModel.erase(mt), type, member, d, gaps, s, sc, W_LOCAL_NAME);
                }
                if (typesChanged) {
                    ch.add("parameter types " + x.paramTypes() + " -> " + y.paramTypes());
                    sc.add("signatures", W_SIGNATURE);
                }
            }
            if (!x.ctor && !java.util.Objects.equals(x.returnType, y.returnType)) {
                ch.add("return " + x.returnType + " -> " + y.returnType);
                gaps.add(new Gap("TYPEREF." + known(typeDelta(x.returnType, y.returnType, origBySimple), x.returnType), type, member, x.returnType, y.returnType, "return"));
                sc.add("signatures", W_SIGNATURE);
            }
            if (x.isStatic != y.isStatic) {
                ch.add(x.isStatic ? "static lost" : "static added");
                gaps.add(new Gap("MEMBER.STATIC_CHANGED", type, member, x.isStatic ? "static" : "instance", y.isStatic ? "static" : "instance", "method"));
                sc.add("member flags", W_MEMBER_FLAG);
            }
            if (!x.visibility.equals(y.visibility)) {
                ch.add("visibility " + x.visibility + " -> " + y.visibility);
                gaps.add(new Gap((rank(y.visibility) > rank(x.visibility) ? "MEMBER.VISIBILITY_WIDENED_" : "MEMBER.VISIBILITY_NARROWED_")
                    + (x.ctor ? "CTOR" : "METHOD"), type, member, x.visibility, y.visibility, x.ctor ? "constructor" : "method"));
                sc.add("member flags", W_MEMBER_FLAG);
            }
            if (!x.typeParameters.isEmpty() && y.typeParameters.size() < x.typeParameters.size()) {
                gaps.add(new Gap("GENERICS.TYPE_PARAMETERS_LOST", type, member, "<" + String.join(",", x.typeParameters) + ">",
                    y.typeParameters.isEmpty() ? "" : "<" + String.join(",", y.typeParameters) + ">", x.ctor ? "constructor" : "method"));
                sc.add("generics", W_MEMBER_FLAG);
            }
            if (y.deadStub) {
                gaps.add(new Gap("METHOD.DEAD_STUB", type, member, x.signature(), y.signature(), "body is a JX-DEAD throw"));
            }
            alignLocals(x, y, type, member, d, gaps, s, sc);
            if (!ch.isEmpty()) d.methods.changed.add(new Change(x.signature(), y.signature(), ch));
            s.signatures.add(sigSame);
            s.signaturesReached.add(sigSame);
            d.signatures.add(sigSame);
            if (api && isApi(x.visibility)) {
                boolean ok = modernApi && sigSame && x.visibility.equals(y.visibility);
                api(s, d, ok, true);
            }
        }
        for (Method x : a.methods) {
            if (pa.contains(x)) continue;
            d.methods.missing.add(x.signature() + (x.origin.equals("source") ? "" : " [" + x.origin + "]"));
            boolean reached = reach.reached(a.name, x);
            String sub = missingKind(x);
            gaps.add(new Gap((reached ? "METHOD.MISSING_" : "METHOD.UNREACHED_") + sub, type, x.key(), x.signature(), "", x.origin + " " + x.visibility));
            if (reached) sc.add("missing methods", api && isApi(x.visibility) ? W_METHOD_MISSING_API : W_METHOD_MISSING);
            s.signatures.add(false);
            if (reached) s.signaturesReached.add(false);
            d.signatures.add(false);
            if (api && isApi(x.visibility)) api(s, d, false, reached);
        }
        for (Method y : b.methods) {
            if (pb.contains(y)) continue;
            d.methods.extra.add(y.signature() + (y.origin.equals("source") ? "" : " [" + y.origin + "]"));
            String sub = y.origin.equals("record") ? "RECORD" : y.ctor ? "CTOR" : isAccessor(y) ? "ACCESSOR"
                : y.visibility.equals("private") ? "PRIVATE" : isApi(y.visibility) ? "API" : "PACKAGE";
            gaps.add(new Gap("METHOD.EXTRA_" + sub, type, y.key(), "", y.signature(), y.origin + " " + y.visibility));
            sc.add("extra methods", y.visibility.equals("private") ? W_METHOD_EXTRA_PRIVATE : W_METHOD_EXTRA);
        }

        // constructors, factories, builder, object methods
        for (Method x : a.methods) if (x.ctor) d.constructorsOriginal.add(x.visibility + " " + x.signature() + originTag(x));
        for (Method y : b.methods) if (y.ctor) d.constructorsModern.add(y.visibility + " " + y.signature() + originTag(y));
        for (Method x : a.methods) if (isFactory(x, a)) d.factoriesOriginal.add(x.signature());
        for (Method y : b.methods) if (isFactory(y, b)) d.factoriesModern.add(y.signature());
        d.builder = new Pair2(builderText(a), builderText(b));
        if (a.builder && !b.builder) {
            boolean builderReached = !reach.known || a.methods.stream().anyMatch(x -> x.name.equals("builder") && x.isStatic && reach.reached(a.name, x))
                || reach.methods.keySet().stream().anyMatch(k -> k.equals(a.name + "Builder") || k.equals("Builder") && a.builder && !a.lombok.contains("Builder"));
            gaps.add(new Gap(builderReached ? "BUILDER.LOST" : "BUILDER.UNREACHED", type, "", builderText(a), builderText(b), String.join(",", a.lombok)));
            if (builderReached) sc.add("builder", W_BUILDER);
        }
        for (String om : List.of("equals", "hashCode", "toString")) {
            d.objectMethods.put(om, new Pair2(objectMethod(a, om), objectMethod(b, om)));
        }

        // idioms: the original side leaves out methods with no modern counterpart (J3S never compiled them)
        Map<String, Integer> ai = new TreeMap<>(a.idioms);
        Map<String, Integer> au = new TreeMap<>(a.typeUses);
        for (Method x : a.methods) {
            if (pa.contains(x)) continue;
            x.idioms.forEach((k, v) -> ai.merge(k, -v, Integer::sum));
            x.typeUses.forEach((k, v) -> au.merge(k, -v, Integer::sum));
        }
        Set<String> fams = new TreeSet<>(a.idioms.keySet());
        fams.addAll(b.idioms.keySet());
        for (String f : fams) {
            int x = ai.getOrDefault(f, 0);
            int y = b.idioms.getOrDefault(f, 0);
            Pair p = new Pair(x, y);
            p.originalAll = a.idioms.getOrDefault(f, 0);
            d.idioms.put(f, p);
            if (x > y && SCORED_IDIOMS.contains(f)) {
                gaps.add(new Gap("IDIOM." + f.toUpperCase() + "_LOST", type, "", String.valueOf(x), String.valueOf(y), "-" + (x - y)));
                sc.add("idioms", Math.min(IDIOM_CAP, W_IDIOM * (x - y)));
            }
        }
        Set<String> uses = new TreeSet<>(a.typeUses.keySet());
        uses.addAll(b.typeUses.keySet());
        for (String u : uses) {
            Pair p = new Pair(au.getOrDefault(u, 0), b.typeUses.getOrDefault(u, 0));
            p.originalAll = a.typeUses.getOrDefault(u, 0);
            d.typeUses.put(u, p);
        }
        int streamsLost = Math.max(0, ai.getOrDefault("stream", 0) + ai.getOrDefault("lambda", 0) + ai.getOrDefault("methodRef", 0)
            - b.idioms.getOrDefault("stream", 0) - b.idioms.getOrDefault("lambda", 0) - b.idioms.getOrDefault("methodRef", 0));
        int loopsAdded = b.idioms.getOrDefault("loop", 0) - ai.getOrDefault("loop", 0);
        if (streamsLost > 0 && loopsAdded > 0) {
            gaps.add(new Gap("IDIOM.STREAM_TO_LOOP", type, "", "streams+lambdas+method refs -" + streamsLost, "loops +" + loopsAdded, ""));
        }
        int msgLost = ai.getOrDefault("exceptionWithArgs", 0) - b.idioms.getOrDefault("exceptionWithArgs", 0);
        if (msgLost > 0 && b.idioms.getOrDefault("exceptionBare", 0) > ai.getOrDefault("exceptionBare", 0)) {
            gaps.add(new Gap("IDIOM.EXCEPTION_MESSAGE_LOST", type, "", "with message " + ai.getOrDefault("exceptionWithArgs", 0),
                "with message " + b.idioms.getOrDefault("exceptionWithArgs", 0) + ", bare " + b.idioms.getOrDefault("exceptionBare", 0), ""));
            sc.add("idioms", Math.min(IDIOM_CAP, W_IDIOM * msgLost));
        }
        int optLost = ai.getOrDefault("optional", 0) - b.idioms.getOrDefault("optional", 0);
        int nullsAdded = b.idioms.getOrDefault("returnNull", 0) + b.idioms.getOrDefault("nullCheck", 0)
            - ai.getOrDefault("returnNull", 0) - ai.getOrDefault("nullCheck", 0);
        if (optLost > 0 && nullsAdded > 0) {
            gaps.add(new Gap("IDIOM.OPTIONAL_TO_NULL", type, "", "Optional -" + optLost, "null returns/checks +" + nullsAdded, ""));
        }

        d.originalStructure = typeStructure(a);
        d.modernStructure = typeStructure(b);
        d.distance = round1(sc.total);
        int members = Math.max(1, a.fields.size() + a.methods.size() + a.enumConstants.size());
        d.distancePerMember = Math.round(100.0 * sc.total / members) / 100.0;
        d.api.close();
        d.signatures.close();
        d.renames.sort(Comparator.comparing(Rename::element).thenComparing(Rename::member).thenComparing(Rename::original));
        d.fields.missing.sort(null);
        d.fields.extra.sort(null);
        d.methods.missing.sort(null);
        d.methods.extra.sort(null);
        d.methods.changed.sort(Comparator.comparing(Change::original));
        d.fields.changed.sort(Comparator.comparing(Change::original));
        d.constructorsOriginal.sort(null);
        d.constructorsModern.sort(null);
        d.factoriesOriginal.sort(null);
        d.factoriesModern.sort(null);
        topReasons.put(d, sc.top());
        return d;
    }

    private static void api(CompareReport.Summary s, TypeDiff d, boolean ok, boolean reached) {
        s.api.add(ok);
        if (reached) s.apiReached.add(ok);
        d.api.add(ok);
    }

    private static String originTag(Method x) {
        return x.origin.equals("source") ? "" : " [" + x.origin + "]";
    }

    private static String builderText(TypeModel t) {
        if (!t.builder) return "none";
        return t.lombok.contains("Builder") || t.lombok.contains("SuperBuilder") ? "lombok @Builder" : "explicit";
    }

    private static String objectMethod(TypeModel t, String name) {
        for (Method x : t.methods) {
            if (!x.ctor && x.name.equals(name) && x.params.size() == (name.equals("equals") ? 1 : 0)) {
                return x.origin.equals("source") ? "explicit" : x.origin;
            }
        }
        return "none";
    }

    static boolean isFactory(Method x, TypeModel t) {
        if (x.ctor || !x.isStatic || x.returnType == null) return false;
        String r = JavaModel.erase(x.returnType);
        return r.equals(t.name) || x.returnType.equals("Optional<" + t.name + ">");
    }

    static boolean isAccessor(Method x) {
        if (x.ctor) return false;
        return x.params.isEmpty() && x.name.matches("(get|is)[A-Z].*") && !"void".equals(x.returnType)
            || x.params.size() == 1 && x.name.matches("set[A-Z].*");
    }

    private static String missingKind(Method x) {
        if (x.origin.equals("lombok")) return "LOMBOK";
        if (x.origin.equals("record")) return "RECORD";
        if (!x.ctor && (x.name.equals("equals") && x.params.size() == 1 || x.name.equals("hashCode") && x.params.isEmpty()
            || x.name.equals("toString") && x.params.isEmpty())) return "OBJECT";
        if (x.ctor) return "CTOR";
        if (isAccessor(x)) return "ACCESSOR";
        if (x.visibility.equals("private")) return "PRIVATE";
        return isApi(x.visibility) ? "API" : "PACKAGE";
    }

    private static String mods(TypeModel t) {
        List<String> m = new ArrayList<>();
        if (t.isFinal) m.add("final");
        if (t.isStatic) m.add("static");
        if (t.isSealed) m.add("sealed");
        return String.join(" ", m);
    }

    private static String fieldText(Field f) {
        return (f.isStatic ? "static " : "") + (f.isFinal ? "final " : "") + f.type + " " + f.name;
    }

    private static void count(Map<String, Integer> names, CompareReport.Summary s, String element, Names.Kind k) {
        names.merge(element + ":" + k.id(), 1, Integer::sum);
        s.nameClasses.computeIfAbsent(element, x -> new TreeMap<>()).merge(k.id(), 1, Integer::sum);
        s.names.add(k.exact());
    }

    private void name(String element, String on, String mn, String modernType, String type, String member, TypeDiff d, List<Gap> gaps,
        CompareReport.Summary s, Score sc, double weight) {
        Names.Kind k = Names.classify(on, mn, modernType);
        count(d.names, s, element, k);
        if (k.exact()) return;
        d.renames.add(new Rename(element, member, on, mn, k.id()));
        String detail = element;
        String code = "NAME." + k.name();
        if (k == Names.Kind.TRUNCATED || k == Names.Kind.TRUNCATED_NUMBERED) {
            String stem = k == Names.Kind.TRUNCATED ? mn : mn.replaceAll("_?\\d+_?$", "");
            String c = completion(stem, on);
            s.truncationCompletion.merge(c, 1, Integer::sum);
            detail = element + ", completion " + c;
            code += "." + c.toUpperCase(java.util.Locale.ROOT).replace('-', '_');
        }
        gaps.add(new Gap(code, type, member, on, mn, detail));
        sc.add("names", weight);
        if (k == Names.Kind.TRUNCATED) s.truncatedLengths.merge(mn.length(), 1, Integer::sum);
    }

    /** Every identifier the modern estate spells out (fields, methods and their property names, parameters, locals, constants). */
    private Set<String> vocabulary = new TreeSet<>();

    static Set<String> vocabulary(Iterable<TypeModel> types) {
        Set<String> v = new TreeSet<>();
        for (TypeModel t : types) {
            for (Field f : t.fields) v.add(f.name);
            v.addAll(t.enumConstants);
            for (Method x : t.methods) {
                if (!x.ctor) {
                    v.add(x.name);
                    var p = java.util.regex.Pattern.compile("^(?:get|set|is|with|has)([A-Z].*)$").matcher(x.name);
                    if (p.matches()) v.add(Character.toLowerCase(p.group(1).charAt(0)) + p.group(1).substring(1));
                }
                for (JavaModel.Param p : x.params) v.add(p.name);
                for (Local l : x.locals) v.add(l.name);
            }
        }
        return v;
    }

    /**
     * Whether the modern estate alone could complete a truncated stem: the identifiers that extend it. unique-correct:
     * exactly one, and it is the original; unique-wrong: exactly one, another; ambiguous-correct / ambiguous-wrong:
     * several, with or without the original; none: no identifier extends it.
     */
    String completion(String stem, String original) {
        List<String> c = new ArrayList<>();
        for (String w : vocabulary) {
            if (w.length() > stem.length() && Names.isTruncation(w, stem)) c.add(w);
        }
        if (c.isEmpty()) return "none";
        if (c.size() == 1) return c.get(0).equals(original) ? "unique-correct" : "unique-wrong";
        return c.contains(original) ? "ambiguous-correct" : "ambiguous-wrong";
    }

    private void alignLocals(Method x, Method y, String type, String member, TypeDiff d, List<Gap> gaps, CompareReport.Summary s, Score sc) {
        List<Local> a = x.locals;
        List<Local> b = y.locals;
        List<int[]> p = alignNames(a.stream().map(l -> l.name).toList(), a.stream().map(l -> JavaModel.erase(l.type)).toList(),
            b.stream().map(l -> l.name).toList(), b.stream().map(l -> JavaModel.erase(l.type)).toList());
        for (int[] q : p) {
            name("local", a.get(q[0]).name, b.get(q[1]).name, b.get(q[1]).type, type, member, d, gaps, s, sc, W_LOCAL_NAME);
        }
    }

    /**
     * Pairs original and modern names: exact; then a damaged form (case, truncated, abbreviated, truncated+numbered),
     * preferring the same type and the longest shared prefix; then, per erased type, the single remaining original
     * with the single remaining modern. Returns [originalIndex, modernIndex] pairs.
     */
    static List<int[]> alignNames(List<String> on, List<String> ot, List<String> mn, List<String> mt) {
        return alignNames(on, ot, mn, mt, true);
    }

    static List<int[]> alignNames(List<String> on, List<String> ot, List<String> mn, List<String> mt, boolean byType) {
        List<int[]> out = new ArrayList<>();
        boolean[] ua = new boolean[on.size()];
        boolean[] ub = new boolean[mn.size()];
        Map<String, List<Integer>> byName = new HashMap<>();
        for (int j = 0; j < mn.size(); j++) byName.computeIfAbsent(mn.get(j), k -> new ArrayList<>()).add(j);
        for (int i = 0; i < on.size(); i++) {
            List<Integer> c = byName.get(on.get(i));
            if (c == null) continue;
            for (int j : c) {
                if (!ub[j]) {
                    ua[i] = ub[j] = true;
                    out.add(new int[] {i, j});
                    break;
                }
            }
        }
        for (int i = 0; i < on.size(); i++) {
            if (ua[i]) continue;
            int best = -1;
            int bestScore = -1;
            boolean tie = false;
            for (int j = 0; j < mn.size(); j++) {
                if (ub[j]) continue;
                Names.Kind k = Names.classify(on.get(i), mn.get(j));
                if (k != Names.Kind.CASE && !k.damaged()) continue;
                int score = (k == Names.Kind.CASE ? 4000 : k == Names.Kind.TRUNCATED ? 3000 : k == Names.Kind.TRUNCATED_NUMBERED ? 2000 : 1000)
                    + (ot.get(i).equals(mt.get(j)) ? 500 : 0) + Math.min(499, commonPrefix(on.get(i), mn.get(j)));
                if (score > bestScore) {
                    best = j;
                    bestScore = score;
                    tie = false;
                } else if (score == bestScore) {
                    tie = true;
                }
            }
            if (best >= 0 && !tie) {
                ua[i] = ub[best] = true;
                out.add(new int[] {i, best});
            }
        }
        Map<String, List<Integer>> ra = new TreeMap<>();
        Map<String, List<Integer>> rb = new TreeMap<>();
        for (int i = 0; i < on.size(); i++) if (!ua[i]) ra.computeIfAbsent(ot.get(i), k -> new ArrayList<>()).add(i);
        for (int j = 0; j < mn.size(); j++) if (!ub[j]) rb.computeIfAbsent(mt.get(j), k -> new ArrayList<>()).add(j);
        for (Map.Entry<String, List<Integer>> e : ra.entrySet()) {
            List<Integer> c = rb.get(e.getKey());
            if (byType && e.getValue().size() == 1 && c != null && c.size() == 1) out.add(new int[] {e.getValue().get(0), c.get(0)});
        }
        out.sort(Comparator.comparingInt((int[] q) -> q[0]));
        return out;
    }

    private static int commonPrefix(String a, String b) {
        int n = Math.min(a.length(), b.length());
        int i = 0;
        while (i < n && Character.toLowerCase(a.charAt(i)) == Character.toLowerCase(b.charAt(i))) i++;
        return i;
    }

    /**
     * Pairs methods: same name and erased parameter types; then same name and arity (declaration order); then, for a
     * name left only on the original side, a modern name only on the modern side that is a damaged form of it with the
     * same arity. Overloads the modern side has fewer of are reported as collapsed.
     */
    static List<Method[]> alignMethods(List<Method> a, List<Method> b, String type, List<Gap> gaps, java.util.function.Predicate<Method> reached) {
        List<Method[]> out = new ArrayList<>();
        Set<Method> ua = java.util.Collections.newSetFromMap(new java.util.IdentityHashMap<>());
        Set<Method> ub = java.util.Collections.newSetFromMap(new java.util.IdentityHashMap<>());
        Map<String, List<Method>> an = new TreeMap<>();
        Map<String, List<Method>> bn = new TreeMap<>();
        for (Method x : a) an.computeIfAbsent(x.name, k -> new ArrayList<>()).add(x);
        for (Method y : b) bn.computeIfAbsent(y.name, k -> new ArrayList<>()).add(y);
        for (Map.Entry<String, List<Method>> e : an.entrySet()) {
            List<Method> ys = bn.get(e.getKey());
            if (ys == null) continue;
            for (Method x : e.getValue()) {
                for (Method y : ys) {
                    if (!ub.contains(y) && y.erasedParamTypes().equals(x.erasedParamTypes())) {
                        out.add(new Method[] {x, y});
                        ua.add(x);
                        ub.add(y);
                        break;
                    }
                }
            }
            for (Method x : e.getValue()) {
                if (ua.contains(x)) continue;
                for (Method y : ys) {
                    if (!ub.contains(y) && y.params.size() == x.params.size()) {
                        out.add(new Method[] {x, y});
                        ua.add(x);
                        ub.add(y);
                        break;
                    }
                }
            }
            long leftA = e.getValue().stream().filter(x -> !ua.contains(x)).count();
            long leftB = ys.stream().filter(y -> !ub.contains(y)).count();
            long reachedOverloads = e.getValue().stream().filter(reached).count();
            if (reachedOverloads > ys.size() && ys.size() > 0 && !e.getKey().equals("<init>")) {
                gaps.add(new Gap("OVERLOAD.COLLAPSED", type, e.getKey(), reachedOverloads + " reached overloads (" + e.getValue().size() + " in all)",
                    ys.size() + " overloads", ""));
            }
            if (leftA > 0 && leftB > 0) {
                for (Method x : e.getValue()) {
                    if (ua.contains(x)) continue;
                    for (Method y : ys) {
                        if (!ub.contains(y)) {
                            out.add(new Method[] {x, y});
                            ua.add(x);
                            ub.add(y);
                            break;
                        }
                    }
                }
            }
        }
        // damaged method names
        for (Map.Entry<String, List<Method>> e : an.entrySet()) {
            if (bn.containsKey(e.getKey()) || e.getKey().equals("<init>")) continue;
            for (Method x : e.getValue()) {
                if (ua.contains(x)) continue;
                Method best = null;
                boolean tie = false;
                for (Map.Entry<String, List<Method>> f : bn.entrySet()) {
                    if (an.containsKey(f.getKey())) continue;
                    Names.Kind k = Names.classify(x.name, f.getKey());
                    if (k != Names.Kind.CASE && k != Names.Kind.ACCESSOR_STYLE && !k.damaged()) continue;
                    for (Method y : f.getValue()) {
                        if (ub.contains(y) || y.params.size() != x.params.size()) continue;
                        if (best != null) tie = true;
                        best = y;
                    }
                }
                if (best != null && !tie) {
                    out.add(new Method[] {x, best});
                    ua.add(x);
                    ub.add(best);
                }
            }
        }
        return out;
    }

    /** Simple names of the modern estate's types: a type the estate declares is known to the generator. */
    private Set<String> modernSimple = Set.of();
    private static final Set<String> JDK = Set.of("String", "Integer", "Long", "Short", "Byte", "Double", "Float", "Boolean", "Character",
        "Object", "Number", "CharSequence", "StringBuilder", "Pattern", "Matcher", "Class", "Enum", "Comparable", "Serializable", "Exception",
        "RuntimeException", "Throwable");

    /** TO_OBJECT and GENERIC_TO_OBJECT, split by whether the original type (or its type arguments) is known to the estate or the JDK. */
    private String known(String delta, String originalType) {
        if (!delta.equals("TO_OBJECT") && !delta.equals("GENERIC_TO_OBJECT")) return delta;
        List<String> names = new ArrayList<>();
        var m = java.util.regex.Pattern.compile("[A-Z][A-Za-z0-9_]*").matcher(originalType);
        while (m.find()) names.add(m.group());
        if (delta.equals("GENERIC_TO_OBJECT") && !names.isEmpty()) names.remove(0);
        boolean allKnown = names.stream().allMatch(n -> modernSimple.contains(n) || JDK.contains(n) || JavaModel.WATCHED.containsKey(n)
            || n.matches("[A-Z][A-Z0-9]?"));
        return delta + (allKnown ? "_KNOWN" : "_EXTERNAL");
    }

    /** A mechanical class for a changed type reference; used as the TYPEREF.* gap code. */
    static String typeDelta(String o, String m) {
        return typeDelta(o, m, Map.of());
    }

    /** As typeDelta(o, m); originals (by simple name) tells an interface or supertype replaced by its implementation. */
    static String typeDelta(String o, String m, Map<String, TypeModel> originals) {
        if (o.equals(m)) return "SAME";
        String eo = JavaModel.erase(o);
        String em = JavaModel.erase(m);
        if (o.endsWith("...") && m.equals(o.substring(0, o.length() - 3) + "[]")) return "VARARGS_TO_ARRAY";
        TypeModel impl = originals.get(em);
        if (impl != null && impl.supertypes().stream().map(JavaModel::erase).anyMatch(eo::equals)) {
            TypeModel sup = originals.get(eo);
            boolean iface = sup != null ? sup.kind.equals("interface")
                : impl.implementsTypes.stream().map(JavaModel::erase).anyMatch(eo::equals) || impl.kind.equals("interface");
            return iface ? "INTERFACE_TO_IMPL" : "SUPERTYPE_TO_SUBTYPE";
        }
        if (eo.equals(em)) {
            if (!m.contains("<")) return "GENERIC_DROPPED";
            if (m.contains("Object") && !o.contains("Object")) return "GENERIC_TO_OBJECT";
            return "GENERIC_CHANGED";
        }
        if (em.equals("Object")) return "TO_OBJECT";
        if (boxed(eo).equals(boxed(em))) return "BOXING";
        if (eo.equals("Optional")) {
            String inner = o.substring(o.indexOf('<') + 1, o.lastIndexOf('>'));
            if (JavaModel.erase(inner).equals(em)) return "OPTIONAL_UNWRAPPED";
            return "OPTIONAL_LOST";
        }
        String fo = JavaModel.WATCHED.get(eo);
        String fm = JavaModel.WATCHED.get(em);
        if (fo != null && fm != null && COLLECTION_FAMILIES.contains(fo) && COLLECTION_FAMILIES.contains(fm)) return "COLLECTION_CHANGED";
        if (eo.endsWith("[]") && fm != null && COLLECTION_FAMILIES.contains(fm) || em.endsWith("[]") && fo != null && COLLECTION_FAMILIES.contains(fo)) {
            return "ARRAY_COLLECTION";
        }
        if ("functional".equals(fo)) return "FUNCTIONAL_LOST";
        if (numeric(eo) && numeric(em)) return "NUMERIC_CHANGED";
        return "OTHER";
    }

    private static boolean numeric(String t) {
        return Set.of("int", "long", "short", "byte", "double", "float", "Integer", "Long", "Short", "Double", "Float", "BigDecimal", "BigInteger")
            .contains(t);
    }

    private static String boxed(String t) {
        return switch (t) {
            case "int" -> "Integer";
            case "long" -> "Long";
            case "boolean" -> "Boolean";
            case "double" -> "Double";
            case "float" -> "Float";
            case "char" -> "Character";
            case "short" -> "Short";
            case "byte" -> "Byte";
            default -> t;
        };
    }

    private static CompareReport.TypeStructure typeStructure(TypeModel t) {
        CompareReport.TypeStructure s = new CompareReport.TypeStructure();
        s.loc = t.loc;
        s.branches = t.branches;
        List<Method> src = t.sourceMethods();
        s.methods = src.size();
        int sum = 0;
        for (Method x : src) {
            sum += x.lines;
            s.maxMethodLines = Math.max(s.maxMethodLines, x.lines);
        }
        s.avgMethodLines = src.isEmpty() ? 0 : round1((double) sum / src.size());
        return s;
    }

    private static void merge(Map<String, Pair> into, Map<String, Pair> from) {
        from.forEach((k, v) -> {
            Pair p = into.computeIfAbsent(k, x -> new Pair());
            p.original += v.original;
            p.modern += v.modern;
            p.originalAll += v.originalAll;
        });
    }

    private static void structure(TypeModel t, CompareReport.Structure s) {
        s.types++;
        s.loc += t.loc;
        s.branches += t.branches;
        for (Method x : t.sourceMethods()) {
            s.methods++;
            s.methodLines += x.lines;
            s.maxMethodLines = Math.max(s.maxMethodLines, x.lines);
        }
        for (Method x : t.methods) {
            if (x.ctor) s.constructors++;
            if (isFactory(x, t)) s.factories++;
        }
        if (t.builder) s.builders++;
        if (!objectMethod(t, "equals").equals("none") && !objectMethod(t, "hashCode").equals("none")) s.equalsHashCode++;
        if (!objectMethod(t, "toString").equals("none")) s.toStrings++;
    }

    private static void close(CompareReport.Structure s) {
        s.avgMethodLines = s.methods == 0 ? 0 : round1((double) s.methodLines / s.methods);
    }

    static String read(Path p) throws IOException {
        byte[] bytes = Files.readAllBytes(p);
        try {
            return StandardCharsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT).onUnmappableCharacter(CodingErrorAction.REPORT)
                .decode(ByteBuffer.wrap(bytes)).toString();
        } catch (CharacterCodingException e) {
            return new String(bytes, StandardCharsets.ISO_8859_1);
        }
    }
}
