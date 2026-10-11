package com.mlcpu.cas.api.statements;

import java.math.BigDecimal;
import java.util.List;

/** One linked card account's cycle: balances, activity by category, interest and rejections (SPEC.md STMT-04..09). */
public record AccountStatement(long accountId, BigDecimal openingBalance, BigDecimal closingBalance, List<CategoryActivity> activity,
                               BigDecimal interest, List<StatementRejection> rejections) {
    public AccountStatement {
        activity = List.copyOf(activity);
        rejections = List.copyOf(rejections);
    }
}
