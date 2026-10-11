package com.mlcpu.cas.api.statements;

import java.time.YearMonth;

/** The statement of one SAM customer (5-character id) for one cycle. */
public record StatementQuery(String customerId, YearMonth cycle) {}
