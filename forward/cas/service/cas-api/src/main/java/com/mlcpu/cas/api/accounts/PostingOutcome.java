package com.mlcpu.cas.api.accounts;

import java.math.BigDecimal;

/** What happened to one transaction. */
public sealed interface PostingOutcome permits PostingOutcome.Posted, PostingOutcome.Rejected {

    String transactionId();

    /** Posted to {@code accountId}; {@code newBalance} is the account's current balance after the posting. */
    record Posted(String transactionId, long accountId, BigDecimal newBalance) implements PostingOutcome {}

    /** Refused for {@code reason}; only the rejection log changed. */
    record Rejected(String transactionId, RejectionReason reason) implements PostingOutcome {}
}
