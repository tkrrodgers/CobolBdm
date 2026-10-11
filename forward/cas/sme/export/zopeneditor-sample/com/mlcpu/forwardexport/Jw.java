package com.mlcpu.forwardexport;

import java.math.BigDecimal;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/** A minimal JSON writer for the forward-engineering scenario export (maps, lists, strings, numbers, BigDecimal as text). */
public final class Jw {
    private Jw() {}

    public static Map<String, Object> obj(Object... kv) {
        Map<String, Object> m = new LinkedHashMap<>();
        for (int i = 0; i < kv.length; i += 2) m.put((String) kv[i], kv[i + 1]);
        return m;
    }

    public static List<Object> list() {
        return new ArrayList<>();
    }

    public static String write(Object v) {
        StringBuilder sb = new StringBuilder();
        write(sb, v, 0);
        return sb.append('\n').toString();
    }

    @SuppressWarnings("unchecked")
    private static void write(StringBuilder sb, Object v, int depth) {
        if (v == null) sb.append("null");
        else if (v instanceof String s) str(sb, s);
        else if (v instanceof BigDecimal d) str(sb, d.toPlainString());
        else if (v instanceof Number || v instanceof Boolean) sb.append(v);
        else if (v instanceof Map<?, ?> m) {
            sb.append('{');
            boolean first = true;
            for (Map.Entry<?, ?> e : m.entrySet()) {
                if (!first) sb.append(',');
                first = false;
                if (depth < 2) sb.append('\n').append("  ".repeat(depth + 1));
                str(sb, (String) e.getKey());
                sb.append(':');
                write(sb, e.getValue(), depth + 1);
            }
            if (depth < 2 && !m.isEmpty()) sb.append('\n').append("  ".repeat(depth));
            sb.append('}');
        } else if (v instanceof List<?> l) {
            sb.append('[');
            for (int i = 0; i < l.size(); i++) {
                if (i > 0) sb.append(',');
                if (depth < 3) sb.append('\n').append("  ".repeat(depth + 1));
                write(sb, l.get(i), depth + 1);
            }
            if (depth < 3 && !l.isEmpty()) sb.append('\n').append("  ".repeat(depth));
            sb.append(']');
        } else throw new IllegalArgumentException("JSON: " + v.getClass());
    }

    private static void str(StringBuilder sb, String s) {
        sb.append('"');
        for (char c : s.toCharArray()) {
            switch (c) {
                case '"' -> sb.append("\\\"");
                case '\\' -> sb.append("\\\\");
                case '\n' -> sb.append("\\n");
                case '\r' -> sb.append("\\r");
                case '\t' -> sb.append("\\t");
                default -> {
                    if (c < 0x20 || c > 0x7e) sb.append(String.format("\\u%04x", (int) c));
                    else sb.append(c);
                }
            }
        }
        sb.append('"');
    }

    /** Text without trailing blanks or x'00' storage bytes. */
    public static String text(String s) {
        int e = s.length();
        while (e > 0 && (s.charAt(e - 1) == ' ' || s.charAt(e - 1) == '\0')) e--;
        return s.substring(0, e);
    }

    /** "2022-06-10 19:27:53.000000" or DB2 "2022-07-05-10.11.12.000000" as "2022-06-10T19:27:53.000000"; null when blank or other. */
    public static String timestamp(String s) {
        String t = text(s);
        var m = java.util.regex.Pattern.compile("(\\d{4}-\\d{2}-\\d{2})[ -](\\d{2})[:.](\\d{2})[:.](\\d{2})\\.(\\d{6})").matcher(t);
        return m.matches() ? m.group(1) + "T" + m.group(2) + ":" + m.group(3) + ":" + m.group(4) + "." + m.group(5) : null;
    }

    /** An ISO date, or null when blank or not a date. */
    public static String date(String s) {
        String t = text(s);
        try {
            return java.time.LocalDate.parse(t).toString();
        } catch (RuntimeException e) {
            return null;
        }
    }

    public static void save(String dir, String name, Object json) {
        try {
            java.nio.file.Path p = java.nio.file.Path.of(dir, name);
            java.nio.file.Files.createDirectories(p.getParent());
            java.nio.file.Files.writeString(p, write(json));
        } catch (java.io.IOException e) {
            throw new java.io.UncheckedIOException(e);
        }
    }

    public static String outDir() {
        String d = System.getenv("FORWARD_EXPORT_DIR");
        if (d == null || d.isBlank()) throw new IllegalStateException("FORWARD_EXPORT_DIR is not set");
        return d;
    }
}
