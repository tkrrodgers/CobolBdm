package com.mlcpu.cas.api.ports;

import com.mlcpu.cas.api.accounts.RejectionReason;
import com.mlcpu.cas.api.accounts.TransactionInput;
import java.time.LocalDateTime;

/** A refused transaction as received, the account its card belongs to (null when the card is unknown), the reason and when. */
public record RejectionRecord(TransactionInput transaction, Long accountId, RejectionReason reason, LocalDateTime rejectedAt) {}
