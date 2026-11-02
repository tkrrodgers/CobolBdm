package com.mlcpu.sidecar;

import com.google.gson.JsonObject;
import java.util.List;
import java.util.Map;

/** Request/response POJOs (§7.2). */
public final class Protocol {
    private Protocol() {}

    public static final class BatteryRow {
        public Map<String, Object> inputs;
        public Map<String, Object> expect;
    }

    public static final class VerifyRequest {
        public JsonObject ir;
        public List<BatteryRow> battery;
        public boolean useLir;
        public List<JsonObject> callees;
        public String loopPolicy;
        public Map<String, Integer> loopLimits;
        public String javaSourceDir;
        public Boolean includeJavaSource;
        public boolean transpileOnly;
        public boolean composeStorage;
        /** Typed rule slices behind generated contracts; requires composeStorage. */
        public boolean extractRules;
        public JsonObject scope;
    }
}
