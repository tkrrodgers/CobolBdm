package com.mlcpu.loans.architecture;

import static com.tngtech.archunit.lang.syntax.ArchRuleDefinition.classes;
import static com.tngtech.archunit.lang.syntax.ArchRuleDefinition.fields;
import static com.tngtech.archunit.lang.syntax.ArchRuleDefinition.noClasses;
import static com.tngtech.archunit.lang.syntax.ArchRuleDefinition.noCodeUnits;
import static com.tngtech.archunit.lang.syntax.ArchRuleDefinition.noFields;
import static com.tngtech.archunit.lang.syntax.ArchRuleDefinition.noMethods;

import com.mlcpu.loans.api.ScheduleService;
import com.mlcpu.loans.api.migration.LegacyMigration;
import com.mlcpu.loans.api.ports.LoansPorts;
import com.mlcpu.loans.api.servicing.LoanServicing;
import com.mlcpu.loans.api.spi.DueDates;
import com.mlcpu.loans.api.spi.RateFactors;
import com.tngtech.archunit.core.domain.JavaClass;
import com.tngtech.archunit.core.domain.JavaClasses;
import com.tngtech.archunit.core.domain.JavaCodeUnit;
import com.tngtech.archunit.core.importer.ClassFileImporter;
import com.tngtech.archunit.core.importer.ImportOption;
import com.tngtech.archunit.lang.ArchCondition;
import com.tngtech.archunit.lang.ArchRule;
import com.tngtech.archunit.lang.ConditionEvents;
import com.tngtech.archunit.lang.EvaluationResult;
import com.tngtech.archunit.lang.SimpleConditionEvent;
import java.io.DataInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.lang.reflect.Method;
import java.lang.reflect.Modifier;
import java.math.BigDecimal;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.stream.Collectors;
import java.util.stream.Stream;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/**
 * RULES.md A-1 to A-11 (the forward-engineering design, FE 5.4) over every class of the engine's modules: the kernel, the contexts
 * and the application. Each test reports every violation it finds.
 */
class ArchitectureTest {
    static final String ROOT = "com.mlcpu.loans";
    static final String API = ROOT + ".api..";
    static final String KERNEL = ROOT + ".kernel..";
    static final String APPLICATION = ROOT + ".application..";
    static final List<String> CONTEXTS = List.of("calendar", "rates", "terms", "schedule", "disclosure", "migration");
    static JavaClasses classes;

    @BeforeAll
    static void importClasses() {
        classes = new ClassFileImporter().withImportOption(ImportOption.Predefined.DO_NOT_INCLUDE_TESTS).importPackages(ROOT);
    }

    static String[] contextPackages() {
        return CONTEXTS.stream().map(c -> ROOT + "." + c + "..").toArray(String[]::new);
    }

    /** The implementation: the kernel, the contexts and the application. */
    static String[] engine() {
        return Stream.concat(Stream.of(KERNEL, APPLICATION), Arrays.stream(contextPackages())).toArray(String[]::new);
    }

    /** Evaluates every rule and fails once with all their violations. */
    static void checkAll(ArchRule... rules) {
        List<String> failures = new ArrayList<>();
        for (ArchRule rule : rules) {
            EvaluationResult result = rule.allowEmptyShould(true).evaluate(classes);
            if (result.hasViolation()) failures.add(result.getFailureReport().toString());
        }
        if (!failures.isEmpty()) throw new AssertionError(String.join("\n", failures));
    }

    @Test
    void contextsDependOnlyOnApiKernelAndJdk() {
        List<ArchRule> rules = new ArrayList<>();
        for (String c : CONTEXTS) {
            rules.add(classes().that().resideInAPackage(ROOT + "." + c + "..")
                    .should().onlyDependOnClassesThat().resideInAnyPackage("java..", API, KERNEL, ROOT + "." + c + "..")
                    .because("A-1: a context depends only on loans-api, loans-kernel, the JDK and itself"));
        }
        rules.add(classes().that().resideInAPackage(KERNEL)
                .should().onlyDependOnClassesThat().resideInAnyPackage("java..", API, KERNEL)
                .because("A-1: the kernel depends only on loans-api and the JDK"));
        checkAll(rules.toArray(ArchRule[]::new));
    }

    @Test
    void applicationDependsOnApiKernelAndContexts() {
        String[] allowed = Stream.concat(Stream.of("java..", API), Arrays.stream(engine())).toArray(String[]::new);
        checkAll(classes().that().resideInAPackage(APPLICATION)
                .should().onlyDependOnClassesThat().resideInAnyPackage(allowed)
                .because("A-2: the application depends on loans-api, the kernel and the contexts"));
    }

