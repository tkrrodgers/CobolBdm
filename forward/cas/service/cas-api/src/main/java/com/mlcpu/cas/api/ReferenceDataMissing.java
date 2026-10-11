package com.mlcpu.cas.api;

/**
 * Reference data a use case cannot do without is missing (SPEC.md section 4). {@code what} names the kind ("account", "card",
 * "disclosure group", "transaction type", "transaction category", "customer", "customer link") and {@code key} the key that was
 * looked up (digests and SPEC.md give each key's form).
 */
public final class ReferenceDataMissing extends RuntimeException {
    private final String what;
    private final String key;

    public ReferenceDataMissing(String what, String key) {
        super(what + " not found: " + key);
        this.what = what;
        this.key = key;
    }

    public String what() {
        return what;
    }

    public String key() {
        return key;
    }
}
