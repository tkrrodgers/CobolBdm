package com.mlcpu.jobol;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/** The estate audit, serialized as jobol-report.json; field order is the JSON order. */
public final class Report {
    public static final String TOOL = "jobol-auditor";
    public static final String VERSION = "1.1.0";

    public String tool = TOOL;
    public String version = VERSION;
    /** SHA-256 over the effective rules and gate, so two reports are comparable only when this matches. */
    public String rulesetHash;
    public String root;
    public Summary summary = new Summary();
    public Gate gate = new Gate();
    public Delta baseline;
    public List<TypeReport> types = new ArrayList<>();
    public List<ParseError> parseErrors = new ArrayList<>();
    public List<RuleRef> rules = new ArrayList<>();

    public static final class Summary {
        public int files;
        public int parsed;
        public int types;
        /** LOC-weighted JOBOL index over the modern lane (0 = idiomatic Java, 100 = COBOL in Java syntax). */
        public double estateIndex;
        public String estateGrade;
        /** Share of audited code (modern + compat + faithful) that is in the modern lane. */
        public double modernLanePct;
        public Map<String, Lane> lanes = new LinkedHashMap<>();
        public Map<String, Integer> grades = new LinkedHashMap<>();
        public Map<String, Integer> findingsBySeverity = new LinkedHashMap<>();
        public Map<String, Integer> findingsByRule = new LinkedHashMap<>();
        /** Modern idioms per 1000 lines of the modern lane. */
        public Map<String, Double> signalsPerKloc = new LinkedHashMap<>();
    }

    public static final class Lane {
        public int files;
        public int types;
        public int loc;
    }

    public static final class Gate {
        public boolean passed;
        public List<String> reasons = new ArrayList<>();
        public Config.Gate thresholds;
    }

    public static final class Delta {
        public String baselineFile;
        public boolean comparable;
        public double previousIndex;
        public double currentIndex;
        public double change;
        public int newBlockers;
        public int resolvedFindings;
        public int newFindings;
        public List<TypeDelta> regressed = new ArrayList<>();
        public List<TypeDelta> improved = new ArrayList<>();
        public List<String> added = new ArrayList<>();
        public List<String> removed = new ArrayList<>();
    }

    public record TypeDelta(String key, int previous, int current) {
    }

    public record ParseError(String file, String message) {
    }

    public record RuleRef(String id, String name, String severity, List<String> zones, int cap, String summary, String fix) {
    }
}
