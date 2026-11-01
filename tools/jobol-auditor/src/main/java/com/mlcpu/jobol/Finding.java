package com.mlcpu.jobol;

/** One rule violation at a source line, attributed to the innermost enclosing type. */
public record Finding(String rule, Severity severity, String file, int line, String type, String message) {
}
