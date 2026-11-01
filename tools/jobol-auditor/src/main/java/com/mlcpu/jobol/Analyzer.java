package com.mlcpu.jobol;

import com.github.javaparser.Range;
import com.github.javaparser.ast.CompilationUnit;
import com.github.javaparser.ast.ImportDeclaration;
import com.github.javaparser.ast.Modifier;
import com.github.javaparser.ast.Node;
import com.github.javaparser.ast.body.AnnotationDeclaration;
import com.github.javaparser.ast.body.BodyDeclaration;
import com.github.javaparser.ast.body.ClassOrInterfaceDeclaration;
import com.github.javaparser.ast.body.EnumDeclaration;
import com.github.javaparser.ast.body.FieldDeclaration;
import com.github.javaparser.ast.body.MethodDeclaration;
import com.github.javaparser.ast.body.Parameter;
import com.github.javaparser.ast.body.RecordDeclaration;
import com.github.javaparser.ast.body.TypeDeclaration;
import com.github.javaparser.ast.body.VariableDeclarator;
import com.github.javaparser.ast.comments.Comment;
import com.github.javaparser.ast.expr.AnnotationExpr;
import com.github.javaparser.ast.expr.AssignExpr;
import com.github.javaparser.ast.expr.BinaryExpr;
import com.github.javaparser.ast.expr.ConditionalExpr;
import com.github.javaparser.ast.expr.Expression;
import com.github.javaparser.ast.expr.FieldAccessExpr;
import com.github.javaparser.ast.expr.IntegerLiteralExpr;
import com.github.javaparser.ast.expr.LambdaExpr;
import com.github.javaparser.ast.expr.MethodCallExpr;
import com.github.javaparser.ast.expr.MethodReferenceExpr;
import com.github.javaparser.ast.expr.NameExpr;
import com.github.javaparser.ast.expr.StringLiteralExpr;
import com.github.javaparser.ast.expr.SwitchExpr;
import com.github.javaparser.ast.expr.TextBlockLiteralExpr;
import com.github.javaparser.ast.expr.UnaryExpr;
import com.github.javaparser.ast.stmt.BlockStmt;
import com.github.javaparser.ast.stmt.BreakStmt;
import com.github.javaparser.ast.stmt.CatchClause;
import com.github.javaparser.ast.stmt.DoStmt;
import com.github.javaparser.ast.stmt.ExpressionStmt;
import com.github.javaparser.ast.stmt.ForEachStmt;
import com.github.javaparser.ast.stmt.ForStmt;
import com.github.javaparser.ast.stmt.IfStmt;
import com.github.javaparser.ast.stmt.LabeledStmt;
import com.github.javaparser.ast.stmt.ReturnStmt;
import com.github.javaparser.ast.stmt.Statement;
import com.github.javaparser.ast.stmt.SwitchEntry;
import com.github.javaparser.ast.stmt.SwitchStmt;
import com.github.javaparser.ast.stmt.WhileStmt;
import com.github.javaparser.ast.type.ClassOrInterfaceType;
import com.github.javaparser.ast.type.Type;
import com.github.javaparser.ast.type.VarType;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.IdentityHashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Optional;
import java.util.Set;
import java.util.TreeMap;
import java.util.TreeSet;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/** Applies the JOBOL rules to one parsed compilation unit and measures each of its types. */
final class Analyzer {
    /** Findings kept per rule and type; scoring caps are far lower, so the cut only bounds report size. */
    static final int EMIT_LIMIT = 25;

