// Customer Account Service (forward-engineering evaluation, tools/eval/PROTOCOL-forward.md).
// cas-api, cas-architecture and cas-acceptance are the SME's; the other five modules are the implementation under test.
val contexts = listOf(":cas-accounts", ":cas-customers", ":cas-reporting", ":cas-statements")

subprojects {
    apply(plugin = "java")
    repositories { mavenCentral() }
    extensions.configure<JavaPluginExtension> {
        sourceCompatibility = JavaVersion.VERSION_21
        targetCompatibility = JavaVersion.VERSION_21
    }
    tasks.withType<JavaCompile>().configureEach {
        options.encoding = "UTF-8"
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

for (c in contexts) project(c) { dependencies { "implementation"(project(":cas-api")) } }

project(":cas-application") {
    apply(plugin = "java-library")
    dependencies {
        "api"(project(":cas-api"))
        for (c in contexts) "implementation"(project(c))
    }
}

project(":cas-architecture") {
    dependencies {
        "testImplementation"(project(":cas-api"))
        "testImplementation"(project(":cas-application"))
        for (c in contexts) "testImplementation"(project(c))
        "testImplementation"("com.tngtech.archunit:archunit-junit5:1.4.1")
    }
}

project(":cas-acceptance") {
    dependencies {
        "testImplementation"(project(":cas-api"))
        "testImplementation"(project(":cas-application"))
        "testImplementation"("com.fasterxml.jackson.core:jackson-databind:2.22.1")
    }
    tasks.withType<Test>().configureEach {
        // A scenario that loops for ever must not hang the judge: each scenario has its own timeout (CasAcceptanceTest).
        systemProperty("cas.scenarios", project.findProperty("casScenarios") ?: "")
        systemProperty("cas.slices", project.findProperty("casSlices") ?: "")
    }
}
