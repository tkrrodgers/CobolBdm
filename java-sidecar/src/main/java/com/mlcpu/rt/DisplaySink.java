package com.mlcpu.rt;

import java.io.OutputStream;
import java.io.PrintStream;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;

/** Where DISPLAY output goes: capturing (default), a stream (SYSOUT), or nowhere. */
public abstract class DisplaySink {
    private static DisplaySink current = new Capturing();

    public static DisplaySink current() { return current; }

    public static void set(DisplaySink s) { current = s; }

    public abstract void write(String text, boolean newline);

    public List<String> lines() { return List.of(); }

    public static final class Capturing extends DisplaySink {
        private final List<String> lines = new ArrayList<>();
        private final StringBuilder pending = new StringBuilder();
        private final int max;

        public Capturing() { this(100_000); }
        public Capturing(int max) { this.max = max; }

        @Override
        public void write(String text, boolean newline) {
            pending.append(text);
            if (newline) {
                if (lines.size() < max) lines.add(pending.toString());
                pending.setLength(0);
            }
        }

        @Override
        public List<String> lines() { return lines; }
    }

    public static final class ToStream extends DisplaySink {
        private final PrintStream out;

        public ToStream(OutputStream os) {
            this.out = new PrintStream(os, false, StandardCharsets.ISO_8859_1);
        }

        @Override
        public void write(String text, boolean newline) {
            out.print(text);
            if (newline) out.print('\n');
        }

        public void flush() { out.flush(); }
    }

    public static final class NoOp extends DisplaySink {
        @Override
        public void write(String text, boolean newline) { }
    }
}