    private static final Set<String> FILE_STATUS = Set.of("00", "02", "04", "05", "07", "10", "14", "21", "22",
        "23", "24", "30", "34", "35", "37", "38", "39", "41", "42", "43", "44", "46", "47", "48", "49");
    private static final Set<String> COMPARISONS = Set.of("equals", "equalsIgnoreCase", "compare", "compareTo",
        "contentEquals");
    private static final Set<String> PERFORM_CALLS = Set.of("perform", "performThru", "performRange", "performUntil",
        "goTo", "goTo_");
    private static final Pattern PARAGRAPH_NAME = Pattern.compile(
        "^(?:p|para|sec)\\d+(?:_.*|[A-Z].*)?$|^(?:s|x)\\d{3,4}(?:_.*|[A-Z].*)?$|^.*_\\d{3,4}(?:_.*)?$|^[a-z]{0,2}\\d{4}[A-Z_].*$");
    private static final Pattern EDIT_MASK = Pattern.compile("^[Z9,.+\\-*$B0/]{3,}(?:CR|DB)?$");
    private static final Pattern PICTURE = Pattern.compile(
        "(?i)^(?:PIC(?:TURE)?\\s+)?S?[9XAZ]\\(\\d+\\)(?:V9*(?:\\(\\d+\\))?)?.*$|^S?9+V9+$");
    private static final Pattern FIXED_FORMAT = Pattern.compile("%-\\d+s|%0\\d+d|%\\d+s");
    private static final Pattern COBOL_COMMENT = Pattern.compile(
        "\\[[A-Z0-9-]+:\\d+:[A-Z-]+:L\\d+]|\\bMOVE\\s+\\S.*\\s+TO\\s+[A-Z0-9-]+|\\bPERFORM\\s+[A-Z0-9][A-Z0-9-]{2,}"
            + "|\\bEVALUATE\\s+[A-Z]|\\bCOMPUTE\\s+[A-Z0-9-]+\\s*=|\\bGO\\s+TO\\s+[A-Z0-9-]+");
    private static final Pattern CAMEL = Pattern.compile("(?<=[a-z0-9])(?=[A-Z])|_|(?<=[A-Z])(?=[A-Z][a-z])|(?<=[A-Za-z])(?=[0-9])");
    private static final Pattern TRAILING_NUMBER = Pattern.compile("^(.*?[A-Za-z])(\\d+)$");
    private static final Pattern UNDERSCORE_NUMBER = Pattern.compile("^.*[A-Za-z]_\\d+$");

    private final Config cfg;
    private final Set<String> runtimeTypes;
    private final Set<String> prefixes;
    private final Set<String> moneyWords;

    Analyzer(Config cfg) {
        this.cfg = cfg;
        this.runtimeTypes = Set.copyOf(cfg.legacyRuntimeTypes);
        this.prefixes = Set.copyOf(cfg.cobolPrefixes);
        this.moneyWords = Set.copyOf(cfg.moneyWords);
    }

    /** Per-type accumulator while walking the tree. */
    private final class Acc {
        final TypeDeclaration<?> type;
        final String name;
        final List<Finding> findings = new ArrayList<>();
        final Map<String, Integer> emitted = new TreeMap<>();
        final Map<String, Integer> signals = new TreeMap<>();
        int suppressed;
        int branches;

        Acc(TypeDeclaration<?> type, String name) {
            this.type = type;
            this.name = name;
        }

        void signal(String s) {
            signals.merge(s, 1, Integer::sum);
        }
    }

    private String file;
    private Zone zone;
    private Map<TypeDeclaration<?>, Acc> accs;
    private Acc primary;

    @SuppressWarnings({"rawtypes", "unchecked"})
    synchronized List<TypeReport> analyze(CompilationUnit cu, String file, Zone zone) {
        this.file = file;
        this.zone = zone;
        this.accs = new IdentityHashMap<>();
        List<TypeDeclaration<?>> types = (List) cu.findAll(TypeDeclaration.class);
        if (types.isEmpty()) return List.of();
        List<Acc> ordered = new ArrayList<>();
        for (TypeDeclaration<?> t : types) {
            Acc a = new Acc(t, qualifiedName(t));
            accs.put(t, a);
            ordered.add(a);
        }
        primary = accs.get(cu.getTypes().isEmpty() ? types.get(0) : cu.getType(0));

        for (ImportDeclaration imp : cu.getImports()) {
            String n = imp.getNameAsString();
            for (String pkg : cfg.legacyRuntimePackages) {
                if (n.equals(pkg) || n.startsWith(pkg + ".")) {
                    add(primary, line(imp), "JOB001", "imports the COBOL emulation runtime: " + n + (imp.isAsterisk() ? ".*" : ""));
                }
            }
            if (n.startsWith("java.time")) primary.signal("javaTime");
        }

        cu.walk(this::visit);

        for (Comment c : cu.getAllComments()) {
            Matcher m = COBOL_COMMENT.matcher(c.getContent());
            if (m.find()) {
                Acc a = ownerByLine(ordered, line(c));
                add(a, line(c), "JOB057", "comment replays a COBOL statement: " + abbreviate(m.group().trim(), 60));
            }
        }

        List<TypeReport> out = new ArrayList<>();
        for (Acc a : ordered) out.add(finishType(a));
        return out;
    }

