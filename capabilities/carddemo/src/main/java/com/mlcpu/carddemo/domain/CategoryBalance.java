package com.mlcpu.carddemo.domain;

import com.mlcpu.carddemo.platform.numeric.Decimals;
import java.math.BigDecimal;

/** The running balance of one account for one transaction type and category (CVTRA01Y TRAN-CAT-BAL-RECORD). */
public record CategoryBalance(Key key, BigDecimal balance) {

    public record Key(long accountId, String typeCode, int categoryCode) {
        /** Display form: 11-digit account, 2-character type, 4-digit category. */
        public String text() {
            String type = (typeCode + "  ").substring(0, 2);
            return String.format("%011d%s%04d", accountId, type, categoryCode);
        }
    }

    public static CategoryBalance opening(Key key, BigDecimal amount) {
        return new CategoryBalance(key, Decimals.amount9(amount));
    }

    public CategoryBalance add(BigDecimal amount) {
        return new CategoryBalance(key, Decimals.amount9(balance.add(amount)));
    }
}
