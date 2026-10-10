package com.mlcpu.carddemo.domain;

import com.mlcpu.carddemo.domain.Reference.CardCrossReference;
import com.mlcpu.carddemo.domain.Reference.DisclosureGroup;
import com.mlcpu.carddemo.domain.Reference.TransactionCategory;
import com.mlcpu.carddemo.domain.Reference.TransactionType;
import java.io.IOException;
import java.util.Optional;

/** Persistence ports of the CardDemo domain. Implementations own file formats; callers see only domain records. */
public final class Repositories {
    private Repositories() {}

    public interface Accounts {
        Optional<Account> findById(long accountId) throws IOException;

        /** Rewrites the fields the change assigned; fails when the account does not exist. */
        void update(Account.Change change) throws IOException;
    }

    public interface CardCrossReferences {
        Optional<CardCrossReference> findByCardNumber(String cardNumber) throws IOException;

        /** The first cross-reference of an account (the alternate-key read). */
        Optional<CardCrossReference> findByAccountId(long accountId) throws IOException;
    }

    public interface CategoryBalances {
        Optional<CategoryBalance> find(CategoryBalance.Key key) throws IOException;

        void create(CategoryBalance balance) throws IOException;

        void update(CategoryBalance balance) throws IOException;
    }

    public interface DisclosureGroups {
        Optional<DisclosureGroup> find(String groupId, String typeCode, int categoryCode) throws IOException;
    }

    public interface TransactionTypes {
        Optional<TransactionType> find(String code) throws IOException;
    }

    public interface TransactionCategories {
        Optional<TransactionCategory> find(String typeCode, int categoryCode) throws IOException;
    }

    /** Where posted or generated transactions go. */
    public interface TransactionSink {
        void add(Transaction transaction) throws IOException;
    }

    /** A record as received, with its original text kept for audit echoes (reject files, trace lines). */
    public record Received<T>(T value, String original) {}
}
