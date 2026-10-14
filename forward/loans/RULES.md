# Design rules of the Loans Service

Each rule is enforced by a test, not by trust.
* Architecture tests: `service/loans-architecture`, ArchUnit, run on every build.
* Acceptance scenarios: `service/loans-acceptance`.

"Context modules" are `loans-calendar`, `loans-rates`, `loans-terms`, `loans-schedule` and `loans-disclosure`. "The engine" is
the kernel, the context modules and the application.

| Rule | Statement | Enforced by |
|---|---|---|
| A-1 Hexagonal dependencies | A context module's classes depend only on `java..`, `com.mlcpu.loans.api..`, `com.mlcpu.loans.kernel..` and classes of their own context package. Contexts never depend on each other or on the application. The kernel depends only on `java..` and `com.mlcpu.loans.api..`. | `ArchitectureTest.contextsDependOnlyOnApiKernelAndJdk` |
| A-2 Application layer | `com.mlcpu.loans.application..` depends only on `java..`, `com.mlcpu.loans.api..`, the kernel and the context packages. | `ArchitectureTest.applicationDependsOnApiKernelAndContexts` |
| A-3 No I/O, clock or randomness | Kernel and context classes do not use `java.io`, `java.nio`, `java.net`, `java.util.Random`, `java.util.UUID`, `java.security.SecureRandom`, `System`, `Thread`, or the no-argument `now()` of `java.time`. | `ArchitectureTest.noIoClockOrRandomnessInContexts` |
| A-4 Immutable state | Every field of an engine class is `final`; values are records or have only final fields. Collections handed out are unmodifiable (`List.copyOf`, `Stream.toList()`). | `ArchitectureTest.fieldsAreFinal` |
| A-5 Money is BigDecimal | No `double` or `float` fields, parameters or return types in the engine. No `new BigDecimal(double)`, no `BigDecimal.valueOf(double)`, and no `BigDecimal.divide(BigDecimal)` without a `MathContext` or a scale and rounding mode. | `ArchitectureTest.noFloatingPointMoney` |
| A-6 No frameworks | No class depends on `org.springframework..`, `jakarta..`, `javax..` or `org.apache.ofbiz..`. | `ArchitectureTest.noFrameworks` |
| A-7 Packages | Every class of a module is in its root package or below: `com.mlcpu.loans.kernel`, `.calendar`, `.rates`, `.terms`, `.schedule`, `.disclosure`, `.application`. | `ArchitectureTest.classesStayInTheirModulePackage` |
| A-8 Entry points | `com.mlcpu.loans.application.LoansApplication` has the entry points of SPEC.md section 2 for the slices built so far: `public static DueDates dueDates()`, `public static RateFactors rateFactors()`, `public static ScheduleService quotes()`. | `ArchitectureTest.entryPoints`; every acceptance scenario (it is how they reach the service) |
| A-9 Typed rejections | A rule violation is a typed rejection (`ScheduleRejection` in a `Rejected` outcome, `RateRejected` from `RateFactors`), never an exception or a string. The engine defines no exception classes. | `ArchitectureTest.rejectionsAreTypedNotThrown`; acceptance scenarios |
| A-10 Java 17 bytecode | The build compiles with `--release 17` (the ERP's language level). | `ArchitectureTest.bytecodeLevel17` |
| A-11 Own vocabulary | Class, method, field and package names are the service's own business vocabulary. | `ArchitectureTest.noSourceSystemNames` |

Engine classes may use any `java.*` class not excluded above: `java.util`, `java.util.stream`, `java.math`, `java.time`,
`java.util.function`.
