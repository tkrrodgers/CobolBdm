package com.mlcpu.loans.acceptance;

import static org.junit.jupiter.api.Assertions.assertTimeoutPreemptively;
import static org.junit.jupiter.api.Assertions.fail;

import java.io.IOException;
import java.io.InputStream;
import java.net.URISyntaxException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.Set;
import java.util.stream.Collectors;
import java.util.stream.Stream;
import org.junit.jupiter.api.DynamicTest;
import org.junit.jupiter.api.TestFactory;

/**
 * Every acceptance scenario of the selected slices (system property loans.slices, comma separated; empty = all; loans.scenarios
 * selects scenarios by id) through the entry points of LoansApplication. Test names: "public calendar cal-0001", "hidden ..." or
 * "privileged ..." (expected values from oracle A beyond the legacy envelope, decision D-6: measured, never shown to a model);
 * a failure lists the first differences. Each scenario has 30 seconds.
 */
class LoansAcceptanceTest {

    static List<Scenario> scenarios() throws IOException, URISyntaxException {
        Set<String> slices = Arrays.stream(System.getProperty("loans.slices", "").split(",")).map(String::trim).filter(s -> !s.isEmpty())
                .collect(Collectors.toSet());
        String only = System.getProperty("loans.scenarios", "").trim();
        Path root = Path.of(LoansAcceptanceTest.class.getResource("/scenarios").toURI());
        List<Scenario> list = new ArrayList<>();
        for (String visibility : List.of("public", "hidden", "privileged")) {
            Path dir = root.resolve(visibility);
            if (!Files.isDirectory(dir)) continue;
            try (Stream<Path> files = Files.walk(dir)) {
                for (Path f : files.filter(f -> f.toString().endsWith(".json")).sorted().toList()) {
                    String slice = f.getParent().getFileName().toString();
                    if (!slices.isEmpty() && !slices.contains(slice)) continue;
                    try (InputStream in = Files.newInputStream(f)) {
                        var json = Json.MAPPER.readTree(in);
                        String id = json.path("id").asText();
                        if (!only.isEmpty() && !Arrays.asList(only.split(",")).contains(id)) continue;
                        list.add(new Scenario(id, slice, visibility, json));
                    }
                }
            }
        }
        return list;
    }

    @TestFactory
    Stream<DynamicTest> scenariosPass() throws IOException, URISyntaxException {
        return scenarios().stream().map(s -> DynamicTest.dynamicTest(s.visibility() + " " + s.slice() + " " + s.id(), () -> {
            List<String> differences = assertTimeoutPreemptively(Duration.ofSeconds(30), () -> s.run(5),
                    "the scenario did not finish in 30 seconds (an endless loop?)");
            if (!differences.isEmpty()) fail(String.join("\n", differences));
        }));
    }
}
