package com.mlcpu.cas.accounts;

import com.mlcpu.cas.api.CasPorts;
import com.mlcpu.cas.api.ReferenceDataMissing;
import com.mlcpu.cas.api.accounts.AccountInterest;
import com.mlcpu.cas.api.accounts.InterestCharge;
import com.mlcpu.cas.api.accounts.InterestRunResult;
import com.mlcpu.cas.api.ports.CardRecord;
import com.mlcpu.cas.api.ports.CategoryBalanceRecord;
import com.mlcpu.cas.api.ports.DisclosureRate;
import com.mlcpu.cas.api.ports.LedgerEntry;
import java.math.BigDecimal;
import java.time.LocalDateTime;
import java.time.YearMonth;
import java.time.format.DateTimeFormatter;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * The monthly interest run (rules INT). It plans the whole run first and writes only when every reference was found, so a missing
 * account, card or rate changes nothing (INT-10).
 */
public final class InterestAccrual {
    static final String INTEREST_TYPE = "01";
    static final int INTEREST_CATEGORY = 5;
    static final String INTEREST_SOURCE = "System";
    private static final DateTimeFormatter CYCLE = DateTimeFormatter.ofPattern("yyyyMM");

    private final CasPorts ports;

    public InterestAccrual(CasPorts ports) {
        this.ports = ports;
    }

    private record Planned(CardAccount closed, BigDecimal total, List<LedgerEntry> entries) {}

    public InterestRunResult accrue(YearMonth cycle) {
        LocalDateTime now = LocalDateTime.now(ports.clock());
        Map<Long, List<CategoryBalanceRecord>> byAccount = new LinkedHashMap<>();
        for (CategoryBalanceRecord b : ports.categoryBalances().all()) byAccount.computeIfAbsent(b.accountId(), k -> new ArrayList<>()).add(b);

        long sequence = ports.ledger().all().stream().filter(e -> cycle.equals(e.interestCycle())).count();
        List<Planned> plans = new ArrayList<>();
        List<InterestCharge> charges = new ArrayList<>();
        List<Long> skipped = new ArrayList<>();
        for (Map.Entry<Long, List<CategoryBalanceRecord>> group : byAccount.entrySet()) {
            long id = group.getKey();
            CardAccount account = ports.accounts().find(id).map(CardAccount::new)
                    .orElseThrow(() -> new ReferenceDataMissing("account", String.format("%011d", id)));
            if (account.closedFor(cycle)) {
                skipped.add(id);
                continue;
            }
            CardRecord card = ports.cards().findByAccountId(id).stream().findFirst()
                    .orElseThrow(() -> new ReferenceDataMissing("card", String.format("%011d", id)));
            BigDecimal total = Cents.ZERO;
            List<LedgerEntry> entries = new ArrayList<>();
            for (CategoryBalanceRecord balance : group.getValue()) {
                BigDecimal rate = rate(account.disclosureGroup(), balance);
                if (!InterestPolicy.charges(rate)) continue;
                BigDecimal interest = InterestPolicy.monthlyInterest(balance.balance(), rate);
                total = total.add(interest);
                String transactionId = "IN" + cycle.format(CYCLE) + String.format("%08d", ++sequence);
                entries.add(new LedgerEntry(transactionId, id, card.cardNumber(), INTEREST_TYPE, INTEREST_CATEGORY, INTEREST_SOURCE,
                        "Int. for a/c " + String.format("%011d", id), interest, 0, "", "", "", now, now, cycle));
                charges.add(new InterestCharge(id, balance.typeCode(), balance.categoryCode(), balance.balance(), rate, interest, transactionId));
            }
            plans.add(new Planned(account.close(cycle, total), total, entries));
        }

        List<AccountInterest> closed = new ArrayList<>();
        for (Planned p : plans) {
            p.entries().forEach(ports.ledger()::append);
            ports.accounts().save(p.closed().state());
            closed.add(new AccountInterest(p.closed().id(), p.total(), p.closed().balance()));
        }
        return new InterestRunResult(cycle, charges, closed, skipped);
    }

    /** INT-05: the account's group, else the DEFAULT group. */
    private BigDecimal rate(String group, CategoryBalanceRecord b) {
        return ports.disclosureRates().find(group, b.typeCode(), b.categoryCode())
                .or(() -> ports.disclosureRates().find(DisclosureRate.DEFAULT_GROUP, b.typeCode(), b.categoryCode()))
                .map(DisclosureRate::annualRatePercent)
                .orElseThrow(() -> new ReferenceDataMissing("disclosure group",
                        DisclosureRate.DEFAULT_GROUP + b.typeCode() + String.format("%04d", b.categoryCode())));
    }
}
