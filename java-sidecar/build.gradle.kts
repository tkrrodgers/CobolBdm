plugins {
    application
    java
}

group = "com.mlcpu"
version = "1.0.0"

java {
    sourceCompatibility = JavaVersion.VERSION_21
    targetCompatibility = JavaVersion.VERSION_21
}

repositories {
    mavenCentral()
}

dependencies {
    implementation("com.google.code.gson:gson:2.11.0")
    testImplementation(platform("org.junit:junit-bom:5.11.3"))
    testImplementation("org.junit.jupiter:junit-jupiter")
    testRuntimeOnly("org.junit.platform:junit-platform-launcher")
}

application {
    mainClass.set("com.mlcpu.sidecar.Sidecar")
    applicationDefaultJvmArgs = listOf(
        "-Xms512m", "-Xmx3g", "-XX:MaxMetaspaceSize=512m",
        "-XX:+ExitOnOutOfMemoryError", "-XX:+HeapDumpOnOutOfMemoryError", "-XX:HeapDumpPath=build/oom"
    )
}

val verifyJava21 by tasks.registering {
    doLast {
        val v = JavaVersion.current()
        if (v != JavaVersion.VERSION_21) throw GradleException("Java 21 is required (found $v)")
    }
}

tasks.named("compileJava") { dependsOn(verifyJava21) }
tasks.named("compileTestJava") { dependsOn(verifyJava21) }

tasks.withType<JavaCompile>().configureEach {
    options.encoding = "UTF-8"
    options.compilerArgs.add("-parameters")
}

tasks.test {
    dependsOn(verifyJava21)
    useJUnitPlatform()
    jvmArgs("-Xss64m")
}

tasks.named<JavaExec>("run") {
    workingDir = projectDir
    environment("MLCPU_CLOCK", System.getenv("MLCPU_CLOCK") ?: "MONOTONIC")
}