    // ---- node rules

    private void visit(Node n) {
        if (n instanceof TypeDeclaration<?>) {
            visitType((TypeDeclaration<?>) n);
            return;
        }
        Acc a = owner(n);
        if (a == null) return;
        if (n instanceof MethodCallExpr mc) {
            visitCall(a, mc);
        } else if (n instanceof StringLiteralExpr s) {
            visitString(a, s);
        } else if (n instanceof VariableDeclarator vd) {
            visitVariable(a, vd);
        } else if (n instanceof Parameter p) {
            visitName(a, p, p.getNameAsString(), p.getType(), false);
        } else if (n instanceof MethodDeclaration md) {
            visitMethod(a, md);
        } else if (n instanceof ClassOrInterfaceType ct) {
            String s = ct.getNameAsString();
            if (s.equals("Optional")) a.signal("optional");
            if (s.equals("BigDecimal")) a.signal("bigDecimal");
            if (isRuntimeType(s)) add(a, line(ct), "JOB001", "uses legacy runtime type " + s);
        } else if (n instanceof AnnotationExpr an) {
            if (isRuntimeType(an.getNameAsString())) add(a, line(an), "JOB001", "carries legacy runtime annotation @" + an.getNameAsString());
        } else if (n instanceof LabeledStmt ls) {
            add(a, line(ls), "JOB052", "labeled statement '" + ls.getLabel() + "' emulates GO TO");
        } else if (n instanceof BlockStmt b) {
            visitBlock(a, b);
        } else if (n instanceof SwitchStmt sw) {
            a.branches++;
            visitSwitch(a, sw);
        } else if (n instanceof LambdaExpr) {
            a.signal("lambda");
        } else if (n instanceof MethodReferenceExpr) {
            a.signal("methodRef");
        } else if (n instanceof SwitchExpr) {
            a.signal("switchExpr");
            a.branches++;
        } else if (n instanceof TextBlockLiteralExpr) {
            a.signal("textBlock");
        } else if (n instanceof VarType) {
            a.signal("var");
        } else if (n instanceof IfStmt || n instanceof ConditionalExpr || n instanceof SwitchEntry || n instanceof ForStmt
            || n instanceof ForEachStmt || n instanceof WhileStmt || n instanceof DoStmt || n instanceof CatchClause) {
            a.branches++;
        } else if (n instanceof BinaryExpr be
            && (be.getOperator() == BinaryExpr.Operator.AND || be.getOperator() == BinaryExpr.Operator.OR)) {
            a.branches++;
        }
    }

    private void visitType(TypeDeclaration<?> t) {
        Acc a = accs.get(t);
        if (t instanceof RecordDeclaration) a.signal("record");
        if (t instanceof EnumDeclaration) a.signal("enum");
        if (t instanceof ClassOrInterfaceDeclaration c && c.isInterface()) a.signal("interface");
        if (t.getModifiers().stream().anyMatch(m -> m.getKeyword() == Modifier.Keyword.SEALED)) a.signal("sealed");
    }

    private void visitCall(Acc a, MethodCallExpr mc) {
        String name = mc.getNameAsString();
        String scope = mc.getScope().map(Node::toString).orElse("");
        if (PERFORM_CALLS.contains(name) && !mc.getArguments().isEmpty() && mc.getArgument(0) instanceof IntegerLiteralExpr) {
            add(a, line(mc), "JOB002", "PERFORM emulation: " + abbreviate(mc.toString(), 60));
        }
        if (mc.getScope().isPresent() && mc.getScope().get() instanceof NameExpr ne && isRuntimeType(ne.getNameAsString())) {
            add(a, line(mc), "JOB001", "calls the legacy runtime: " + ne.getNameAsString() + "." + name + "(...)");
        }
        String method = enclosingMethod(mc);
        if (scope.equals("System") && name.equals("exit") && !method.equals("main")) {
            add(a, line(mc), "JOB053", "System.exit in " + method + "()");
        }
        if ((scope.equals("System.out") || scope.equals("System.err")) && name.startsWith("print") && !method.equals("main")) {
            add(a, line(mc), "JOB055", scope + "." + name + " in " + method + "()");
        }
        if (name.equals("substring") && mc.getArguments().size() == 2
            && mc.getArgument(0) instanceof IntegerLiteralExpr && mc.getArgument(1) instanceof IntegerLiteralExpr) {
            add(a, line(mc), "JOB042", "positional field access " + abbreviate(mc.toString(), 60));
        }
        if ((name.equals("format") || name.equals("formatted")) && !mc.getArguments().isEmpty()) {
            Expression first = name.equals("format") ? mc.getArgument(0) : mc.getScope().orElse(null);
            if (first instanceof StringLiteralExpr fs && FIXED_FORMAT.matcher(fs.asString()).find()) {
                add(a, line(mc), "JOB042", "fixed-width formatting \"" + abbreviate(fs.asString(), 40) + "\"");
            }
        }
        if (name.equals("stream") || name.equals("toList") || name.equals("collect")) a.signal("stream");
    }

