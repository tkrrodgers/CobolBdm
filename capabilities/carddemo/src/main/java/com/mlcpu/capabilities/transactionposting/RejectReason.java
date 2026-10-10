package com.mlcpu.capabilities.transactionposting;

/** Why a daily transaction is not posted (WS-VALIDATION-FAIL-REASON and its description). */
public enum RejectReason {
    INVALID_CARD(100, "INVALID CARD NUMBER FOUND"),
    ACCOUNT_NOT_FOUND(101, "ACCOUNT RECORD NOT FOUND"),
    OVERLIMIT(102, "OVERLIMIT TRANSACTION"),
    RECEIVED_AFTER_EXPIRATION(103, "TRANSACTION RECEIVED AFTER ACCT EXPIRATION");

    public final int code;
    public final String description;

    RejectReason(int code, String description) {
        this.code = code;
        this.description = description;
    }
}
