package com.mlcpu.jobol;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.Reader;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.EnumSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.regex.Pattern;

/** Auditor configuration: lane globs, rule overrides, naming vocabularies and the quality gate (jobol.json). */
public final class Config {
    /** First matching entry wins; a file no entry matches is MODERN. */
    public List<ZoneGlobs> zones = new ArrayList<>();
    public Map<String, RuleOverride> rules = new LinkedHashMap<>();
    public Gate gate = new Gate();
    public List<String> legacyRuntimePackages = new ArrayList<>();
    public List<String> legacyRuntimeTypes = new ArrayList<>();
    public List<String> cobolPrefixes = new ArrayList<>();
    public List<String> moneyWords = new ArrayList<>();
    public List<String> excludeDirs = new ArrayList<>();
    /**
     * compare: modern-only types matching these globs (besides the compat, faithful and test lanes) are pipeline
     * infrastructure (the generated job/application layer), not gaps.
     */
    public List<String> compareInfrastructureGlobs = new ArrayList<>();

    public static final class ZoneGlobs {
        public String zone;
        public List<String> globs = new ArrayList<>();
    }

    public static final class RuleOverride {
        public Boolean enabled;
        public String severity;
        public Integer cap;
        public List<String> zones;
    }

    public static final class Gate {
        public double maxEstateIndex = 25;
        public boolean failOnBlocker = true;
        /** The worst class grade the modern lane may contain. */
        public String worstGradeAllowed = "D";
        /** With a baseline: fail when the estate index rises by more than this. */
        public double maxRegression = 0;
    }

    private transient List<Map.Entry<Zone, List<Pattern>>> compiled;
    private transient Map<String, Rules.Spec> effective;

    public static Config load(Path file) throws IOException {
        try (Reader r = Files.newBufferedReader(file, StandardCharsets.UTF_8)) {
            return parse(r);
        }
    }

    public static Config defaults() {
        try (InputStream in = Config.class.getResourceAsStream("/default-jobol.json")) {
            if (in == null) throw new IllegalStateException("default-jobol.json missing from the classpath");
            return parse(new InputStreamReader(in, StandardCharsets.UTF_8));
        } catch (IOException e) {
            throw new IllegalStateException(e);
        }
    }

    public static String defaultsText() {
        try (InputStream in = Config.class.getResourceAsStream("/default-jobol.json")) {
            return new String(in.readAllBytes(), StandardCharsets.UTF_8);
        } catch (IOException e) {
            throw new IllegalStateException(e);
        }
    }

    private static Config parse(Reader r) {
        Gson gson = new GsonBuilder().create();
        Config c = gson.fromJson(r, Config.class);
        c.prepare();
        return c;
    }

    private void prepare() {
        compiled = new ArrayList<>();
        for (ZoneGlobs z : zones) {
            List<Pattern> ps = new ArrayList<>();
            for (String g : z.globs) ps.add(Glob.compile(g));
            compiled.add(Map.entry(Zone.parse(z.zone), ps));
        }
        effective = new LinkedHashMap<>();
        for (Rules.Spec s : Rules.ALL) {
            RuleOverride o = rules.get(s.id());
            if (o == null) {
                effective.put(s.id(), s);
                continue;
            }
            if (Boolean.FALSE.equals(o.enabled)) continue;
            Severity sev = o.severity == null ? s.severity() : Severity.valueOf(o.severity.toUpperCase());
            int cap = o.cap == null ? s.cap() : o.cap;
            Set<Zone> zs = s.zones();
            if (o.zones != null) {
                zs = EnumSet.noneOf(Zone.class);
                for (String z : o.zones) zs.add(Zone.parse(z));
            }
            effective.put(s.id(), new Rules.Spec(s.id(), s.name(), sev, zs, cap, s.summary(), s.fix()));
        }
    }

    /** The lane of a '/'-separated path relative to the audit root. */
    public Zone zoneOf(String relPath) {
        for (Map.Entry<Zone, List<Pattern>> e : compiled) {
            for (Pattern p : e.getValue()) {
                if (p.matcher(relPath).matches()) return e.getKey();
            }
        }
        return Zone.MODERN;
    }

    /** The rule after overrides, or null when it is disabled. */
    public Rules.Spec rule(String id) {
        return effective.get(id);
    }

    public boolean applies(String id, Zone zone) {
        Rules.Spec s = effective.get(id);
        return s != null && s.zones().contains(zone);
    }

    public List<Rules.Spec> effectiveRules() {
        return List.copyOf(effective.values());
    }
}
