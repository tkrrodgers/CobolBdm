package com.mlcpu.cas.api.ports;

import java.math.BigDecimal;
import java.time.LocalDate;
import java.time.YearMonth;

/**
 * A stored card account. Balances and limits have 2 decimals; {@code currentCycleDebit} accumulates negative amounts.
 * {@code groupId} is the disclosure group; {@code lastClosedCycle} is the last cycle interest was accrued for (null: never).
 */
public record AccountRecord(long accountId, String activeStatus, BigDecimal currentBalance, BigDecimal creditLimit,
                            BigDecimal cashCreditLimit, LocalDate openDate, LocalDate expirationDate, LocalDate reissueDate,
                            BigDecimal currentCycleCredit, BigDecimal currentCycleDebit, String addressZip, String groupId,
                            YearMonth lastClosedCycle) {}
