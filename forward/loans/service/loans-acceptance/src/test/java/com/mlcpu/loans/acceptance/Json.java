package com.mlcpu.loans.acceptance;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.node.ArrayNode;
import com.fasterxml.jackson.databind.node.JsonNodeFactory;
import com.fasterxml.jackson.databind.node.ObjectNode;
import java.lang.reflect.ParameterizedType;
import java.lang.reflect.RecordComponent;
import java.lang.reflect.Type;
import java.math.BigDecimal;
import java.time.LocalDate;
import java.util.ArrayList;
import java.util.List;

/**
 * Canonical JSON for the API's records, both ways (the CAS canon; the forward-engineering design, FE section 5.3). Records are objects
 * with their components in declaration order; an implementation of a sealed interface also carries "@type" (its simple name).
 * BigDecimal is a string with its scale ("12.30"); enums by name; LocalDate "2023-01-28"; an absent nullable value is null;
 * booleans true and false; lists are arrays in the list's order.
 */
public final class Json {
    private Json() {}

    public static final ObjectMapper MAPPER = new ObjectMapper();
    static final JsonNodeFactory F = JsonNodeFactory.instance;

    // ------------------------------------------------------------------------------------------------ objects to JSON

    public static JsonNode of(Object value) {
        if (value == null) return F.nullNode();
        if (value instanceof String s) return F.textNode(s);
        if (value instanceof BigDecimal d) return F.textNode(d.toPlainString());
        if (value instanceof Integer i) return F.numberNode(i);
        if (value instanceof Long l) return F.numberNode(l);
        if (value instanceof Boolean b) return F.booleanNode(b);
        if (value instanceof Enum<?> e) return F.textNode(e.name());
        if (value instanceof LocalDate d) return F.textNode(d.toString());
        if (value instanceof List<?> list) {
            ArrayNode a = F.arrayNode();
            for (Object o : list) a.add(of(o));
            return a;
        }
        if (value.getClass().isRecord()) {
            ObjectNode o = F.objectNode();
            for (Class<?> i : value.getClass().getInterfaces())
                if (i.isSealed()) o.put("@type", value.getClass().getSimpleName());
            for (RecordComponent c : value.getClass().getRecordComponents()) {
                try {
                    c.getAccessor().setAccessible(true);
                    o.set(c.getName(), of(c.getAccessor().invoke(value)));
                } catch (ReflectiveOperationException e) {
                    throw new IllegalStateException(e);
                }
            }
            return o;
        }
        throw new IllegalArgumentException("no canonical JSON for " + value.getClass().getName());
    }

    // ------------------------------------------------------------------------------------------------ JSON to objects

    @SuppressWarnings("unchecked")
    public static <T> T to(JsonNode n, Type type) {
        if (n == null || n.isNull()) return null;
        if (type instanceof ParameterizedType p && p.getRawType() == List.class) {
            List<Object> list = new ArrayList<>();
            for (JsonNode e : n) list.add(to(e, p.getActualTypeArguments()[0]));
            return (T) list;
        }
        Class<?> c = (Class<?>) type;
        if (c == String.class) return (T) n.asText();
        if (c == int.class || c == Integer.class) return (T) Integer.valueOf(n.asInt());
        if (c == long.class || c == Long.class) return (T) Long.valueOf(n.asLong());
        if (c == boolean.class || c == Boolean.class) return (T) Boolean.valueOf(n.asBoolean());
        if (c == BigDecimal.class) return (T) new BigDecimal(n.asText());
        if (c == LocalDate.class) return (T) LocalDate.parse(n.asText());
        if (c.isEnum()) return (T) Enum.valueOf(c.asSubclass(Enum.class), n.asText());
        if (c.isRecord()) {
            RecordComponent[] cs = c.getRecordComponents();
            Object[] args = new Object[cs.length];
            for (int i = 0; i < cs.length; i++) {
                JsonNode v = n.get(cs[i].getName());
                if (v == null) throw new IllegalArgumentException(c.getSimpleName() + ": missing " + cs[i].getName() + " in " + n);
                args[i] = to(v, cs[i].getGenericType());
                if (args[i] == null && cs[i].getType().isPrimitive()) throw new IllegalArgumentException(c.getSimpleName() + "." + cs[i].getName() + " is null");
            }
            try {
                var ctor = c.getDeclaredConstructor(java.util.Arrays.stream(cs).map(RecordComponent::getType).toArray(Class[]::new));
                ctor.setAccessible(true);
                return (T) ctor.newInstance(args);
            } catch (ReflectiveOperationException e) {
                throw new IllegalArgumentException("cannot build " + c.getSimpleName() + ": " + e, e);
            }
        }
        throw new IllegalArgumentException("cannot read " + type.getTypeName() + " from JSON");
    }

    // ------------------------------------------------------------------------------------------------ differences

    /** The first differences between expected and actual (at most {@code limit}), each "path: expected X, got Y". */
    public static List<String> differences(String path, JsonNode expected, JsonNode actual, int limit) {
        List<String> out = new ArrayList<>();
        diff(path, expected, actual, out, limit);
        return out;
    }

    private static void diff(String path, JsonNode e, JsonNode a, List<String> out, int limit) {
        if (out.size() >= limit) return;
        if (e == null) e = F.missingNode();
        if (a == null) a = F.missingNode();
        if (e.isObject() && a.isObject()) {
            var names = new java.util.LinkedHashSet<String>();
            e.fieldNames().forEachRemaining(names::add);
            a.fieldNames().forEachRemaining(names::add);
            for (String n : names) diff(path + "." + n, e.get(n), a.get(n), out, limit);
            return;
        }
        if (e.isArray() && a.isArray()) {
            for (int i = 0; i < Math.min(e.size(), a.size()); i++) diff(path + "[" + i + "]", e.get(i), a.get(i), out, limit);
            if (e.size() != a.size() && out.size() < limit)
                out.add(path + ": expected " + e.size() + " entries, got " + a.size()
                        + (a.size() > e.size() ? "; first extra: " + clip(a.get(e.size()).toString()) : "; first missing: " + clip(e.get(a.size()).toString())));
            return;
        }
        if (e.isNumber() && a.isNumber()) {
            if (e.decimalValue().compareTo(a.decimalValue()) != 0) out.add(path + ": expected " + show(e) + ", got " + show(a));
            return;
        }
        if (!e.equals(a)) out.add(path + ": expected " + show(e) + ", got " + show(a));
    }

    static String show(JsonNode n) {
        if (n.isMissingNode()) return "(nothing)";
        return clip(n.isTextual() ? "\"" + n.asText() + "\"" : n.toString());
    }

    static String clip(String s) {
        return s.length() <= 300 ? s : s.substring(0, 300) + " ...";
    }
}
