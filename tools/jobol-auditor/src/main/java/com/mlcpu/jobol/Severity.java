package com.mlcpu.jobol;

/** Finding severity: its score weight and SARIF level. */
public enum Severity {
    BLOCKER(30, "error"),
    MAJOR(8, "warning"),
    MINOR(2, "note");

    public final int weight;
    public final String sarifLevel;

    Severity(int weight, String sarifLevel) {
        this.weight = weight;
        this.sarifLevel = sarifLevel;
    }
}
