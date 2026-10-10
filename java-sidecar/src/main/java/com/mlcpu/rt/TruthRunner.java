package com.mlcpu.rt;

import java.util.LinkedHashMap;
import java.util.Map;

/**
 * Conformance truth-table driver (§11.1): seed input fields, run the generated program, print each requested output.
 * Numeric outputs print as plain decimals; alphanumeric outputs with trailing spaces removed.
 * <pre>TruthRunner --program TRUTH01 --in WS-A=12.5 --in WS-B=3 --out WS-C [--out WS-D]</pre>
 * Prints one line per output: {@code NAME=value}, then {@code @OUTCOME=...}.
 */
public final class TruthRunner {
    private TruthRunner() {}

    public static void main(String[] args) {
        String program = null;
        Map<String, String> inputs = new LinkedHashMap<>();
        java.util.List<String> outputs = new java.util.ArrayList<>();
        for (int i = 0; i + 1 < args.length; i += 2) {
            switch (args[i]) {
                case "--program" -> program = args[i + 1];
                case "--in" -> {
                    String[] kv = args[i + 1].split("=", 2);
                    inputs.put(kv[0].toUpperCase(), kv.length > 1 ? kv[1] : "");
                }
                case "--out" -> outputs.add(args[i + 1].toUpperCase());
                default -> throw new IllegalArgumentException("unknown argument " + args[i]);
            }
        }
        CobolBase p = ProgramRegistry.resolve(program);
        if (p == null) throw new IllegalArgumentException("program not found: " + program);
        ProgramState st = p.runProgram(inputs);
        for (String o : outputs) {
            Object v = st.get(o);
            System.out.println(o + "=" + (v == null ? "<no such field>" : stripTrailing(String.valueOf(v))));
        }
        System.out.println("@OUTCOME=" + st.get("@OUTCOME"));
    }

    private static String stripTrailing(String s) {
        int e = s.length();
        while (e > 0 && s.charAt(e - 1) == ' ') e--;
        return s.substring(0, e);
    }
}
