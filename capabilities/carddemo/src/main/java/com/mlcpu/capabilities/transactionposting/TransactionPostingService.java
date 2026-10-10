package com.mlcpu.capabilities.transactionposting;

import com.mlcpu.carddemo.domain.CategoryBalance;
import com.mlcpu.carddemo.domain.Repositories;
import com.mlcpu.carddemo.domain.Repositories.Received;
import com.mlcpu.carddemo.domain.Transaction;
import com.mlcpu.carddemo.platform.text.Formats;
import com.mlcpu.carddemo.platform.time.BatchTimestamps;
import java.io.IOException;
import java.time.OffsetDateTime;
import java.util.Optional;
import java.util.function.Consumer;
import java.util.function.Supplier;

/**
 * Posts the day's card transactions (CBTRN02C, design mlcpu.capability-design.v1 TransactionPosting). Each valid
 * transaction updates its account and its category balance and is recorded with the processing timestamp; each invalid
 * one is written to the reject file with its reason. The run reports how many were processed and rejected.
 */
public final class TransactionPostingService {

    public interface DailyTransactions {
        Optional<Received<Transaction>> next() throws IOException;
    }

    public interface Rejects {
        void reject(Received<Transaction> transaction, RejectReason reason) throws IOException;
    }

    public record Summary(long processed, long rejected) {
        /** 4 when anything was rejected, else 0. */
        public int returnCode() {
            return rejected > 0 ? 4 : 0;
        }
    }

    private final DailyTransactions daily;
    private final TransactionValidator validator;
    private final Repositories.Accounts accounts;
    private final Repositories.CategoryBalances balances;
    private final Repositories.TransactionSink posted;
    private final Rejects rejects;
    private final Consumer<String> console;
    private final Supplier<OffsetDateTime> clock;

    public TransactionPostingService(DailyTransactions daily, TransactionValidator validator, Repositories.Accounts accounts,
                                     Repositories.CategoryBalances balances, Repositories.TransactionSink posted, Rejects rejects,
                                     Consumer<String> console, Supplier<OffsetDateTime> clock) {
        this.daily = daily;
        this.validator = validator;
        this.accounts = accounts;
        this.balances = balances;
        this.posted = posted;
        this.rejects = rejects;
        this.console = console;
        this.clock = clock;
    }

    public Summary postAll() throws IOException {
        long processed = 0, rejected = 0;
        for (Optional<Received<Transaction>> next = daily.next(); next.isPresent(); next = daily.next()) {
            processed++;
            Received<Transaction> received = next.get();
            switch (validator.validate(received.value())) {
                case TransactionValidator.Accepted ok -> post(received.value(), ok);
                case TransactionValidator.Rejected no -> {
                    rejected++;
                    rejects.reject(received, no.reason());
                }
            }
        }
        console.accept("TRANSACTIONS PROCESSED :" + Formats.count(processed, 9));
        console.accept("TRANSACTIONS REJECTED  :" + Formats.count(rejected, 9));
        return new Summary(processed, rejected);
    }

    /** R-CBTRN02C-011..019: category balance, then account, then the posted transaction stamped with the processing time. */
    private void post(Transaction t, TransactionValidator.Accepted ok) throws IOException {
        Transaction stamped = t.processedAt(BatchTimestamps.db2(clock.get()));
        CategoryBalance.Key key = new CategoryBalance.Key(ok.crossReference().accountId(), t.typeCode(), t.categoryCode());
        Optional<CategoryBalance> balance = balances.find(key);
        if (balance.isPresent()) balances.update(balance.get().add(t.amount()));
        else {
            console.accept("TCATBAL record not found for key : " + key.text() + ".. Creating.");
            balances.create(CategoryBalance.opening(key, t.amount()));
        }
        accounts.update(ok.account().post(t.amount()));
        posted.add(stamped);
    }
}
