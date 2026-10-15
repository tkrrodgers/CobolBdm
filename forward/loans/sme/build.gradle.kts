// The SME's tools over the reference implementation (ForwardEngineeringFineract.md section 7): the pool runner. It compiles the
// service's loans-api and the reference modules (../reference/<module>) as one program; the service build checks their module
// boundaries (gradle -p ../service -PimplDir=../reference build).
plugins {
    java
    application
}

repositories { mavenCentral() }

val referenceModules = listOf("loans-kernel", "loans-calendar", "loans-rates", "loans-terms", "loans-schedule", "loans-disclosure",
    "loans-migration", "loans-application")

sourceSets {
    main {
        java.srcDirs(listOf("src/main/java", "../service/loans-api/src/main/java", "../service/loans-api/src/generated/java", "../service/loans-acceptance/src/memory/java")
            + referenceModules.map { "../reference/$it/src/main/java" })
    }
}

dependencies {
    implementation("com.fasterxml.jackson.core:jackson-databind:2.22.1")
}

tasks.withType<JavaCompile>().configureEach {
    options.encoding = "UTF-8"
    options.release.set(17)
}

application {
    mainClass.set("com.mlcpu.loans.sme.PoolRunner")
    applicationDefaultJvmArgs = listOf("-Xss64m")
}
