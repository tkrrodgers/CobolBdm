package com.mlcpu.jobol;

import java.util.ArrayList;
import java.util.Comparator;
import java.util.List;
import java.util.Map;
import java.util.TreeMap;

/**
 * The JOBOL index: per rule, the heaviest occurrences up to the rule's cap are summed by severity weight
 * (BLOCKER 30, MAJOR 8, MINOR 2) and the total is clamped to 100. The cap keeps one noisy rule from dominating, so
 * the index rises with the number of distinct COBOL habits a type shows.
 */
public final class Scorer {
    private Scorer() {
    }

    public static int index(List<Finding> findings, Config cfg) {
        Map<String, List<Finding>> byRule = new TreeMap<>();
        for (Finding f : findings) byRule.computeIfAbsent(f.rule(), k -> new ArrayList<>()).add(f);
        int points = 0;
        for (Map.Entry<String, List<Finding>> e : byRule.entrySet()) {
            Rules.Spec spec = cfg.rule(e.getKey());
            int cap = spec == null ? 0 : spec.cap();
            List<Finding> fs = new ArrayList<>(e.getValue());
            fs.sort(Comparator.comparingInt((Finding f) -> f.severity().weight).reversed());
            for (int i = 0; i < Math.min(cap, fs.size()); i++) points += fs.get(i).severity().weight;
        }
        return Math.min(100, points);
    }

    public static String grade(double index) {
        if (index <= 10) return "A";
        if (index <= 25) return "B";
        if (index <= 45) return "C";
        if (index <= 70) return "D";
        return "F";
    }

    /** Grades from best to worst. */
    public static int rank(String grade) {
        return "ABCDF".indexOf(grade);
    }
}
