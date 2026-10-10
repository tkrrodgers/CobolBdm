package com.mlcpu.carddemo.platform.records;

import java.io.IOException;

/** A dataset operation failed; {@code status} is the equivalent two-digit file status (35 not found, 22 duplicate, 23 no record). */
public final class DatasetException extends IOException {
    private final String status;

    public DatasetException(String message, String status) {
        super(message);
        this.status = status;
    }

    public String status() {
        return status;
    }
}
