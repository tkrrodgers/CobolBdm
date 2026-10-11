package com.mlcpu.cas.api;

import com.mlcpu.cas.api.ports.Accounts;
import com.mlcpu.cas.api.ports.Cards;
import com.mlcpu.cas.api.ports.CategoryBalances;
import com.mlcpu.cas.api.ports.CommandJournal;
import com.mlcpu.cas.api.ports.CustomerLinks;
import com.mlcpu.cas.api.ports.CustomerMaster;
import com.mlcpu.cas.api.ports.DisclosureRates;
import com.mlcpu.cas.api.ports.Ledger;
import com.mlcpu.cas.api.ports.RejectionLog;
import com.mlcpu.cas.api.ports.TransactionCategories;
import com.mlcpu.cas.api.ports.TransactionTypes;
import java.time.Clock;
import java.util.Objects;

/**
 * The outbound ports the service is built with: persistence, reference data, the command journal and the clock. Adapters
 * (in-memory in the tests) own storage; the service sees only these interfaces and the snapshot records of
 * {@code com.mlcpu.cas.api.ports}. The current time is {@code LocalDateTime.now(clock)}.
 */
public record CasPorts(Accounts accounts, Cards cards, CategoryBalances categoryBalances, DisclosureRates disclosureRates,
                       TransactionTypes transactionTypes, TransactionCategories transactionCategories, Ledger ledger,
                       RejectionLog rejections, CustomerMaster customers, CustomerLinks customerLinks, CommandJournal journal,
                       Clock clock) {
    public CasPorts {
        Objects.requireNonNull(accounts);
        Objects.requireNonNull(cards);
        Objects.requireNonNull(categoryBalances);
        Objects.requireNonNull(disclosureRates);
        Objects.requireNonNull(transactionTypes);
        Objects.requireNonNull(transactionCategories);
        Objects.requireNonNull(ledger);
        Objects.requireNonNull(rejections);
        Objects.requireNonNull(customers);
        Objects.requireNonNull(customerLinks);
        Objects.requireNonNull(journal);
        Objects.requireNonNull(clock);
    }
}