    private void visitString(Acc a, StringLiteralExpr s) {
        String v = s.asString();
        if (isEditMask(v)) {
            add(a, line(s), "JOB040", "edit mask \"" + v + "\"");
            return;
        }
        if (PICTURE.matcher(v).matches()) {
            add(a, line(s), "JOB040", "PICTURE clause \"" + v + "\"");
            return;
        }
        Node p = s.getParentNode().orElse(null);
        if (p == null) return;
        boolean comparison = p instanceof SwitchEntry
            || (p instanceof MethodCallExpr mc && COMPARISONS.contains(mc.getNameAsString()))
            || (p instanceof BinaryExpr be && (be.getOperator() == BinaryExpr.Operator.EQUALS || be.getOperator() == BinaryExpr.Operator.NOT_EQUALS));
        if (v.equals("Y") || v.equals("N")) {
            if (comparison || p instanceof AssignExpr || p instanceof VariableDeclarator) {
                add(a, line(s), "JOB030", "'" + v + "' switch in " + abbreviate(p.toString(), 60));
            }
            return;
        }
        if (FILE_STATUS.contains(v) && comparison) {
            String other = p.toString().toLowerCase(Locale.ROOT);
            if (v.equals("00") || other.contains("status") || other.contains("stat") || other.matches("(?s).*\\b(ds|fs)[a-z].*")) {
                add(a, line(s), "JOB031", "FILE STATUS check " + abbreviate(p.toString(), 60));
            }
        }
    }

    private void visitVariable(Acc a, VariableDeclarator vd) {
        boolean constant = false;
        Node p = vd.getParentNode().orElse(null);
        if (p instanceof FieldDeclaration fd) {
            // A static UPPER_SNAKE field is meant as a constant; whether it is mutable is JOB022's concern.
            constant = fd.isStatic() || isInterface(a.type);
        }
        visitName(a, vd, vd.getNameAsString(), vd.getType(), constant);
    }

    private void visitName(Acc a, Node at, String name, Type type, boolean constant) {
        if (hasCobolPrefix(name)) {
            add(a, line(at), "JOB010", "COBOL storage prefix in '" + name + "'");
        } else if (!constant && isCobolCase(name)) {
            add(a, line(at), "JOB011", "COBOL-style identifier '" + name + "'");
        }
        String t = type.asString();
        if (t.equals("String") && isDateName(name)) {
            add(a, line(at), "JOB043", "date '" + name + "' is a String");
        }
        if ((t.equals("double") || t.equals("float") || t.equals("Double") || t.equals("Float")) && isMoneyName(name)) {
            add(a, line(at), "JOB041", "monetary value '" + name + "' is " + t);
        }
    }

    private void visitMethod(Acc a, MethodDeclaration md) {
        String name = md.getNameAsString();
        if (PARAGRAPH_NAME.matcher(name).matches()) {
            add(a, line(md), "JOB003", "paragraph-style method name '" + name + "'");
        } else if (isCobolCase(name) && !UNDERSCORE_NUMBER.matcher(name).matches()) {
            add(a, line(md), "JOB011", "COBOL-style method name '" + name + "'");
        }
        Optional<BlockStmt> body = md.getBody();
        if (body.isEmpty()) return;
        List<Statement> stmts = body.get().getStatements();
        if (md.getType().isVoidType() && !stmts.isEmpty() && stmts.get(stmts.size() - 1) instanceof ReturnStmt r && r.getExpression().isEmpty()) {
            add(a, line(r), "JOB051", "trailing 'return;' in " + name + "()");
        }
        int lines = lines(body.get());
        if (lines > 150) {
            add(a, line(md), "JOB054", Severity.MAJOR, name + "() is " + lines + " lines");
        } else if (lines > 60) {
            add(a, line(md), "JOB054", name + "() is " + lines + " lines");
        }
    }

