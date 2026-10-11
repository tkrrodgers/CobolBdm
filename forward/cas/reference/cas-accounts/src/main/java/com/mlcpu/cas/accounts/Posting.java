package com.mlcpu.cas.accounts;

import com.mlcpu.cas.api.CasPorts;
import com.mlcpu.cas.api.accounts.BatchPostingResult;
import com.mlcpu.cas.api.accounts.PostingOutcome;
import com.mlcpu.cas.api.accounts.RejectionReason;
import com.mlcpu.cas.api.accounts.TransactionInput;
import com.mlcpu.cas.api.ports.AccountRecord;
import com.mlcpu.cas.api.ports.CardRecord;
import com.mlcpu.cas.api.ports.CategoryBalanceRecord;
import com.mlcpu.cas.api.ports.LedgerEntry;
import com.mlcpu.cas.api.ports.RejectionRecord;
import java.time.LocalDateTime;
import java.util.ArrayList;
import java.util.List;
import java.util.Optional;

/** Posting card transactions (rules POST): one at a time in real time, or a day in order. */
public final class Posting {
    private final CasPorts ports;

    public Posting(CasPorts ports) {
        this.ports = ports;
    }

    /** POST-01..11 for one transaction. */
    public PostingOutcome post(TransactionInput t) {
        LocalDateTime now = LocalDateTime.now(ports.clock());
        Optional<CardRecord> card = ports.cards().findByCardNumber(t.cardNumber());
        Long accountId = card.map(CardRecord::accountId).orElse(null);
        if (ports.ledger().contains(t.transactionId())) return reject(t, accountId, RejectionReason.DUPLICATE_TRANSACTION, now);
        if (card.isEmpty()) return reject(t, null, RejectionReason.INVALID_CARD, now);
        Optional<AccountRecord> stored = ports.accounts().find(accountId);
        if (stored.isEmpty()) return reject(t, accountId, RejectionReason.ACCOUNT_NOT_FOUND, now);
        CardAccount account = new CardAccount(stored.get());
        Optional<RejectionReason> refusal = account.refusal(t.amount(), t.originTimestamp());
        if (refusal.isPresent()) return reject(t, accountId, refusal.get(), now);

        CategoryBalanceRecord balance = ports.categoryBalances().find(accountId, t.typeCode(), t.categoryCode())
                .map(b -> new CategoryBalanceRecord(b.accountId(), b.typeCode(), b.categoryCode(), b.balance().add(t.amount())))
                .orElse(new CategoryBalanceRecord(accountId, t.typeCode(), t.categoryCode(), t.amount()));
        ports.categoryBalances().save(balance);
        CardAccount posted = account.post(t.amount());
        ports.accounts().save(posted.state());
        ports.ledger().append(new LedgerEntry(t.transactionId(), accountId, t.cardNumber(), t.typeCode(), t.categoryCode(), t.source(),
                t.description(), t.amount(), t.merchantId(), t.merchantName(), t.merchantCity(), t.merchantZip(), t.originTimestamp(), now,
                null));
        return new PostingOutcome.Posted(t.transactionId(), accountId, posted.balance());
    }

    /** POST-12, POST-13: the day in input order, each transaction seeing the ones before it. */
    public BatchPostingResult postAll(List<TransactionInput> transactions) {
        List<PostingOutcome> outcomes = new ArrayList<>();
        for (TransactionInput t : transactions) outcomes.add(post(t));
        int posted = (int) outcomes.stream().filter(o -> o instanceof PostingOutcome.Posted).count();
        return new BatchPostingResult(outcomes.size(), posted, outcomes.size() - posted, outcomes);
    }

    private PostingOutcome reject(TransactionInput t, Long accountId, RejectionReason reason, LocalDateTime now) {
        ports.rejections().append(new RejectionRecord(t, accountId, reason, now));
        return new PostingOutcome.Rejected(t.transactionId(), reason);
    }
}
