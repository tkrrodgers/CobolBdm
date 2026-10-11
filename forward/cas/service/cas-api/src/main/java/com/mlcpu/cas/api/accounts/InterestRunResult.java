package com.mlcpu.cas.api.accounts;

import java.time.YearMonth;
import java.util.List;

/**
 * An interest run: the charges and the closed accounts in processing order, and the accounts skipped because their cycle was
 * already closed (ascending ids).
 */
public record InterestRunResult(YearMonth cycle, List<InterestCharge> charges, List<AccountInterest> accounts, List<Long> skippedAccounts) {
    public InterestRunResult {
        charges = List.copyOf(charges);
        accounts = List.copyOf(accounts);
        skippedAccounts = List.copyOf(skippedAccounts);
    }
}
