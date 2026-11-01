package com.mlcpu.jobol;

import java.util.Locale;

/**
 * The lane a source file belongs to. Only MODERN is held to the full JOBOL rule set: FAITHFUL code is the
 * byte-exact COBOL translation and is expected to look like COBOL; COMPAT code (record codecs, dataset
 * adapters, strangler) legitimately handles fixed-width layouts.
 */
public enum Zone {
    MODERN, COMPAT, FAITHFUL, TEST, EXCLUDED;

    public static Zone parse(String s) {
        return valueOf(s.trim().toUpperCase(Locale.ROOT));
    }

    public String id() {
        return name().toLowerCase(Locale.ROOT);
    }

    public boolean audited() {
        return this == MODERN || this == COMPAT;
    }
}
