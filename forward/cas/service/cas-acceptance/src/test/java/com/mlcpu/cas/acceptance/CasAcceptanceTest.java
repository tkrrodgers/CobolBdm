package com.mlcpu.cas.acceptance;

import static org.junit.jupiter.api.Assertions.assertTimeoutPreemptively;
import static org.junit.jupiter.api.Assertions.fail;

import com.mlcpu.cas.application.CasApplication;
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
 * Every acceptance scenario of the selected slices (system property cas.slices, comma separated; empty = all) against
 * CasApplication.create. Test names: "public accounts post-0001" or "hidden ..."; a failure lists the first differences.
 */
class CasAcceptanceTest {

    static List<Scenario> scenarios() throws IOException, URISyntaxException {
        Set<String> slices = Arrays.stream(System.getProperty("cas.slices", "").split(",")).map(String::trim).filter(s -> !s.isEmpty())
                .collect(Collectors.toSet());
        String only = System.getProperty("cas.scenarios", "").trim();
        Path root = Path.of(CasAcceptanceTest.class.getResource("/scenarios").toURI());
        List<Scenario> list = new ArrayList<>();
        for (String visibility : List.of("public", "hidden")) {
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
            List<String> differences = assertTimeoutPreemptively(Duration.ofSeconds(30),
                    () -> s.run(p -> CasApplication.create(p.ports()), 5), "the scenario did not finish in 30 seconds (an endless loop?)");
            if (!differences.isEmpty()) fail(String.join("\n", differences));
        }));
    }
}
