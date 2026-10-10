package com.mlcpu.carddemo.data;

import static com.mlcpu.carddemo.platform.records.Fields.putText;
import static com.mlcpu.carddemo.platform.records.Fields.putUnsigned;
import static com.mlcpu.carddemo.platform.records.Fields.putZoned;
import static com.mlcpu.carddemo.platform.records.Fields.text;
import static com.mlcpu.carddemo.platform.records.Fields.unsigned;
import static com.mlcpu.carddemo.platform.records.Fields.zoned;

import com.mlcpu.carddemo.domain.Account;
import com.mlcpu.carddemo.domain.CategoryBalance;
import com.mlcpu.carddemo.domain.Reference.CardCrossReference;
import com.mlcpu.carddemo.domain.Reference.DisclosureGroup;
import com.mlcpu.carddemo.domain.Reference.TransactionCategory;
import com.mlcpu.carddemo.domain.Reference.TransactionType;
import com.mlcpu.carddemo.domain.Transaction;

/**
 * Byte layouts of the CardDemo datasets (copybooks CVACT01Y, CVACT03Y, CVTRA01Y-06Y). Encoding writes onto a base
 * image: fields an operation assigns are written canonically (even when the value is unchanged, as COBOL ADD and MOVE
 * do), and every other byte, including FILLER, keeps what was stored.
 */
public final class RecordLayouts {
    private RecordLayouts() {}

    public static final int ACCOUNT = 300, CROSS_REFERENCE = 50, TRANSACTION = 350, CATEGORY_BALANCE = 50, DISCLOSURE_GROUP = 50,
            TRANSACTION_TYPE = 60, TRANSACTION_CATEGORY = 60;

    // ---------------------------------------------------------------- account (CVACT01Y)

    public static Account account(byte[] r) {
        return new Account(unsigned(r, 0, 11), text(r, 11, 1), zoned(r, 12, 12, 2), zoned(r, 24, 12, 2), zoned(r, 36, 12, 2),
                text(r, 48, 10), text(r, 58, 10), text(r, 68, 10), zoned(r, 78, 12, 2), zoned(r, 90, 12, 2), text(r, 102, 10), text(r, 112, 10));
    }

    /** The assigned balances, written canonically; every other byte of the stored record is kept. */
    public static byte[] account(Account.Change change, byte[] base) {
        byte[] r = base.clone();
        Account a = change.account();
        for (Account.Field f : change.assigned())
            switch (f) {
                case CURRENT_BALANCE -> putZoned(r, 12, 12, 2, true, a.currentBalance());
                case CYCLE_CREDIT -> putZoned(r, 78, 12, 2, true, a.currentCycleCredit());
                case CYCLE_DEBIT -> putZoned(r, 90, 12, 2, true, a.currentCycleDebit());
            }
        return r;
    }

    // ---------------------------------------------------------------- card cross-reference (CVACT03Y)

    public static CardCrossReference crossReference(byte[] r) {
        return new CardCrossReference(text(r, 0, 16), unsigned(r, 16, 9), unsigned(r, 25, 11));
    }

    public static final int XREF_ACCOUNT_OFFSET = 25;

    // ---------------------------------------------------------------- transaction (CVTRA05Y / CVTRA06Y)

    public static Transaction transaction(byte[] r) {
        return new Transaction(text(r, 0, 16), text(r, 16, 2), (int) unsigned(r, 18, 4), text(r, 22, 10), text(r, 32, 100),
                zoned(r, 132, 11, 2), unsigned(r, 143, 9), text(r, 152, 50), text(r, 202, 50), text(r, 252, 10), text(r, 262, 16),
                text(r, 278, 26), text(r, 304, 26));
    }

    /** Every field written; bytes beyond the fields (FILLER) keep {@code base}. */
    public static byte[] transaction(Transaction t, byte[] base) {
        byte[] r = base.clone();
        putText(r, 0, 16, t.id());
        putText(r, 16, 2, t.typeCode());
        putUnsigned(r, 18, 4, t.categoryCode());
        putText(r, 22, 10, t.source());
        putText(r, 32, 100, t.description());
        putZoned(r, 132, 11, 2, true, t.amount());
        putUnsigned(r, 143, 9, t.merchantId());
        putText(r, 152, 50, t.merchantName());
        putText(r, 202, 50, t.merchantCity());
        putText(r, 252, 10, t.merchantZip());
        putText(r, 262, 16, t.cardNumber());
        putText(r, 278, 26, t.originTimestamp());
        putText(r, 304, 26, t.processedTimestamp());
        return r;
    }

    // ---------------------------------------------------------------- category balance (CVTRA01Y)

    public static CategoryBalance categoryBalance(byte[] r) {
        return new CategoryBalance(new CategoryBalance.Key(unsigned(r, 0, 11), text(r, 11, 2), (int) unsigned(r, 13, 4)), zoned(r, 17, 11, 2));
    }

    /** All key and balance fields written (a new record); FILLER keeps {@code base}. */
    public static byte[] newCategoryBalance(CategoryBalance b, byte[] base) {
        byte[] r = base.clone();
        putUnsigned(r, 0, 11, b.key().accountId());
        putText(r, 11, 2, b.key().typeCode());
        putUnsigned(r, 13, 4, b.key().categoryCode());
        putZoned(r, 17, 11, 2, true, b.balance());
        return r;
    }

    /** The balance is assigned (written canonically); key and FILLER keep the stored bytes. */
    public static byte[] categoryBalance(CategoryBalance b, byte[] base) {
        byte[] r = base.clone();
        putZoned(r, 17, 11, 2, true, b.balance());
        return r;
    }

    // ---------------------------------------------------------------- reference data (CVTRA02Y-04Y)

    public static DisclosureGroup disclosureGroup(byte[] r) {
        return new DisclosureGroup(text(r, 0, 10), text(r, 10, 2), (int) unsigned(r, 12, 4), zoned(r, 16, 6, 2));
    }

    public static String disclosureGroupKey(String groupId, String typeCode, int categoryCode) {
        return String.format("%-10s%-2s%04d", groupId, typeCode, categoryCode).substring(0, 16);
    }

    public static TransactionType transactionType(byte[] r) {
        return new TransactionType(text(r, 0, 2), text(r, 2, 50));
    }

    public static TransactionCategory transactionCategory(byte[] r) {
        return new TransactionCategory(text(r, 0, 2), (int) unsigned(r, 2, 4), text(r, 6, 50));
    }
}
