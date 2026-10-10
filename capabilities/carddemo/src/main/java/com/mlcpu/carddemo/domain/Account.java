package com.mlcpu.carddemo.domain;

import com.mlcpu.carddemo.platform.numeric.Decimals;
import java.math.BigDecimal;
import java.util.EnumSet;
import java.util.Set;

/** A credit-card account (CVACT01Y ACCOUNT-RECORD). Balances hold 10 integer and 2 fraction digits. */
public record Account(long id, String activeStatus, BigDecimal currentBalance, BigDecimal creditLimit, BigDecimal cashCreditLimit,
                      String openDate, String expirationDate, String reissueDate, BigDecimal currentCycleCredit,
                      BigDecimal currentCycleDebit, String addressZip, String groupId) {

    /** The balances an operation can assign. */
    public enum Field { CURRENT_BALANCE, CYCLE_CREDIT, CYCLE_DEBIT }

    /**
     * The account after an operation and the fields the operation assigned. Storage rewrites exactly the assigned fields
     * (in canonical form, even when the value is unchanged) and keeps every other stored byte.
     */
    public record Change(Account account, Set<Field> assigned) {}

    /** Posts a transaction amount: the balance moves; credits (amount >= 0) and debits accumulate separately for the cycle. */
    public Change post(BigDecimal amount) {
        BigDecimal balance = Decimals.balance10(currentBalance.add(amount));
        if (amount.signum() >= 0)
            return new Change(with(balance, Decimals.balance10(currentCycleCredit.add(amount)), currentCycleDebit),
                    EnumSet.of(Field.CURRENT_BALANCE, Field.CYCLE_CREDIT));
        return new Change(with(balance, currentCycleCredit, Decimals.balance10(currentCycleDebit.add(amount))),
                EnumSet.of(Field.CURRENT_BALANCE, Field.CYCLE_DEBIT));
    }

    /** Closes the cycle: accrued interest is added to the balance and the cycle credit/debit totals restart at zero. */
    public Change closeCycleWithInterest(BigDecimal interest) {
        return new Change(with(Decimals.balance10(currentBalance.add(interest)), BigDecimal.ZERO.setScale(2), BigDecimal.ZERO.setScale(2)),
                EnumSet.allOf(Field.class));
    }

    /** Credit used this cycle if {@code amount} were posted: cycle credits minus cycle debits plus the amount. */
    public BigDecimal projectedCycleBalance(BigDecimal amount) {
        return Decimals.amount9(currentCycleCredit.subtract(currentCycleDebit).add(amount));
    }

    /** Expiration dates are ISO text (YYYY-MM-DD); a transaction dated after the expiration date is refused. */
    public boolean expiredOn(String isoDate) {
        return expirationDate.compareTo(isoDate) < 0;
    }

    private Account with(BigDecimal balance, BigDecimal cycleCredit, BigDecimal cycleDebit) {
        return new Account(id, activeStatus, balance, creditLimit, cashCreditLimit, openDate, expirationDate, reissueDate,
                cycleCredit, cycleDebit, addressZip, groupId);
    }
}
