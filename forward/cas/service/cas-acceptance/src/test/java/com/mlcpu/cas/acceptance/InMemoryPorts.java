package com.mlcpu.cas.acceptance;

import com.mlcpu.cas.api.CasPorts;
import com.mlcpu.cas.api.IdempotencyKey;
import com.mlcpu.cas.api.ports.AccountRecord;
import com.mlcpu.cas.api.ports.Accounts;
import com.mlcpu.cas.api.ports.CardRecord;
import com.mlcpu.cas.api.ports.Cards;
import com.mlcpu.cas.api.ports.CategoryBalanceRecord;
import com.mlcpu.cas.api.ports.CategoryBalances;
import com.mlcpu.cas.api.ports.CommandJournal;
import com.mlcpu.cas.api.ports.CustomerLink;
import com.mlcpu.cas.api.ports.CustomerLinks;
import com.mlcpu.cas.api.ports.CustomerMaster;
import com.mlcpu.cas.api.ports.DisclosureRate;
import com.mlcpu.cas.api.ports.DisclosureRates;
import com.mlcpu.cas.api.ports.Ledger;
import com.mlcpu.cas.api.ports.LedgerEntry;
import com.mlcpu.cas.api.ports.MasterRecord;
import com.mlcpu.cas.api.ports.RejectionLog;
import com.mlcpu.cas.api.ports.RejectionRecord;
import com.mlcpu.cas.api.ports.TransactionCategories;
import com.mlcpu.cas.api.ports.TransactionCategoryRecord;
import com.mlcpu.cas.api.ports.TransactionTypeRecord;
import com.mlcpu.cas.api.ports.TransactionTypes;
import java.time.Clock;
import java.time.Instant;
import java.time.LocalDateTime;
import java.time.ZoneId;
import java.time.ZoneOffset;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import java.util.Set;
import java.util.TreeMap;

/** In-memory adapters for every outbound port, as the acceptance scenarios' storage. Lists come back in key or append order. */
public final class InMemoryPorts {
    public final TreeMap<Long, AccountRecord> accounts = new TreeMap<>();
    public final TreeMap<String, CardRecord> cards = new TreeMap<>();
    public final TreeMap<String, CategoryBalanceRecord> categoryBalances = new TreeMap<>();
    public final Map<String, DisclosureRate> disclosureRates = new HashMap<>();
    public final Map<String, TransactionTypeRecord> transactionTypes = new HashMap<>();
    public final Map<String, TransactionCategoryRecord> transactionCategories = new HashMap<>();
    public final List<LedgerEntry> ledger = new ArrayList<>();
    private final Set<String> ledgerIds = new HashSet<>();
    public final List<RejectionRecord> rejections = new ArrayList<>();
    public final TreeMap<String, MasterRecord> customers = new TreeMap<>();
    public final Map<String, CustomerLink> customerLinks = new HashMap<>();
    public final Map<IdempotencyKey, Object> journal = new HashMap<>();
    public final MutableClock clock = new MutableClock();

    static String balanceKey(long account, String type, int category) {
        return String.format("%011d|%s|%04d", account, type, category);
    }

    static String rateKey(String group, String type, int category) {
        return group + "|" + type + "|" + category;
    }

    public void addLedger(LedgerEntry e) {
        ledger.add(e);
        ledgerIds.add(e.transactionId());
    }

    public CasPorts ports() {
        return new CasPorts(
                new Accounts() {
                    public Optional<AccountRecord> find(long id) { return Optional.ofNullable(accounts.get(id)); }
                    public void save(AccountRecord a) { accounts.put(a.accountId(), a); }
                },
                new Cards() {
                    public Optional<CardRecord> findByCardNumber(String n) { return Optional.ofNullable(n == null ? null : cards.get(n)); }
                    public List<CardRecord> findByAccountId(long id) {
                        return cards.values().stream().filter(c -> c.accountId() == id).toList();
                    }
                },
                new CategoryBalances() {
                    public Optional<CategoryBalanceRecord> find(long a, String t, int c) { return Optional.ofNullable(categoryBalances.get(balanceKey(a, t, c))); }
                    public List<CategoryBalanceRecord> all() { return List.copyOf(categoryBalances.values()); }
                    public void save(CategoryBalanceRecord b) { categoryBalances.put(balanceKey(b.accountId(), b.typeCode(), b.categoryCode()), b); }
                },
                (g, t, c) -> Optional.ofNullable(disclosureRates.get(rateKey(g, t, c))),
                t -> Optional.ofNullable(transactionTypes.get(t)),
                (t, c) -> Optional.ofNullable(transactionCategories.get(t + "|" + c)),
                new Ledger() {
                    public boolean contains(String id) { return ledgerIds.contains(id); }
                    public void append(LedgerEntry e) {
                        if (!ledgerIds.add(e.transactionId())) throw new IllegalStateException("ledger: transaction id " + e.transactionId() + " appended twice");
                        ledger.add(e);
                    }
                    public List<LedgerEntry> all() { return Collections.unmodifiableList(new ArrayList<>(ledger)); }
                },
                new RejectionLog() {
                    public void append(RejectionRecord r) { rejections.add(r); }
                    public List<RejectionRecord> all() { return Collections.unmodifiableList(new ArrayList<>(rejections)); }
                },
                new CustomerMaster() {
                    public Optional<MasterRecord> find(String key) { return Optional.ofNullable(key == null ? null : customers.get(key)); }
                    public List<MasterRecord> all() { return List.copyOf(customers.values()); }
                    public void save(MasterRecord r) { customers.put(r.key(), r); }
                    public void delete(String key) { customers.remove(key); }
                },
                id -> Optional.ofNullable(customerLinks.get(id)),
                new CommandJournal() {
                    public Optional<Object> find(IdempotencyKey k) { return Optional.ofNullable(journal.get(k)); }
                    public void record(IdempotencyKey k, Object result) { journal.put(k, result); }
                },
                clock);
    }

    /** A clock the scenario sets before each step; the service's local time is UTC. */
    public static final class MutableClock extends Clock {
        private Instant now = Instant.parse("2022-07-05T10:11:12Z");

        public void set(LocalDateTime at) {
            now = at.toInstant(ZoneOffset.UTC);
        }

        @Override
        public ZoneId getZone() { return ZoneOffset.UTC; }

        @Override
        public Clock withZone(ZoneId zone) { return this; }

        @Override
        public Instant instant() { return now; }
    }
}
