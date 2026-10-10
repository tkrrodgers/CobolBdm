package com.mlcpu.rt;

import java.util.LinkedHashMap;
import java.util.Map;

/** The equivalence boundary: values keyed by COBOL name, in declaration order. */
public final class ProgramState {
    private final LinkedHashMap<String, Object> values = new LinkedHashMap<>();

    public ProgramState set(String name, Object value) {
        values.put(name, value);
        return this;
    }

    public Object get(String name) { return values.get(name); }

    public Map<String, Object> fields() { return values; }
}
