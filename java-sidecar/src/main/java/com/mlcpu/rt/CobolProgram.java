package com.mlcpu.rt;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

@Retention(RetentionPolicy.RUNTIME)
@Target(ElementType.TYPE)
public @interface CobolProgram {
    String programId();
    String source() default "";
    String area() default "";
    double readiness() default 0.0;
}
