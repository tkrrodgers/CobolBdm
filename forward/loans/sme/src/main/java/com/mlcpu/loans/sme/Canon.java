package com.mlcpu.loans.sme;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.node.ArrayNode;
import com.fasterxml.jackson.databind.node.JsonNodeFactory;
import com.fasterxml.jackson.databind.node.ObjectNode;
import java.lang.reflect.RecordComponent;
import java.math.BigDecimal;
import java.time.LocalDate;
import java.util.List;

/**
 * The acceptance runner's canonical JSON of the API's records (service/loans-acceptance Json.of, copied: the SME tools do not
 * compile the test sources), so a pool's expected values are written as the runner compares them.
 */
public final class Canon {

    private static final JsonNodeFactory F = JsonNodeFactory.instance;

    private Canon() {
    }

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
}