    private void visitBlock(Acc a, BlockStmt b) {
        List<Statement> s = b.getStatements();
        for (int i = 0; i + 1 < s.size(); i++) {
            if (!(s.get(i) instanceof ExpressionStmt read) || !(s.get(i + 1) instanceof WhileStmt loop)) continue;
            if (!(loop.getBody() instanceof BlockStmt lb) || lb.getStatements().isEmpty()) continue;
            Statement last = lb.getStatements().get(lb.getStatements().size() - 1);
            if (last instanceof ExpressionStmt again && again.getExpression().toString().equals(read.getExpression().toString())
                && containsCall(read.getExpression())) {
                add(a, line(loop), "JOB050", "priming read " + abbreviate(read.getExpression().toString(), 40) + " around while");
            }
        }
    }

    private void visitSwitch(Acc a, SwitchStmt sw) {
        List<SwitchEntry> entries = sw.getEntries();
        if (entries.size() < 8) return;
        int dispatch = 0;
        for (SwitchEntry e : entries) {
            boolean intLabel = !e.getLabels().isEmpty() && e.getLabels().get(0) instanceof IntegerLiteralExpr;
            List<Statement> st = e.getStatements();
            boolean callOnly = !st.isEmpty() && st.get(0) instanceof ExpressionStmt es && es.getExpression() instanceof MethodCallExpr call
                && call.getArguments().isEmpty()
                && (st.size() == 1 || (st.size() == 2 && (st.get(1) instanceof BreakStmt || st.get(1) instanceof ReturnStmt)));
            if (intLabel && callOnly) dispatch++;
        }
        if (dispatch * 10 >= entries.size() * 8) {
            add(a, line(sw), "JOB002", "integer switch dispatches " + dispatch + " paragraphs");
        }
    }

    // ---- type rules and metrics

