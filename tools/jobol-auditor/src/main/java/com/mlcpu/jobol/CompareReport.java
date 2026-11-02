package com.mlcpu.jobol;

import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.TreeMap;

/**
 * The distance of a modern estate from the original Java it was (indirectly) generated from: jobol-audit compare.
 * Every list is sorted and nothing depends on the clock, so two runs over the same trees give the same bytes.
 */
public final class CompareReport {
    public static final String VERSION = "1.0.0";

    public String tool = "jobol-audit compare";
    public String version = VERSION;
    public String estate;
    public List<String> originalRoots = new ArrayList<>();
    public String modernRoot;
    public String cobolRoot;
    /** all: every original type; reached: types J3S compiled (COBOL CLASS headers) plus types the modern estate names */
    public String scope;
    public Summary summary = new Summary();
    public List<TypeDiff> matched = new ArrayList<>();
    public List<Missing> originalOnly = new ArrayList<>();
    public List<String> outOfScope = new ArrayList<>();
    public List<ModernOnly> modernOnly = new ArrayList<>();
    public List<Ranked> topByDistance = new ArrayList<>();
    public List<Gap> gaps = new ArrayList<>();
    public List<String> parseErrors = new ArrayList<>();
    public List<String> notes = new ArrayList<>();

    public static final class Summary {
        public int originalTypes;
        public int outOfScope;
        public int matched;
        public int matchedByFqn;
        public int matchedMoved;
        /** --align structural: types paired by shape, not name */
        public int matchedStructural;
        /** --align structural: matched type pairs whose modern simple name is exactly the original's */
        public Ratio typeNames = new Ratio();
        /** --align structural: matched type name pairs by class (exact, case, renamed, ...) */
        public Map<String, Integer> typeNameClasses = new TreeMap<>();
        /** --align structural: mean camel-token overlap (Dice, %) of matched type names */
        public double typeNameTokens;
        public int originalOnly;
        public int modernOnlyInfrastructure;
        public int modernOnlyOther;
        /** modern classes that are the explicit form of an original Lombok annotation (a nested XBuilder for @Builder) */
        public int modernOnlyLombok;
        /** public/protected original members (of effectively public types) present unchanged in modern */
        public Ratio api = new Ratio();
        /** the same, leaving out methods and types J3S never compiled (needs --cobol) */
        public Ratio apiReached = new Ratio();
        /** aligned identifier pairs whose modern spelling is exact */
        public Ratio names = new Ratio();
        /** original methods and constructors present with the same name, parameter types, return type and static flag */
        public Ratio signatures = new Ratio();
        public Ratio signaturesReached = new Ratio();
        /** name pairs by class (exact, truncated, ...) and by element (field, method, param, local) */
        public Map<String, Map<String, Integer>> nameClasses = new TreeMap<>();
        /** the length of the modern spelling of truncated names, to show where the cut falls */
        public Map<Integer, Integer> truncatedLengths = new TreeMap<>();
        /** for truncated names: could a unique identifier elsewhere in the modern estate complete them? */
        public Map<String, Integer> truncationCompletion = new TreeMap<>();
        public Map<String, Pair> idioms = new TreeMap<>();
        public Map<String, Pair> typeUses = new TreeMap<>();
        public Structure originalStructure = new Structure();
        public Structure modernStructure = new Structure();
        public Map<String, Integer> gapCounts = new TreeMap<>();
    }

    public static final class Ratio {
        public int total;
        public int kept;
        public double pct;

        void add(boolean ok) {
            total++;
            if (ok) kept++;
        }

        void close() {
            pct = total == 0 ? 100.0 : Math.round(1000.0 * kept / total) / 10.0;
        }
    }

    /** original: in the original members that have a modern counterpart; originalAll: in the whole original type. */
    public static final class Pair {
        public int original;
        public int modern;
        public int originalAll;

        public Pair() {
        }

        public Pair(int original, int modern) {
            this.original = original;
            this.modern = modern;
        }
    }

    public static final class Structure {
        public int types;
        public int loc;
        public int methods;
        public double avgMethodLines;
        public int maxMethodLines;
        public int branches;
        public int constructors;
        public int factories;
        public int builders;
        public int equalsHashCode;
        public int toStrings;
        transient long methodLines;
    }

    public static final class TypeStructure {
        public int loc;
        public int methods;
        public double avgMethodLines;
        public int maxMethodLines;
        public int branches;
    }

    public static final class TypeDiff {
        public String original;
        public String modern;
        /** fqn: same name; moved: same simple name, other package; relocated: nesting changed; structural: by shape */
        public String alignment;
        /** --align structural: the shape similarity of a structural pair */
        public Double structuralScore;
        public String originalFile;
        public String modernFile;
        public Pair2 kind = new Pair2();
        public Pair2 visibility = new Pair2();
        public Pair2 modifiers = new Pair2();
        public List<String> supertypesOriginal = new ArrayList<>();
        public List<String> supertypesModern = new ArrayList<>();
        public List<String> supertypesLost = new ArrayList<>();
        public List<String> supertypesAdded = new ArrayList<>();
        public List<String> lombok = new ArrayList<>();
        public Members fields = new Members();
        public Members methods = new Members();
        public Members enumConstants = new Members();
        public Ratio api = new Ratio();
        public Ratio signatures = new Ratio();
        public Map<String, Integer> names = new TreeMap<>();
        public List<Rename> renames = new ArrayList<>();
        public Map<String, Pair> idioms = new TreeMap<>();
        public Map<String, Pair> typeUses = new TreeMap<>();
        public TypeStructure originalStructure = new TypeStructure();
        public TypeStructure modernStructure = new TypeStructure();
        public List<String> constructorsOriginal = new ArrayList<>();
        public List<String> constructorsModern = new ArrayList<>();
        public List<String> factoriesOriginal = new ArrayList<>();
        public List<String> factoriesModern = new ArrayList<>();
        public Pair2 builder = new Pair2();
        public Map<String, Pair2> objectMethods = new TreeMap<>();
        public double distance;
        public double distancePerMember;
    }

    public static final class Pair2 {
        public String original;
        public String modern;

        public Pair2() {
        }

        public Pair2(String original, String modern) {
            this.original = original;
            this.modern = modern;
        }

        boolean same() {
            return java.util.Objects.equals(original, modern);
        }
    }

    public static final class Members {
        public int original;
        public int modern;
        public int matched;
        public List<String> missing = new ArrayList<>();
        public List<String> extra = new ArrayList<>();
        public List<Change> changed = new ArrayList<>();
    }

    public record Change(String original, String modern, List<String> changes) {
    }

    public record Rename(String element, String member, String original, String modern, String kind) {
    }

    public record Missing(String fqn, String kind, String file, boolean reachedByJ3s, int apiMembers, int members) {
    }

    public record ModernOnly(String fqn, String kind, String file, String category, String reason) {
    }

    public record Ranked(String original, String modern, double distance, double distancePerMember, String top) {
    }

    /** One concrete difference, tagged with a mechanical pattern code (FAMILY.DETAIL). */
    public record Gap(String pattern, String type, String member, String original, String modern, String detail) {
    }
}
