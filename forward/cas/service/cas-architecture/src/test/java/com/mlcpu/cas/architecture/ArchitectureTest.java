package com.mlcpu.cas.architecture;

import static com.tngtech.archunit.lang.syntax.ArchRuleDefinition.classes;
import static com.tngtech.archunit.lang.syntax.ArchRuleDefinition.fields;
import static com.tngtech.archunit.lang.syntax.ArchRuleDefinition.noClasses;
import static com.tngtech.archunit.lang.syntax.ArchRuleDefinition.noCodeUnits;
import static com.tngtech.archunit.lang.syntax.ArchRuleDefinition.noFields;
import static com.tngtech.archunit.lang.syntax.ArchRuleDefinition.noMethods;

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
import java.math.BigDecimal;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.stream.Stream;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/** RULES.md A-1 to A-7 over every class of the five implementation modules. Each test reports every violation it finds. */
class ArchitectureTest {
    static final String API = "com.mlcpu.cas.api..";
    static final List<String> CONTEXTS = List.of("accounts", "customers", "reporting", "statements");
    static JavaClasses classes;

    @BeforeAll
    static void importClasses() {
        classes = new ClassFileImporter().withImportOption(ImportOption.Predefined.DO_NOT_INCLUDE_TESTS).importPackages("com.mlcpu.cas");
    }

    static String[] contextPackages() {
        return CONTEXTS.stream().map(c -> "com.mlcpu.cas." + c + "..").toArray(String[]::new);
    }

    static String[] contextsAndApplication() {
        return Stream.concat(Arrays.stream(contextPackages()), Stream.of("com.mlcpu.cas.application..")).toArray(String[]::new);
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
    void contextsDependOnlyOnApiAndJdk() {
        checkAll(CONTEXTS.stream().map(c -> classes().that().resideInAPackage("com.mlcpu.cas." + c + "..")
                .should().onlyDependOnClassesThat().resideInAnyPackage("java..", API, "com.mlcpu.cas." + c + "..")
                .because("A-1: a context depends only on cas-api, the JDK and itself")).toArray(ArchRule[]::new));
    }

    @Test
    void applicationDependsOnApiAndContexts() {
        String[] allowed = Stream.concat(Stream.of("java..", API), Arrays.stream(contextsAndApplication())).toArray(String[]::new);
        checkAll(classes().that().resideInAPackage("com.mlcpu.cas.application..")
                .should().onlyDependOnClassesThat().resideInAnyPackage(allowed)
                .because("A-2: the application depends on cas-api and the contexts"));
    }

    @Test
    void noIoClockOrRandomnessInContexts() {
        checkAll(
                noClasses().that().resideInAnyPackage(contextPackages())
                        .should().dependOnClassesThat().resideInAnyPackage("java.io..", "java.nio..", "java.net..")
                        .because("A-3: no I/O in the contexts"),
                noClasses().that().resideInAnyPackage(contextPackages())
                        .should().dependOnClassesThat().haveFullyQualifiedName("java.util.Random")
                        .orShould().dependOnClassesThat().haveFullyQualifiedName("java.util.UUID")
                        .orShould().dependOnClassesThat().haveFullyQualifiedName("java.security.SecureRandom")
                        .orShould().dependOnClassesThat().haveFullyQualifiedName("java.util.concurrent.ThreadLocalRandom")
                        .orShould().dependOnClassesThat().haveFullyQualifiedName("java.lang.System")
                        .orShould().dependOnClassesThat().haveFullyQualifiedName("java.lang.Thread")
                        .because("A-3: no randomness, system access or threads in the contexts"),
                noClasses().that().resideInAnyPackage(contextPackages())
                        .should(callNowWithoutClock()).because("A-3: the time comes from CasPorts.clock()"));
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
    void contextFieldsAreFinal() {
        checkAll(
                fields().that().areDeclaredInClassesThat().resideInAnyPackage(contextPackages()).and().areNotStatic()
                        .should().beFinal().because("A-4: domain state is immutable"),
                fields().that().areDeclaredInClassesThat().resideInAnyPackage(contextPackages()).and().areStatic()
                        .should().beFinal().because("A-4: no mutable static state"));
    }

    @Test
    void noFloatingPointMoney() {
        List<ArchRule> rules = new ArrayList<>();
        for (Class<?> fp : List.of(double.class, float.class, Double.class, Float.class)) {
            rules.add(noFields().that().areDeclaredInClassesThat().resideInAnyPackage(contextsAndApplication()).should().haveRawType(fp)
                    .because("A-5: money is BigDecimal"));
            rules.add(noMethods().that().areDeclaredInClassesThat().resideInAnyPackage(contextsAndApplication()).should().haveRawReturnType(fp)
                    .because("A-5: money is BigDecimal"));
        }
        rules.add(noCodeUnits().that().areDeclaredInClassesThat().resideInAnyPackage(contextsAndApplication()).should(haveFloatingPointParameters())
                .because("A-5: money is BigDecimal"));
        rules.add(noClasses().that().resideInAnyPackage(contextsAndApplication())
                .should().callConstructor(BigDecimal.class, double.class)
                .orShould().callMethod(BigDecimal.class, "valueOf", double.class)
                .because("A-5: no BigDecimal from a double"));
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
        checkAll(noClasses().that().resideInAPackage("com.mlcpu.cas..")
                .should().dependOnClassesThat().resideInAnyPackage("org.springframework..", "jakarta..", "javax..")
                .because("A-6: no frameworks in phase 2"));
    }

    @Test
    void classesStayInTheirModulePackage() {
        checkAll(classes().that().resideInAPackage("com.mlcpu.cas..")
                .should().resideInAnyPackage("com.mlcpu.cas.api..", "com.mlcpu.cas.accounts..", "com.mlcpu.cas.customers..",
                        "com.mlcpu.cas.reporting..", "com.mlcpu.cas.statements..", "com.mlcpu.cas.application..")
                .because("A-7: every class is in its module's root package or below"));
    }
}
