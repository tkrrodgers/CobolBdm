package com.mlcpu.cas.api.statements;

import com.mlcpu.cas.api.accounts.RejectionReason;
import java.math.BigDecimal;
import java.time.LocalDateTime;

/** A transaction refused during the cycle. */
public record StatementRejection(String transactionId, RejectionReason reason, BigDecimal amount, LocalDateTime rejectedAt) {}
