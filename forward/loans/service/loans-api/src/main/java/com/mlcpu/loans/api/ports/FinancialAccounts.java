package com.mlcpu.loans.api.ports;

/** The loans' financial accounts and their transactions. */
public interface FinancialAccounts {

    /** Opens a loan's account and returns its id. */
    String open(LoanAccount account);

    /** Records a transaction on an account and returns its id. */
    String post(AccountTransaction transaction);

    /** Cancels a transaction. */
    void cancel(String transactionId);
}
