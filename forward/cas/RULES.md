# Design rules of the Customer Account Service

Each rule is enforced by a test, not by trust.
* Architecture tests: `service/cas-architecture`, ArchUnit, run on every build.
* Acceptance scenarios: `service/cas-acceptance`.

"Context modules" are `cas-accounts`, `cas-customers`, `cas-reporting` and `cas-statements`.

| Rule | Statement | Enforced by |
|---|---|---|
| A-1 Hexagonal dependencies | A context module's classes depend only on `java..` and `com.mlcpu.cas.api..`, and on classes of their own context package. Contexts never depend on each other or on the application. | `ArchitectureTest.contextsDependOnlyOnApiAndJdk` |
| A-2 Application layer | `com.mlcpu.cas.application..` depends only on `java..`, `com.mlcpu.cas.api..` and the context packages. | `ArchitectureTest.applicationDependsOnApiAndContexts` |
| A-3 No I/O, clock or randomness in the contexts | Context classes do not use `java.io`, `java.nio`, `java.net`, `java.util.Random`, `java.util.UUID`, `java.security.SecureRandom`, `System` (no `System.out`, `System.currentTimeMillis`, `System.nanoTime`, `System.getenv`), `Thread`, or the no-argument `now()` of `java.time`. The time comes from `CasPorts.clock()` (`LocalDateTime.now(clock)`). | `ArchitectureTest.noIoClockOrRandomnessInContexts` |
| A-4 Immutable domain state | Every field of a context class is `final`; values are records or have only final fields. Collections handed out are unmodifiable (`List.copyOf`, `Stream.toList()`). | `ArchitectureTest.contextFieldsAreFinal` |
| A-5 Money is BigDecimal | No `double` or `float` fields, parameters or return types in the contexts or the application. No `new BigDecimal(double)` and no `BigDecimal.valueOf(double)`. | `ArchitectureTest.noFloatingPointMoney` |
| A-6 No frameworks in phase 2 | No class depends on `org.springframework..`, `jakarta..` or `javax..`. | `ArchitectureTest.noFrameworks` |
| A-7 Packages | Every class of a module is in its root package or below: `com.mlcpu.cas.accounts`, `.customers`, `.reporting`, `.statements`, `.application`. | `ArchitectureTest.classesStayInTheirModulePackage` |
| A-8 Entry point | `com.mlcpu.cas.application.CasApplication` has `public static CustomerAccountService create(CasPorts)`. | Every acceptance scenario (it is how they reach the service) |
| A-9 Typed rejections | A refused transaction is `PostingOutcome.Rejected` with a `RejectionReason` carrying the legacy code. A refused customer change is a `CustomerChangeError` with a `ChangeRejection`. They are never exceptions or strings. | Acceptance scenarios (rules POST, CUST, UPD) |
| A-10 Idempotency | A replayed command key changes nothing and returns the first result. Interest is idempotent per account and cycle. | Acceptance scenarios (rules IDEM, INT-02) |
| A-11 All-or-nothing interest | `accrueInterest` that throws `ReferenceDataMissing` has changed nothing. | Acceptance scenarios (INT-10) |

Context and application classes may use any `java.*` class not excluded above: `java.util`, `java.util.stream`,
`java.math`, `java.time` (with the clock), `java.util.function`.
