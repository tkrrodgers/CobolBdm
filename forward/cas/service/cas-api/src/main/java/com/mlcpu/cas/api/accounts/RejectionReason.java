package com.mlcpu.cas.api.accounts;

/** Why a transaction is not posted, with the legacy reason code and description (DUPLICATE_TRANSACTION is new: SPEC.md D-8). */
public enum RejectionReason {
    INVALID_CARD(100, "INVALID CARD NUMBER FOUND"),
    ACCOUNT_NOT_FOUND(101, "ACCOUNT RECORD NOT FOUND"),
    OVERLIMIT(102, "OVERLIMIT TRANSACTION"),
    EXPIRED_ACCOUNT(103, "TRANSACTION RECEIVED AFTER ACCT EXPIRATION"),
    DUPLICATE_TRANSACTION(104, "TRANSACTION ID ALREADY POSTED");

    private final int code;
    private final String description;

    RejectionReason(int code, String description) {
        this.code = code;
        this.description = description;
    }

    public int code() {
        return code;
    }

    public String description() {
        return description;
    }
}