    private TypeReport finishType(Acc a) {
        TypeDeclaration<?> t = a.type;
        boolean iface = isInterface(t);
        List<String> mutable = new ArrayList<>();
        List<String> fieldNames = new ArrayList<>();
        Map<String, FieldDeclaration> declOf = new LinkedHashMap<>();
        int finalFields = 0;
        int instanceFields = 0;
        for (FieldDeclaration fd : t.getFields()) {
            for (VariableDeclarator v : fd.getVariables()) {
                String n = v.getNameAsString();
                fieldNames.add(n);
                declOf.put(n, fd);
                if (!fd.isStatic()) {
                    instanceFields++;
                    if (fd.isFinal()) finalFields++;
                    else mutable.add(n);
                } else if (!fd.isFinal() && !iface) {
                    add(a, line(v), "JOB022", "mutable static field '" + n + "'");
                }
                if (n.toLowerCase(Locale.ROOT).startsWith("filler")) {
                    add(a, line(v), "JOB013", "filler field '" + n + "'");
                }
            }
        }
        numberedSeries(a, fieldNames, declOf);

        Set<String> mutableSet = new HashSet<>(mutable);
        Map<String, Set<String>> writers = new TreeMap<>();
        int methods = 0;
        int paragraphs = 0;
        int valueReturning = 0;
        int params = 0;
        int methodLines = 0;
        int maxMethod = 0;
        List<MethodDeclaration> declared = t.getMethods();
        for (MethodDeclaration m : declared) {
            if (m.getBody().isEmpty()) continue;
            int ml = lines(m.getBody().get());
            methodLines += ml;
            maxMethod = Math.max(maxMethod, ml);
            params += m.getParameters().size();
            if (!m.getType().isVoidType()) valueReturning++;
            if (isAccessor(m) || isMain(m)) continue;
            methods++;
            Set<String> written = fieldWrites(m, mutableSet);
            for (String f : written) writers.computeIfAbsent(f, k -> new TreeSet<>()).add(m.getNameAsString());
            if (!m.isStatic() && m.getType().isVoidType() && m.getParameters().isEmpty() && !written.isEmpty()) paragraphs++;
            localSeries(a, m);
        }
        List<String> scratch = new ArrayList<>();
        for (Map.Entry<String, Set<String>> e : writers.entrySet()) {
            if (e.getValue().size() >= 2) scratch.add(e.getKey());
        }
        if (scratch.size() > 20) {
            add(a, line(t), "JOB020", Severity.BLOCKER, scratch.size() + " mutable fields are shared scratch state across methods: " + list(scratch));
        } else if (scratch.size() > 8) {
            add(a, line(t), "JOB020", scratch.size() + " mutable fields are shared scratch state across methods: " + list(scratch));
        }
        if (paragraphs >= 4 && paragraphs * 2 >= methods) {
            add(a, line(t), "JOB021", paragraphs + " of " + methods + " methods are void, take no parameters and communicate through fields");
        }
        List<String> statusFields = new ArrayList<>();
        for (String f : mutable) {
            List<String> tok = tokens(f);
            if (tok.contains("status") || tok.contains("stat") || tok.contains("eof")
                || (f.length() > 2 && f.startsWith("ds") && Character.isUpperCase(f.charAt(2)))) statusFields.add(f);
        }
        if (statusFields.size() >= 2) {
            add(a, line(t), "JOB032", statusFields.size() + " status/end-of-file fields steer control flow: " + list(statusFields));
        }

        int loc = ownLines(t);
        int memberMethods = declared.size();
        boolean stateful = scratch.size() > 5 || paragraphs >= 4;
        if ((loc > 800 && stateful) || loc > 2500) {
            add(a, line(t), "JOB056", loc + " lines, " + memberMethods + " methods and " + scratch.size()
                + " shared mutable fields in one class");
        }

        Map<String, Number> metrics = new LinkedHashMap<>();
        metrics.put("methods", memberMethods);
        metrics.put("fields", fieldNames.size());
        metrics.put("mutableFields", mutable.size());
        metrics.put("scratchFields", scratch.size());
        metrics.put("paragraphMethods", paragraphs);
        metrics.put("finalFieldPct", instanceFields == 0 ? 100 : Math.round(100.0 * finalFields / instanceFields));
        int withBody = (int) declared.stream().filter(m -> m.getBody().isPresent()).count();
        metrics.put("avgMethodLines", withBody == 0 ? 0 : Math.round(10.0 * methodLines / withBody) / 10.0);
        metrics.put("maxMethodLines", maxMethod);
        metrics.put("avgParams", withBody == 0 ? 0 : Math.round(10.0 * params / withBody) / 10.0);
        metrics.put("valueReturningPct", withBody == 0 ? 0 : Math.round(100.0 * valueReturning / withBody));
        metrics.put("branches", a.branches);
        if (a.suppressed > 0) metrics.put("suppressedFindings", a.suppressed);

        a.findings.sort((x, y) -> x.line() != y.line() ? Integer.compare(x.line(), y.line()) : x.rule().compareTo(y.rule()));
        boolean audited = zone.audited();
        int index = audited ? Scorer.index(a.findings, cfg) : 0;
        String grade = audited ? Scorer.grade(index) : "-";
        return new TypeReport(file, a.name, kind(t), zone.id(), loc, index, grade, metrics, a.signals, List.copyOf(a.findings));
    }

    private void numberedSeries(Acc a, List<String> names, Map<String, FieldDeclaration> declOf) {
        Map<String, List<String>> series = new TreeMap<>();
        for (String n : names) {
            FieldDeclaration fd = declOf.get(n);
            if (fd.isStatic() && fd.isFinal()) continue;
            if (UNDERSCORE_NUMBER.matcher(n).matches()) {
                add(a, line(fd), "JOB012", "disambiguation suffix in '" + n + "'");
                continue;
            }
            Matcher m = TRAILING_NUMBER.matcher(n);
            if (m.matches()) series.computeIfAbsent(m.group(1), k -> new ArrayList<>()).add(n);
        }
        for (Map.Entry<String, List<String>> e : series.entrySet()) {
            List<String> members = e.getValue();
            if (members.size() < 3) continue;
            FieldDeclaration first = declOf.get(members.get(0));
            add(a, line(first), "JOB012", "numbered field series '" + e.getKey() + "N' (" + members.size() + " fields: "
                + members.get(0) + ".." + members.get(members.size() - 1) + ")");
        }
    }

