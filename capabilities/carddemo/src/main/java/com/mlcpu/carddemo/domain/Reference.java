package com.mlcpu.carddemo.domain;

import java.math.BigDecimal;

/** Reference data of the CardDemo batch: card cross-references, disclosure groups, transaction types and categories. */
public final class Reference {
    private Reference() {}

    /** Which customer and account a card belongs to (CVACT03Y CARD-XREF-RECORD). */
    public record CardCrossReference(String cardNumber, long customerId, long accountId) {}

    /** The interest rate (annual percent) a disclosure group applies to one transaction type and category (CVTRA02Y). */
    public record DisclosureGroup(String groupId, String typeCode, int categoryCode, BigDecimal interestRate) {
        public static final String DEFAULT_GROUP = "DEFAULT";
    }

    /** CVTRA03Y TRAN-TYPE-RECORD. */
    public record TransactionType(String code, String description) {}

    /** CVTRA04Y TRAN-CAT-RECORD. */
    public record TransactionCategory(String typeCode, int categoryCode, String description) {}
}
