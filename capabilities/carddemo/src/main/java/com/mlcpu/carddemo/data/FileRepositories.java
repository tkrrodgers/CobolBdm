package com.mlcpu.carddemo.data;

import com.mlcpu.carddemo.domain.Account;
import com.mlcpu.carddemo.domain.CategoryBalance;
import com.mlcpu.carddemo.domain.Reference.CardCrossReference;
import com.mlcpu.carddemo.domain.Reference.DisclosureGroup;
import com.mlcpu.carddemo.domain.Reference.TransactionCategory;
import com.mlcpu.carddemo.domain.Reference.TransactionType;
import com.mlcpu.carddemo.domain.Repositories;
import com.mlcpu.carddemo.platform.records.IndexedDataset;
import java.io.IOException;
import java.util.Arrays;
import java.util.Optional;

/** Repositories over key-sequenced dataset images. */
public final class FileRepositories {
    private FileRepositories() {}

    public static final class Accounts implements Repositories.Accounts {
        private final IndexedDataset data;

        public Accounts(IndexedDataset data) {
            this.data = data;
        }

        @Override
        public Optional<Account> findById(long accountId) {
            return data.find(String.format("%011d", accountId)).map(RecordLayouts::account);
        }

        @Override
        public void update(Account.Change change) throws IOException {
            String key = String.format("%011d", change.account().id());
            byte[] stored = data.find(key).orElseThrow(() -> new com.mlcpu.carddemo.platform.records.DatasetException("no account " + key, "23"));
            data.replace(RecordLayouts.account(change, stored));
        }
    }

    public static final class CardCrossReferences implements Repositories.CardCrossReferences {
        private final IndexedDataset data;

        public CardCrossReferences(IndexedDataset data) {
            this.data = data;
        }

        @Override
        public Optional<CardCrossReference> findByCardNumber(String cardNumber) {
            return data.find(String.format("%-16s", cardNumber)).map(RecordLayouts::crossReference);
        }

        @Override
        public Optional<CardCrossReference> findByAccountId(long accountId) {
            return data.findBy(RecordLayouts.XREF_ACCOUNT_OFFSET, String.format("%011d", accountId)).map(RecordLayouts::crossReference);
        }
    }

    /**
     * Category balances. A new balance record is laid over the last record image this repository read or wrote, so its
     * FILLER bytes are that record's (the legacy program reused one record area; quirk preserved for byte parity).
     */
    public static final class CategoryBalances implements Repositories.CategoryBalances {
        private final IndexedDataset data;
        private byte[] area;

        public CategoryBalances(IndexedDataset data, byte storageDefault) {
            this.data = data;
            this.area = new byte[RecordLayouts.CATEGORY_BALANCE];
            Arrays.fill(area, storageDefault);
        }

        @Override
        public Optional<CategoryBalance> find(CategoryBalance.Key key) {
            Optional<byte[]> r = data.find(key.text());
            r.ifPresent(img -> area = img);
            return r.map(RecordLayouts::categoryBalance);
        }

        @Override
        public void create(CategoryBalance balance) throws IOException {
            byte[] img = RecordLayouts.newCategoryBalance(balance, area);
            data.insert(img);
            area = img;
        }

        @Override
        public void update(CategoryBalance balance) throws IOException {
            byte[] stored = data.find(balance.key().text()).orElseThrow(() -> new com.mlcpu.carddemo.platform.records.DatasetException("no balance " + balance.key().text(), "23"));
            byte[] img = RecordLayouts.categoryBalance(balance, stored);
            data.replace(img);
            area = img;
        }
    }

    public static final class DisclosureGroups implements Repositories.DisclosureGroups {
        private final IndexedDataset data;

        public DisclosureGroups(IndexedDataset data) {
            this.data = data;
        }

        @Override
        public Optional<DisclosureGroup> find(String groupId, String typeCode, int categoryCode) {
            return data.find(RecordLayouts.disclosureGroupKey(groupId, typeCode, categoryCode)).map(RecordLayouts::disclosureGroup);
        }
    }

    public static final class TransactionTypes implements Repositories.TransactionTypes {
        private final IndexedDataset data;

        public TransactionTypes(IndexedDataset data) {
            this.data = data;
        }

        @Override
        public Optional<TransactionType> find(String code) {
            return data.find(String.format("%-2s", code)).map(RecordLayouts::transactionType);
        }
    }

    public static final class TransactionCategories implements Repositories.TransactionCategories {
        private final IndexedDataset data;

        public TransactionCategories(IndexedDataset data) {
            this.data = data;
        }

        @Override
        public Optional<TransactionCategory> find(String typeCode, int categoryCode) {
            return data.find(String.format("%-2s%04d", typeCode, categoryCode)).map(RecordLayouts::transactionCategory);
        }
    }
}