    /** Numbered locals in one method (flag, flag2, flag3): decompiler/translator residue rather than named values. */
    private void localSeries(Acc a, MethodDeclaration m) {
        Set<String> names = new TreeSet<>();
        for (VariableDeclarator v : m.findAll(VariableDeclarator.class)) names.add(v.getNameAsString());
        Map<String, List<String>> series = new TreeMap<>();
        for (String n : names) {
            Matcher mm = TRAILING_NUMBER.matcher(n);
            if (mm.matches()) series.computeIfAbsent(mm.group(1), k -> new ArrayList<>()).add(n);
        }
        for (Map.Entry<String, List<String>> e : series.entrySet()) {
            int count = e.getValue().size() + (names.contains(e.getKey()) ? 1 : 0);
            if (count >= 3) {
                add(a, line(m), "JOB012", "numbered locals '" + e.getKey() + "', " + String.join(", ", e.getValue())
                    + " in " + m.getNameAsString() + "()");
            }
        }
    }

    /** Mutable instance fields a method assigns or increments (names shadowed by locals or parameters excluded). */
    private static Set<String> fieldWrites(MethodDeclaration m, Set<String> fields) {
        Set<String> locals = new HashSet<>();
        for (Parameter p : m.findAll(Parameter.class)) locals.add(p.getNameAsString());
        for (VariableDeclarator v : m.findAll(VariableDeclarator.class)) locals.add(v.getNameAsString());
        Set<String> out = new TreeSet<>();
        for (AssignExpr ae : m.findAll(AssignExpr.class)) target(ae.getTarget(), fields, locals, out);
        for (UnaryExpr ue : m.findAll(UnaryExpr.class)) {
            UnaryExpr.Operator op = ue.getOperator();
            if (op == UnaryExpr.Operator.PREFIX_INCREMENT || op == UnaryExpr.Operator.PREFIX_DECREMENT
                || op == UnaryExpr.Operator.POSTFIX_INCREMENT || op == UnaryExpr.Operator.POSTFIX_DECREMENT) {
                target(ue.getExpression(), fields, locals, out);
            }
        }
        return out;
    }

    private static void target(Expression e, Set<String> fields, Set<String> locals, Set<String> out) {
        if (e instanceof NameExpr n && fields.contains(n.getNameAsString()) && !locals.contains(n.getNameAsString())) {
            out.add(n.getNameAsString());
        } else if (e instanceof FieldAccessExpr fa && fa.getScope().isThisExpr() && fields.contains(fa.getNameAsString())) {
            out.add(fa.getNameAsString());
        }
    }

    // ---- helpers

    private void add(Acc a, int line, String rule, String message) {
        add(a, line, rule, null, message);
    }

    private void add(Acc a, int line, String rule, Severity severity, String message) {
        if (a == null || !cfg.applies(rule, zone)) return;
        Rules.Spec spec = cfg.rule(rule);
        int n = a.emitted.merge(rule, 1, Integer::sum);
        if (n > EMIT_LIMIT) {
            a.suppressed++;
            return;
        }
        Severity sev = severity == null ? spec.severity() : severity;
        a.findings.add(new Finding(rule, sev, file, line, a.name, message));
    }

    private Acc owner(Node n) {
        Node p = n.getParentNode().orElse(null);
        while (p != null && !(p instanceof TypeDeclaration<?>)) p = p.getParentNode().orElse(null);
        return p == null ? primary : accs.get(p);
    }

    private Acc ownerByLine(List<Acc> ordered, int line) {
        Acc best = primary;
        int bestSpan = Integer.MAX_VALUE;
        for (Acc a : ordered) {
            Optional<Range> r = a.type.getRange();
            if (r.isEmpty()) continue;
            int b = r.get().begin.line;
            int e = r.get().end.line;
            if (line >= b && line <= e && e - b < bestSpan) {
                best = a;
                bestSpan = e - b;
            }
        }
        return best;
    }

    private static String qualifiedName(TypeDeclaration<?> t) {
        StringBuilder sb = new StringBuilder(t.getNameAsString());
        Node p = t.getParentNode().orElse(null);
        while (p != null) {
            if (p instanceof TypeDeclaration<?> o) sb.insert(0, o.getNameAsString() + ".");
            p = p.getParentNode().orElse(null);
        }
        return sb.toString();
    }

