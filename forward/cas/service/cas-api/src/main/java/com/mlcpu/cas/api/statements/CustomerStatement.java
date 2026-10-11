package com.mlcpu.cas.api.statements;

import java.math.BigDecimal;
import java.time.YearMonth;
import java.util.List;

/** The customer statement (SPEC.md section 6): the SAM profile's figures and one entry per linked account, in link order. */
public record CustomerStatement(String customerId, YearMonth cycle, String name, int ordersYtd, BigDecimal monthlyAverage,
                                List<AccountStatement> accounts) {
    public CustomerStatement {
        accounts = List.copyOf(accounts);
    }
}
