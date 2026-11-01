package com.mlcpu.jobol;

import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.regex.Pattern;

/**
 * How a modern identifier relates to the original one it stands for. The classes are ordered from best to worst and are
 * decided without any dictionary: only the two spellings (and, for temporaries, the declared type) are used.
 */
public final class Names {
    private Names() {
    }

    public enum Kind {
        /** identical */
        EXACT,
        /** identical ignoring case and word separators: the same words (intToEnumMap for INT_TO_ENUM_MAP, calculatedEmi for calculatedEMI) */
        CASE,
        /** modern is a strict prefix of the original: the tail was cut (disbursementDueDat for disbursementDueDate) */
        TRUNCATED,
        /** every camel token of modern is a prefix of the matching original token, in order (prinPortion for principalPortion) */
        ABBREVIATED,
        /** the same property in the other accessor style: installments() for getInstallments(), or the reverse */
        ACCESSOR_STYLE,
        /** a truncated or abbreviated stem with a counter appended (totalCumula2 for totalCumulativeInterest) */
        TRUNCATED_NUMBERED,
        /** a counter or temporary name where the original is meaningful (money2, t20_, bigDecimal3) */
        NUMBERED,
        /** any other spelling */
        RENAMED;

        public String id() {
            return name().toLowerCase(Locale.ROOT);
        }

        public boolean exact() {
            return this == EXACT;
        }

        /** The modern name is a damaged form of the original (it can be completed from the original's spelling). */
        public boolean damaged() {
            return this == TRUNCATED || this == ABBREVIATED || this == TRUNCATED_NUMBERED;
        }
    }

    private static final Pattern CAMEL = Pattern.compile("(?<=[a-z0-9])(?=[A-Z])|_|(?<=[A-Z])(?=[A-Z][a-z])|(?<=[A-Za-z])(?=[0-9])");
    private static final Pattern TRAILING_COUNTER = Pattern.compile("^(.*?[A-Za-z])_?(\\d+)_?$");
    private static final Pattern TEMP = Pattern.compile(
        "^(?:t|tmp|temp|v|w|x|r|res|ret|val|var|aux|wk|ws|lv)\\d*_?$|^[a-z]\\d+_?$|^[a-z]{1,3}_$|^_\\w*$");

    /** Lower-cased camel/underscore tokens; digits are their own token. */
    public static List<String> tokens(String name) {
        List<String> out = new ArrayList<>();
        for (String s : CAMEL.split(name)) if (!s.isEmpty()) out.add(s.toLowerCase(Locale.ROOT));
        return out;
    }

    /** Classifies without type information. */
    public static Kind classify(String original, String modern) {
        return classify(original, modern, null);
    }

    /**
     * @param modernType the declared type of the modern name (erased simple name), used to recognise type-derived
     *     temporaries such as bigDecimal3; may be null
     */
    public static Kind classify(String original, String modern, String modernType) {
        if (original.equals(modern)) return Kind.EXACT;
        if (original.equalsIgnoreCase(modern) || tokens(original).equals(tokens(modern))) return Kind.CASE;
        if (isTruncation(original, modern)) return Kind.TRUNCATED;
        if (isAbbreviation(original, modern)) return Kind.ABBREVIATED;
        if (isAccessorStyle(original, modern)) return Kind.ACCESSOR_STYLE;
        var m = TRAILING_COUNTER.matcher(modern);
        boolean originalCounted = TRAILING_COUNTER.matcher(original).matches();
        if (m.matches() && !originalCounted) {
            String stem = m.group(1);
            if (isTruncation(original, stem) || isAbbreviation(original, stem)) {
                return Kind.TRUNCATED_NUMBERED;
            }
            return Kind.NUMBERED;
        }
        if (isTemporary(modern, modernType) && !isTemporary(original, null)) return Kind.NUMBERED;
        return Kind.RENAMED;
    }

    /** getX/isX against x, either way round. */
    public static boolean isAccessorStyle(String a, String b) {
        return bean(a, b) || bean(b, a);
    }

    private static boolean bean(String plain, String bean) {
        if (plain.isEmpty()) return false;
        String cap = Character.toUpperCase(plain.charAt(0)) + plain.substring(1);
        return bean.equals("get" + cap) || bean.equals("is" + cap);
    }

    /** Modern is a strict prefix of the original (case-insensitive on the first letter), at least two characters long. */
    public static boolean isTruncation(String original, String modern) {
        if (modern.length() < 2 || modern.length() >= original.length()) return false;
        return original.regionMatches(true, 0, modern, 0, 1) && original.startsWith(modern.substring(1), 1);
    }

    /**
     * Token-wise: modern has no more tokens than the original, every modern token is a prefix of the original token at
     * the same position, and at least one token is shortened. Trailing original tokens may be dropped.
     */
    public static boolean isAbbreviation(String original, String modern) {
        List<String> o = tokens(original);
        List<String> m = tokens(modern);
        if (m.isEmpty() || m.size() > o.size()) return false;
        boolean shortened = m.size() < o.size();
        for (int i = 0; i < m.size(); i++) {
            String mt = m.get(i);
            String ot = o.get(i);
            if (!ot.startsWith(mt) && !skeleton(ot, mt)) return false;
            if (mt.length() < ot.length()) shortened = true;
        }
        return shortened;
    }

    /** An abbreviation by letters kept in order (pct for percentage, amt for amount): same first letter, at least two letters, a subsequence. */
    static boolean skeleton(String word, String abbreviation) {
        if (abbreviation.length() < 2 || abbreviation.length() >= word.length() || word.charAt(0) != abbreviation.charAt(0)) return false;
        int j = 0;
        for (int i = 0; i < word.length() && j < abbreviation.length(); i++) if (word.charAt(i) == abbreviation.charAt(j)) j++;
        return j == abbreviation.length();
    }

    /** A counter or scratch name: t20_, tmp, x1, a trailing-digit copy of the decapitalised type (bigDecimal3, money2). */
    public static boolean isTemporary(String name, String type) {
        if (TEMP.matcher(name).matches()) return true;
        var m = TRAILING_COUNTER.matcher(name);
        if (m.matches()) {
            String stem = m.group(1);
            return type != null && stem.equalsIgnoreCase(type);
        }
        return type != null && name.equalsIgnoreCase(type) && name.length() > 0;
    }
}
