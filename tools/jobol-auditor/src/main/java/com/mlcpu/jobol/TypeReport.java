package com.mlcpu.jobol;

import java.util.List;
import java.util.Map;

/**
 * The audit of one type declaration (nested types are separate entries). index is the JOBOL index 0..100 (higher is
 * more COBOL-shaped); metrics are structural measurements, signals count modern-Java idioms.
 */
public record TypeReport(
    String file,
    String type,
    String kind,
    String zone,
    int loc,
    int index,
    String grade,
    Map<String, Number> metrics,
    Map<String, Integer> signals,
    List<Finding> findings) {

    public String key() {
        return file + "#" + type;
    }
}
