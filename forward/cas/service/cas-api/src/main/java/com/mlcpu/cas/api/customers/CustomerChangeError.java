package com.mlcpu.cas.api.customers;

/** A change that was not applied: its position in the batch (from 0, comment lines included), its key and the reason. */
public record CustomerChangeError(int index, String customerKey, ChangeRejection reason) {}