    @Test
    void noIoClockOrRandomnessInContexts() {
        String[] pure = Stream.concat(Stream.of(KERNEL), Arrays.stream(contextPackages())).toArray(String[]::new);
        checkAll(
                noClasses().that().resideInAnyPackage(pure)
                        .should().dependOnClassesThat().resideInAnyPackage("java.io..", "java.nio..", "java.net..")
                        .because("A-3: no I/O in the kernel and the contexts"),
                noClasses().that().resideInAnyPackage(pure)
                        .should().dependOnClassesThat().haveFullyQualifiedName("java.util.Random")
                        .orShould().dependOnClassesThat().haveFullyQualifiedName("java.util.UUID")
                        .orShould().dependOnClassesThat().haveFullyQualifiedName("java.security.SecureRandom")
                        .orShould().dependOnClassesThat().haveFullyQualifiedName("java.util.concurrent.ThreadLocalRandom")
                        .orShould().dependOnClassesThat().haveFullyQualifiedName("java.lang.System")
                        .orShould().dependOnClassesThat().haveFullyQualifiedName("java.lang.Thread")
                        .because("A-3: no randomness, system access or threads in the kernel and the contexts"),
                noClasses().that().resideInAnyPackage(pure)
                        .should(callNowWithoutClock()).because("A-3: the time comes from LoansPorts.clock()"));
    }

    static ArchCondition<JavaClass> callNowWithoutClock() {
        return new ArchCondition<>("call java.time now() without a clock") {
            @Override
            public void check(JavaClass item, ConditionEvents events) {
                item.getMethodCallsFromSelf().forEach(call -> {
                    var target = call.getTarget();
                    boolean noArgNow = target.getName().equals("now") && target.getOwner().getPackageName().equals("java.time")
                                       && target.getRawParameterTypes().isEmpty();
                    boolean systemClock = target.getOwner().getFullName().equals("java.time.Clock") && target.getName().startsWith("system");
                    if (noArgNow || systemClock) events.add(SimpleConditionEvent.violated(call, call.getDescription()));
                });
            }
        };
    }

    @Test
    void fieldsAreFinal() {
        checkAll(
                fields().that().areDeclaredInClassesThat().resideInAnyPackage(engine()).and().areNotStatic()
                        .should().beFinal().because("A-4: domain state is immutable"),
                fields().that().areDeclaredInClassesThat().resideInAnyPackage(engine()).and().areStatic()
                        .should().beFinal().because("A-4: no mutable static state"));
    }

    @Test
    void noFloatingPointMoney() {
        List<ArchRule> rules = new ArrayList<>();
        for (Class<?> fp : List.of(double.class, float.class, Double.class, Float.class)) {
            rules.add(noFields().that().areDeclaredInClassesThat().resideInAnyPackage(engine()).should().haveRawType(fp)
                    .because("A-5: money is BigDecimal"));
            rules.add(noMethods().that().areDeclaredInClassesThat().resideInAnyPackage(engine()).should().haveRawReturnType(fp)
                    .because("A-5: money is BigDecimal"));
        }
        rules.add(noCodeUnits().that().areDeclaredInClassesThat().resideInAnyPackage(engine()).should(haveFloatingPointParameters())
                .because("A-5: money is BigDecimal"));
        rules.add(noClasses().that().resideInAnyPackage(engine())
                .should().callConstructor(BigDecimal.class, double.class)
                .orShould().callMethod(BigDecimal.class, "valueOf", double.class)
                .because("A-5: no BigDecimal from a double"));
        rules.add(noClasses().that().resideInAnyPackage(engine())
                .should().callMethod(BigDecimal.class, "divide", BigDecimal.class)
                .because("A-5: every divide carries a MathContext, or a scale and a rounding mode"));
        checkAll(rules.toArray(ArchRule[]::new));
    }

    static ArchCondition<JavaCodeUnit> haveFloatingPointParameters() {
        return new ArchCondition<>("have double or float parameters") {
            @Override
            public void check(JavaCodeUnit item, ConditionEvents events) {
                boolean fp = item.getRawParameterTypes().stream()
                        .anyMatch(t -> List.of("double", "float", "java.lang.Double", "java.lang.Float").contains(t.getName()));
                events.add(new SimpleConditionEvent(item, fp, item.getFullName()));
            }
        };
    }

    @Test
    void noFrameworks() {
        checkAll(noClasses().that().resideInAPackage(ROOT + "..")
                .should().dependOnClassesThat().resideInAnyPackage("org.springframework..", "jakarta..", "javax..", "org.apache.ofbiz..")
                .because("A-6: no frameworks in the engine"));
    }

    @Test
    void classesStayInTheirModulePackage() {
        String[] allowed = Stream.concat(Stream.of(API), Arrays.stream(engine())).toArray(String[]::new);
        checkAll(classes().that().resideInAPackage(ROOT + "..")
                .should().resideInAnyPackage(allowed)
                .because("A-7: every class is in its module's root package or below"));
    }

