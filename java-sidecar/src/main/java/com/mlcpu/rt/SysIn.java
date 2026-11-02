package com.mlcpu.rt;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStreamReader;
import java.nio.charset.StandardCharsets;

/** ACCEPT without FROM reads the next SYSIN line. */
public final class SysIn {
    private SysIn() {}

    private static BufferedReader reader = new BufferedReader(new InputStreamReader(System.in, StandardCharsets.ISO_8859_1));

    public static void set(BufferedReader r) {
        try (RunUnit.Guard guard = RunUnit.guard()) { reader = java.util.Objects.requireNonNull(r); }
    }
    static BufferedReader current() {
        try (RunUnit.Guard guard = RunUnit.guard()) { return reader; }
    }

    public static String readLine() {
        try {
            return reader.readLine();
        } catch (IOException e) {
            throw new java.io.UncheckedIOException("SYSIN read failed", e);
        }
    }
}
