package com.mlcpu.loans.api.ports;

/**
 * New ids derived from domain keys, so an adapter knows a record's key before the use case runs: the same external id always
 * gives the same loan id.
 */
public interface Ids {

    String loanId(String externalId);
}