    /** An entry method of LoansApplication: its name, what it returns, its parameters. */
    record Entry(String method, Class<?> returns, Class<?>... parameters) {
    }

    /** The entry methods of LoansApplication, by the slice that brings them (rule A-8). */
    static final Map<String, Entry> ENTRIES = Map.of(
            "calendar", new Entry("dueDates", DueDates.class),
            "rates", new Entry("rateFactors", RateFactors.class),
            "terms", new Entry("quotes", ScheduleService.class),
            "schedule", new Entry("quotes", ScheduleService.class),
            "disclosure", new Entry("quotes", ScheduleService.class),
            "servicing", new Entry("create", LoanServicing.class, LoansPorts.class),
            "migration", new Entry("migration", LegacyMigration.class));

    /** A-8, for the slices under test (system property loans.slices; empty = all). */
    @Test
    void entryPoints() throws ReflectiveOperationException {
        Set<String> slices = Arrays.stream(System.getProperty("loans.slices", "").split(",")).map(String::trim).filter(s -> !s.isEmpty())
                .collect(Collectors.toSet());
        Class<?> app = Class.forName(ROOT + ".application.LoansApplication");
        List<String> failures = new ArrayList<>();
        ENTRIES.forEach((slice, entry) -> {
            if (!slices.isEmpty() && !slices.contains(slice)) return;
            String expected = "public static " + entry.returns().getSimpleName() + " " + entry.method() + "("
                    + Arrays.stream(entry.parameters()).map(p -> p.getSimpleName() + " " + Character.toLowerCase(p.getSimpleName().charAt(0))
                    + p.getSimpleName().substring(1)).collect(Collectors.joining(", ")) + ")";
            try {
                Method m = app.getMethod(entry.method(), entry.parameters());
                if (!Modifier.isStatic(m.getModifiers()) || m.getReturnType() != entry.returns()) failures.add("LoansApplication needs " + expected);
            } catch (NoSuchMethodException e) {
                failures.add("LoansApplication needs " + expected + " (slice " + slice + ")");
            }
        });
        if (!failures.isEmpty()) throw new AssertionError("A-8: entry points\n" + String.join("\n", failures.stream().distinct().toList()));
    }

    @Test
    void rejectionsAreTypedNotThrown() {
        checkAll(noClasses().that().resideInAnyPackage(engine())
                .should().beAssignableTo(Throwable.class)
                .because("A-9: a rule violation is a typed rejection (ScheduleRejection and the like), never an exception"));
    }

    @Test
    void bytecodeLevel17() throws IOException {
        List<String> failures = new ArrayList<>();
        for (JavaClass c : classes) {
            if (!c.getPackageName().startsWith(ROOT)) continue;
            String resource = "/" + c.getName().replace('.', '/') + ".class";
            try (InputStream in = ArchitectureTest.class.getResourceAsStream(resource)) {
                if (in == null) continue;
                DataInputStream data = new DataInputStream(in);
                data.readInt();
                data.readUnsignedShort();
                int major = data.readUnsignedShort();
                if (major != 61) failures.add(c.getName() + ": class file version " + major);
            }
        }
        if (!failures.isEmpty()) throw new AssertionError("A-10: bytecode level 17 (class file version 61)\n" + String.join("\n", failures));
    }

    /**
     * A-11, the part that needs no dictionary: no class, method, field or package name contains the source system's name words
     * (test resource source-system-names.txt, the leak checker's own data). The leak checker (fwd-leaks) checks every identifier
     * of its dictionary on the package and on what a model writes.
     */
    @Test
    void noSourceSystemNames() throws IOException {
        List<String> words;
        try (InputStream in = ArchitectureTest.class.getResourceAsStream("/source-system-names.txt")) {
            words = new String(in.readAllBytes(), java.nio.charset.StandardCharsets.UTF_8).lines().map(String::trim)
                    .filter(w -> !w.isEmpty() && !w.startsWith("#")).map(w -> w.toLowerCase(Locale.ROOT)).toList();
        }
        List<String> failures = new ArrayList<>();
        for (JavaClass c : classes) {
            if (!c.getPackageName().startsWith(ROOT)) continue;
            List<String> names = new ArrayList<>(List.of(c.getName()));
            c.getMethods().forEach(m -> names.add(m.getFullName()));
            c.getFields().forEach(f -> names.add(f.getFullName()));
            for (String n : names) {
                String lower = n.toLowerCase(Locale.ROOT);
                if (words.stream().anyMatch(lower::contains)) failures.add(n);
            }
        }
        if (!failures.isEmpty()) throw new AssertionError("A-11: no source-system identifiers\n" + String.join("\n", failures));
    }
}
