package com.mlcpu.rt;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Map;
import java.util.TreeMap;

/**
 * Golden pins for characterization tests: the first run records each case's state under characterization/&lt;PID&gt;.golden;
 * later runs must reproduce it exactly. Delete the file (or one line) to re-approve.
 */
public final class Characterization {
    private Characterization() {}

    private static final Map<String, TreeMap<String, String>> cache = new java.util.HashMap<>();

    public static synchronized void pin(String programId, String key, ProgramState state) {
        Path file = Path.of(System.getProperty("mlcpu.characterization.dir", "characterization"), programId + ".golden");
        TreeMap<String, String> golden = cache.computeIfAbsent(programId, k -> load(file));
        String actual = render(state);
        String expected = golden.get(key);
        if (expected == null) {
            golden.put(key, actual);
            save(file, golden);
            return;
        }
        if (!expected.equals(actual))
            throw new AssertionError("characterization drift for " + programId + " " + key + "\n expected: " + expected + "\n actual:   " + actual);
    }

    static String render(ProgramState s) {
        StringBuilder sb = new StringBuilder();
        for (Map.Entry<String, Object> e : new TreeMap<>(s.fields()).entrySet())
            sb.append(e.getKey()).append('=').append(String.valueOf(e.getValue()).replace("\\", "\\\\").replace("\n", "\\n").replace("|", "\\|")).append('|');
        return sb.toString();
    }

    private static TreeMap<String, String> load(Path file) {
        TreeMap<String, String> m = new TreeMap<>();
        if (!Files.exists(file)) return m;
        try {
            for (String line : Files.readAllLines(file, StandardCharsets.ISO_8859_1)) {
                int tab = line.indexOf('\t');
                if (tab > 0) m.put(line.substring(0, tab), line.substring(tab + 1));
            }
        } catch (IOException e) {
            throw new IllegalStateException(e);
        }
        return m;
    }

    private static void save(Path file, TreeMap<String, String> m) {
        try {
            if (file.getParent() != null) Files.createDirectories(file.getParent());
            StringBuilder sb = new StringBuilder();
            for (Map.Entry<String, String> e : m.entrySet()) sb.append(e.getKey()).append('\t').append(e.getValue()).append('\n');
            Files.writeString(file, sb.toString(), StandardCharsets.ISO_8859_1);
        } catch (IOException e) {
            throw new IllegalStateException(e);
        }
    }
}
