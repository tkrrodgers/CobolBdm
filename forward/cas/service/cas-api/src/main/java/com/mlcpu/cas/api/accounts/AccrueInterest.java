package com.mlcpu.cas.api.accounts;

import com.mlcpu.cas.api.IdempotencyKey;
import java.time.YearMonth;

/** Charge the monthly interest for {@code cycle} and close the cycle of every account with category balances. */
public record AccrueInterest(IdempotencyKey key, YearMonth cycle) {}
