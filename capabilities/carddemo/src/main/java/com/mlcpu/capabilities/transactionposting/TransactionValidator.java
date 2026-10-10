package com.mlcpu.capabilities.transactionposting;

import com.mlcpu.carddemo.domain.Account;
import com.mlcpu.carddemo.domain.Reference.CardCrossReference;
import com.mlcpu.carddemo.domain.Repositories;
import com.mlcpu.carddemo.domain.Transaction;
import java.io.IOException;
import java.util.Optional;

/**
 * Decides whether a daily transaction may be posted (rules R-CBTRN02C-004..010): the card must be known, its account
 * must exist, the cycle balance including the amount must stay within the credit limit, and the transaction must not
 * be dated after the account's expiration date.
 */
public final class TransactionValidator {

    public sealed interface Outcome permits Accepted, Rejected {}

    /** Valid: the card's cross-reference and the account the posting will update. */
    public record Accepted(CardCrossReference crossReference, Account account) implements Outcome {}

    public record Rejected(RejectReason reason) implements Outcome {}

    private final Repositories.CardCrossReferences crossReferences;
    private final Repositories.Accounts accounts;

    public TransactionValidator(Repositories.CardCrossReferences crossReferences, Repositories.Accounts accounts) {
        this.crossReferences = crossReferences;
        this.accounts = accounts;
    }

    public Outcome validate(Transaction t) throws IOException {
        Optional<CardCrossReference> xref = crossReferences.findByCardNumber(t.cardNumber());
        if (xref.isEmpty()) return new Rejected(RejectReason.INVALID_CARD);
        Optional<Account> account = accounts.findById(xref.get().accountId());
        if (account.isEmpty()) return new Rejected(RejectReason.ACCOUNT_NOT_FOUND);
        return limitAndExpiry(t, account.get()).<Outcome>map(Rejected::new).orElse(new Accepted(xref.get(), account.get()));
    }

    /**
     * Both checks run; when both fail the expiration reason is reported (the legacy program assigns the reasons in
     * sequence and the later one wins — quirk Q-03, preserved).
     */
    static Optional<RejectReason> limitAndExpiry(Transaction t, Account account) {
        RejectReason reason = null;
        if (account.creditLimit().compareTo(account.projectedCycleBalance(t.amount())) < 0) reason = RejectReason.OVERLIMIT;
        if (account.expiredOn(t.originDate())) reason = RejectReason.RECEIVED_AFTER_EXPIRATION;
        return Optional.ofNullable(reason);
    }
}
