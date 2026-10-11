package com.mlcpu.cas.accounts;

import com.mlcpu.cas.api.accounts.RejectionReason;
import com.mlcpu.cas.api.ports.AccountRecord;
import java.math.BigDecimal;
import java.time.LocalDateTime;
import java.time.YearMonth;
import java.util.Optional;

/**
 * A card account and its cycle: the aggregate that posting and interest change. Wraps the stored snapshot so that every field the
 * rules do not touch is kept as stored.
 */
record CardAccount(AccountRecord state) {

    long id() {
        return state.accountId();
    }

    BigDecimal balance() {
        return state.currentBalance();
    }

    String disclosureGroup() {
        return state.groupId();
    }

    /**
     * POST-04..06: over limit when the credit limit is below the projected cycle balance; expired when the expiration date is before
     * the origin date (never without an origin timestamp); when both fail, expiry wins.
     */
    Optional<RejectionReason> refusal(BigDecimal amount, LocalDateTime origin) {
        RejectionReason reason = null;
        BigDecimal projected = state.currentCycleCredit().subtract(state.currentCycleDebit()).add(amount);
        if (state.creditLimit().compareTo(projected) < 0) reason = RejectionReason.OVERLIMIT;
        if (origin != null && state.expirationDate() != null && state.expirationDate().isBefore(origin.toLocalDate()))
            reason = RejectionReason.EXPIRED_ACCOUNT;
        return Optional.ofNullable(reason);
    }

    /** POST-08: the balance moves; credits and debits accumulate separately for the cycle. */
    CardAccount post(BigDecimal amount) {
        boolean credit = amount.signum() >= 0;
        return new CardAccount(new AccountRecord(state.accountId(), state.activeStatus(), state.currentBalance().add(amount),
                state.creditLimit(), state.cashCreditLimit(), state.openDate(), state.expirationDate(), state.reissueDate(),
                credit ? state.currentCycleCredit().add(amount) : state.currentCycleCredit(),
                credit ? state.currentCycleDebit() : state.currentCycleDebit().add(amount), state.addressZip(), state.groupId(),
                state.lastClosedCycle()));
    }

    /** INT-02: the cycle (or a later one) is already closed. */
    boolean closedFor(YearMonth cycle) {
        return state.lastClosedCycle() != null && !state.lastClosedCycle().isBefore(cycle);
    }

    /** INT-09: the cycle's interest joins the balance and the cycle totals restart. */
    CardAccount close(YearMonth cycle, BigDecimal interest) {
        return new CardAccount(new AccountRecord(state.accountId(), state.activeStatus(), state.currentBalance().add(interest),
                state.creditLimit(), state.cashCreditLimit(), state.openDate(), state.expirationDate(), state.reissueDate(),
                Cents.ZERO, Cents.ZERO, state.addressZip(), state.groupId(), cycle));
    }
}
