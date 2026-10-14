// Loans service (the forward-engineering design, FE section 5: the recovered loan schedule estate into an ERP).
// loans-api, loans-architecture and loans-acceptance are the SME's; the other modules are the implementation under test.
// Java 21 toolchain, bytecode for 17: OFBiz compiles at 17 (rule A-10).
val kernel = ":loans-kernel"
val contexts = listOf(":loans-calendar", ":loans-rates", ":loans-terms", ":loans-schedule", ":loans-disclosure")

subprojects {
    apply(plugin = "java")
    repositories { mavenCentral() }
    tasks.withType<JavaCompile>().configureEach {
        options.encoding = "UTF-8"
        options.release.set(17)
        options.compilerArgs.add("-parameters")
    }
    tasks.withType<Test>().configureEach {
        useJUnitPlatform()
        jvmArgs("-Xss16m")
        // The acceptance and architecture suites run in full every time; Gradle must not skip them as up to date.
        outputs.upToDateWhen { false }
    }
    dependencies {
        "testImplementation"(platform("org.junit:junit-bom:5.11.3"))
        "testImplementation"("org.junit.jupiter:junit-jupiter")
        "testRuntimeOnly"("org.junit.platform:junit-platform-launcher")
    }
}

// -PimplDir=<dir>: the implementation modules' sources from <dir>/<module>/src/main/java instead, with the same module graph
// (the SME's reference: gradle -PimplDir=../reference build).
findProperty("implDir")?.let { dir ->
    for (m in listOf(kernel) + contexts + ":loans-application") project(m) {
        the<SourceSetContainer>().named("main") { java.setSrcDirs(listOf(rootProject.file("$dir/${project.name}/src/main/java"))) }
    }
}

project(kernel) { dependencies { "implementation"(project(":loans-api")) } }
for (c in contexts) project(c) {
    dependencies {
        "implementation"(project(":loans-api"))
        "implementation"(project(kernel))
    }
}

project(":loans-application") {
    apply(plugin = "java-library")
    dependencies {
        "api"(project(":loans-api"))
        "implementation"(project(kernel))
        for (c in contexts) "implementation"(project(c))
    }
}

project(":loans-architecture") {
    dependencies {
        "testImplementation"(project(":loans-api"))
        "testImplementation"(project(":loans-application"))
        "testImplementation"(project(kernel))
        for (c in contexts) "testImplementation"(project(c))
        "testImplementation"("com.tngtech.archunit:archunit-junit5:1.4.1")
    }
    // A-8 checks the entry points of the slices under test only (each slice brings its own).
    tasks.withType<Test>().configureEach { systemProperty("loans.slices", project.findProperty("loansSlices") ?: "") }
}

project(":loans-acceptance") {
    // The in-memory ports are shown to the implementer and shared with the SME's tools (sme/), so they have their own folder.
    the<SourceSetContainer>().named("test") { java.srcDir("src/memory/java") }
    dependencies {
        "testImplementation"(project(":loans-api"))
        "testImplementation"(project(":loans-application"))
        "testImplementation"("com.fasterxml.jackson.core:jackson-databind:2.22.1")
    }
    tasks.withType<Test>().configureEach {
        // A scenario that loops for ever must not hang the judge: each scenario has its own timeout.
        systemProperty("loans.scenarios", project.findProperty("loansScenarios") ?: "")
        systemProperty("loans.slices", project.findProperty("loansSlices") ?: "")
    }
}
