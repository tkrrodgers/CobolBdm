package com.mlcpu.capabilities.interestcalculation;

import com.mlcpu.carddemo.domain.Account;
import com.mlcpu.carddemo.domain.CategoryBalance;
import com.mlcpu.carddemo.domain.Reference.CardCrossReference;
import com.mlcpu.carddemo.domain.Reference.DisclosureGroup;
import com.mlcpu.carddemo.domain.Repositories;
import com.mlcpu.carddemo.domain.Repositories.Received;
import com.mlcpu.carddemo.domain.Transaction;
import com.mlcpu.carddemo.platform.numeric.Decimals;
import com.mlcpu.carddemo.platform.time.BatchTimestamps;
import java.io.IOException;
import java.math.BigDecimal;
import java.time.OffsetDateTime;
import java.util.Optional;
import java.util.function.Consumer;
import java.util.function.Supplier;

/**
 * Monthly interest run (CBACT04C, design InterestCalculation). Category balances arrive in account order; for each
 * balance whose disclosure group charges interest, an interest transaction is generated; when the account changes, the
 * previous account's cycle is closed with its accumulated interest.
 * <p>Quirk Q-01 (dead code): the legacy end-of-file branch that would close the <em>last</em> account is unreachable,
 * so the last account's interest transactions are written but its balance is not updated. Preserved for parity; raised
 * for business review.
 */
public final class InterestCalculationService {

    public interface Balances {
        Optional<Received<CategoryBalance>> next() throws IOException;
    }

    /** A lookup that must succeed; the step decides how a miss ends the run. */
    public static final class MissingReference extends IOException {
        public final String what;
        public final String key;

        public MissingReference(String what, String key) {
            super(what + " " + key);
            this.what = what;
            this.key = key;
        }
    }

    static final String INTEREST_TYPE = "01";
    static final int INTEREST_CATEGORY = 5;
    static final String INTEREST_SOURCE = "System";
    private static final int DESCRIPTION_LENGTH = 100;

    private final Balances balances;
    private final Repositories.Accounts accounts;
    private final Repositories.CardCrossReferences crossReferences;
    private final Repositories.DisclosureGroups disclosureGroups;
    private final Repositories.TransactionSink transactions;
    private final Consumer<String> console;
    private final Supplier<OffsetDateTime> clock;
    private final char unwrittenText;

    public InterestCalculationService(Balances balances, Repositories.Accounts accounts, Repositories.CardCrossReferences crossReferences,
                                      Repositories.DisclosureGroups disclosureGroups, Repositories.TransactionSink transactions,
                                      Consumer<String> console, Supplier<OffsetDateTime> clock, byte storageDefault) {
        this.balances = balances;
        this.accounts = accounts;
        this.crossReferences = crossReferences;
        this.disclosureGroups = disclosureGroups;
        this.transactions = transactions;
        this.console = console;
        this.clock = clock;
        this.unwrittenText = (char) (storageDefault & 0xFF);
    }

    /** @param runDate the first ten characters of the PARM (YYYYMMDDnn), the prefix of generated transaction ids */
    public void run(String runDate) throws IOException {
        String idPrefix = (runDate + " ".repeat(10)).substring(0, 10);
        Account account = null;
        CardCrossReference card = null;
        long currentAccountId = -1;
        BigDecimal accruedInterest = BigDecimal.ZERO.setScale(2);
        int sequence = 0;
        for (Optional<Received<CategoryBalance>> next = balances.next(); next.isPresent(); next = balances.next()) {
            console.accept(next.get().original());
            CategoryBalance balance = next.get().value();
            if (balance.key().accountId() != currentAccountId) {
                if (account != null) accounts.update(account.closeCycleWithInterest(accruedInterest));
                accruedInterest = BigDecimal.ZERO.setScale(2);
                currentAccountId = balance.key().accountId();
                long id = currentAccountId;
                account = accounts.findById(id).orElseThrow(() -> new MissingReference("ACCOUNT", String.format("%011d", id)));
                card = crossReferences.findByAccountId(id).orElseThrow(() -> new MissingReference("XREF", String.format("%011d", id)));
            }
            BigDecimal rate = rateFor(account.groupId(), balance.key());
            if (!InterestPolicy.charges(rate)) continue;
            BigDecimal interest = InterestPolicy.monthlyInterest(balance.balance(), rate);
            accruedInterest = Decimals.amount9(accruedInterest.add(interest));
            sequence = (sequence + 1) % 1_000_000;
            transactions.add(interestTransaction(idPrefix + String.format("%06d", sequence), account, card, interest));
        }
    }

    /** The account's disclosure group; when it has no entry for the type and category, the DEFAULT group's rate. */
    private BigDecimal rateFor(String groupId, CategoryBalance.Key key) throws IOException {
        Optional<DisclosureGroup> group = disclosureGroups.find(groupId, key.typeCode(), key.categoryCode());
        if (group.isEmpty()) {
            console.accept("DISCLOSURE GROUP RECORD MISSING");
            console.accept("TRY WITH DEFAULT GROUP CODE");
            group = disclosureGroups.find(DisclosureGroup.DEFAULT_GROUP, key.typeCode(), key.categoryCode());
        }
        return group.orElseThrow(() -> new MissingReference("DEFAULT DISCLOSURE GROUP", DisclosureGroup.DEFAULT_GROUP + key.typeCode() + key.categoryCode())).interestRate();
    }

    /**
     * The interest transaction: type 01 / category 5 from source "System", described as "Int. for a/c" and the account
     * number. The description is not blank-padded: its unused bytes hold the storage default (quirk Q-06, preserved).
     */
    private Transaction interestTransaction(String id, Account account, CardCrossReference card, BigDecimal interest) {
        String now = BatchTimestamps.db2(clock.get());
        String text = "Int. for a/c " + String.format("%011d", account.id());
        String description = text + String.valueOf(unwrittenText).repeat(DESCRIPTION_LENGTH - text.length());
        return new Transaction(id, INTEREST_TYPE, INTEREST_CATEGORY, INTEREST_SOURCE, description, interest, 0, "", "", "",
                card.cardNumber(), now, now);
    }
}