    private static String kind(TypeDeclaration<?> t) {
        if (t instanceof RecordDeclaration) return "record";
        if (t instanceof EnumDeclaration) return "enum";
        if (t instanceof AnnotationDeclaration) return "annotation";
        if (isInterface(t)) return "interface";
        return "class";
    }

    private static boolean isInterface(TypeDeclaration<?> t) {
        return t instanceof ClassOrInterfaceDeclaration c && c.isInterface() || t instanceof AnnotationDeclaration;
    }

    private boolean isRuntimeType(String simpleName) {
        return runtimeTypes.contains(simpleName) || simpleName.startsWith("State_") || simpleName.startsWith("Prog_")
            || simpleName.startsWith("Baseline_");
    }

    private boolean hasCobolPrefix(String name) {
        for (String p : prefixes) {
            if (name.length() > p.length() && name.startsWith(p)) {
                char c = name.charAt(p.length());
                if (Character.isUpperCase(c) || c == '_' || Character.isDigit(c)) return true;
            }
        }
        return false;
    }

    private static boolean isCobolCase(String name) {
        if (name.startsWith("_")) return true;
        if (name.contains("_")) return true;
        return name.length() >= 3 && name.equals(name.toUpperCase(Locale.ROOT)) && name.chars().anyMatch(Character::isLetter);
    }

    private static final Set<String> DATE_WORDS = Set.of("date", "dt", "timestamp", "ts", "yyyymmdd", "ccyymmdd", "time");

    private static boolean isDateName(String name) {
        for (String t : tokens(name)) if (DATE_WORDS.contains(t)) return true;
        return false;
    }

    private boolean isMoneyName(String name) {
        for (String t : tokens(name)) if (moneyWords.contains(t)) return true;
        return false;
    }

    static List<String> tokens(String name) {
        List<String> out = new ArrayList<>();
        for (String s : CAMEL.split(name)) if (!s.isEmpty()) out.add(s.toLowerCase(Locale.ROOT));
        return out;
    }

    static boolean isEditMask(String v) {
        if (!EDIT_MASK.matcher(v).matches()) return false;
        long digits = v.chars().filter(c -> c == '9' || c == 'Z').count();
        if (digits < 2) return false;
        return v.indexOf('Z') >= 0 || (v.indexOf('9') >= 0 && (v.indexOf(',') >= 0 || v.indexOf('.') >= 0));
    }

    private static boolean isAccessor(MethodDeclaration m) {
        List<Statement> st = m.getBody().map(BlockStmt::getStatements).map(List::copyOf).orElse(List.of());
        String n = m.getNameAsString();
        if (m.getParameters().isEmpty() && !m.getType().isVoidType() && st.size() == 1 && st.get(0) instanceof ReturnStmt) {
            return true;
        }
        return n.startsWith("set") && m.getParameters().size() == 1 && st.size() <= 1;
    }

    private static boolean isMain(MethodDeclaration m) {
        return m.isStatic() && m.getNameAsString().equals("main");
    }

    private static boolean containsCall(Expression e) {
        return e instanceof MethodCallExpr || (e instanceof AssignExpr ae && ae.getValue() instanceof MethodCallExpr);
    }

    @SuppressWarnings("unchecked")
    private static String enclosingMethod(Node n) {
        return n.findAncestor(MethodDeclaration.class).map(MethodDeclaration::getNameAsString).orElse("");
    }

    private static int line(Node n) {
        return n.getBegin().map(p -> p.line).orElse(0);
    }

    private static int lines(Node n) {
        return n.getRange().map(r -> r.end.line - r.begin.line + 1).orElse(0);
    }

    /** Lines of the declaration, less those of directly nested member types (reported separately). */
    private static int ownLines(TypeDeclaration<?> t) {
        int total = lines(t);
        for (BodyDeclaration<?> m : t.getMembers()) {
            if (m instanceof TypeDeclaration<?>) total -= lines(m);
        }
        return Math.max(total, 1);
    }

    private static String list(List<String> names) {
        int k = Math.min(names.size(), 8);
        String s = String.join(", ", names.subList(0, k));
        return names.size() > k ? s + ", ..." : s;
    }

    static String abbreviate(String s, int max) {
        String one = s.replaceAll("\\s+", " ");
        return one.length() <= max ? one : one.substring(0, max - 3) + "...";
    }
}
