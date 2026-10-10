package com.mlcpu.rt;

import java.util.ArrayList;
import java.util.List;

/** Frontier hits and loop cuts observed during a run (published as @FRONTIER_HITS). */
public final class FrontierHits {
    private FrontierHits() {}

    private static final List<String> hits = new ArrayList<>();
    private static final List<String> loopCuts = new ArrayList<>();

    public static synchronized void record(String stepId, String reason) { hits.add(stepId + ": " + reason); }
    public static synchronized void loopCut(String stepId) { loopCuts.add(stepId); }
    public static synchronized List<String> hits() { return List.copyOf(hits); }
    public static synchronized List<String> loopCuts() { return List.copyOf(loopCuts); }
    public static synchronized void clear() { hits.clear(); loopCuts.clear(); }
}
