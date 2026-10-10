package com.mlcpu.sidecar;

import com.mlcpu.rt.CobField;
import com.mlcpu.rt.CobolProgram;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.util.ArrayList;
import java.util.List;

/** Structural check of a compiled program: paragraph methods, business fields, and the @CobolProgram identity. */
public final class TreeApiChecker {

    public static final class Structure {
        public String className;
        public String cobolProgramId;
        public List<String> methods = new ArrayList<>();
        public List<String> fields = new ArrayList<>();
    }

    public Structure scan(Class<?> c) {
        Structure s = new Structure();
        s.className = c.getName();
        CobolProgram a = c.getAnnotation(CobolProgram.class);
        s.cobolProgramId = a == null ? null : a.programId();
        for (Method m : c.getDeclaredMethods()) if (m.getName().matches("p\\d+_.*")) s.methods.add(m.getName());
        for (Field f : c.getDeclaredFields()) if (f.getType() == CobField.class) s.fields.add(f.getName());
        return s;
    }
}
