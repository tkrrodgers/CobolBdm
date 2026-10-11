package com.mlcpu.cas.api.customers;

/** Why a customer change was not applied, with the legacy message. */
public enum ChangeRejection {
    OUT_OF_SEQUENCE("TRANSACTION OUT OF SEQUENCE"),
    INVALID_CODE("INVALID TRAN CODE:"),
    NO_MATCHING_KEY("NO MATCHING KEY:"),
    DUPLICATE_KEY("DUPLICATE KEY:"),
    NOT_NUMERIC("DATA IS NOT NUMERIC"),
    INVALID_ACTION("INVALID ACTION CODE"),
    INVALID_FIELD("FIELD NAME INVALID");

    private final String message;

    ChangeRejection(String message) {
        this.message = message;
    }

    public String message() {
        return message;
    }
}
