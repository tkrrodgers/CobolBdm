package com.mlcpu.cas.statements;

import com.mlcpu.cas.api.CasPorts;
import com.mlcpu.cas.api.ReferenceDataMissing;
import com.mlcpu.cas.api.ports.AccountRecord;
import com.mlcpu.cas.api.ports.CustomerLink;
import com.mlcpu.cas.api.ports.CustomerProfile;
import com.mlcpu.cas.api.ports.LedgerEntry;
import com.mlcpu.cas.api.statements.AccountStatement;
import com.mlcpu.cas.api.statements.CategoryActivity;
import com.mlcpu.cas.api.statements.CustomerStatement;
import com.mlcpu.cas.api.statements.StatementQuery;
import com.mlcpu.cas.api.statements.StatementRejection;
import java.math.BigDecimal;
import java.time.LocalDateTime;
import java.time.YearMonth;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.List;
import java.util.Map;
import java.util.TreeMap;
import java.util.function.Function;

/**
 * The customer statement (rules STMT). The 12-month average is the customers context's rule (AVG-01); the application passes it in,
 * so it lives in one place.
 */
public final class Statements {
    private static final BigDecimal ZERO = BigDecimal.ZERO.setScale(2);

    private final CasPorts ports;
    private final Function<CustomerProfile, BigDecimal> monthlyAverage;

    public Statements(CasPorts ports, Function<CustomerProfile, BigDecimal> monthlyAverage) {
        this.ports = ports;
        this.monthlyAverage = monthlyAverage;
    }

    public CustomerStatement statement(StatementQuery q) {
        CustomerProfile profile = ports.customers().find(q.customerId() + CustomerProfile.RECORD_TYPE)
                .filter(CustomerProfile.class::isInstance).map(CustomerProfile.class::cast)
                .orElseThrow(() -> new ReferenceDataMissing("customer", q.customerId()));
        CustomerLink link = ports.customerLinks().find(q.customerId())
                .orElseThrow(() -> new ReferenceDataMissing("customer link", q.customerId()));
        List<AccountStatement> accounts = new ArrayList<>();
        for (long id : link.accountIds()) {
            AccountRecord account = ports.accounts().find(id).orElseThrow(() -> new ReferenceDataMissing("account", String.format("%011d", id)));
            accounts.add(account(account, q.cycle()));
        }
        return new CustomerStatement(q.customerId(), q.cycle(), profile.name(), profile.ordersYtd(), monthlyAverage.apply(profile), accounts);
    }

    private AccountStatement account(AccountRecord account, YearMonth cycle) {
        List<LedgerEntry> entries = ports.ledger().all().stream().filter(e -> e.accountId() == account.accountId()).toList();
        Map<String, List<LedgerEntry>> byCategory = new TreeMap<>();
        BigDecimal postings = ZERO, interest = ZERO, later = ZERO;
        for (LedgerEntry e : entries) {
            YearMonth of = cycleOf(e);
            if (of == null) continue;
            if (of.isAfter(cycle)) later = later.add(e.amount());
            else if (of.equals(cycle)) {
                if (e.interestCycle() != null) interest = interest.add(e.amount());
                else {
                    postings = postings.add(e.amount());
                    byCategory.computeIfAbsent(e.typeCode() + String.format("%04d", e.categoryCode()), k -> new ArrayList<>()).add(e);
                }
            }
        }
        List<CategoryActivity> activity = byCategory.values().stream().map(list -> new CategoryActivity(list.get(0).typeCode(),
                list.get(0).categoryCode(), list.size(), list.stream().map(LedgerEntry::amount).reduce(ZERO, BigDecimal::add))).toList();
        BigDecimal closing = account.currentBalance().subtract(later);
        BigDecimal opening = closing.subtract(postings).subtract(interest);
        List<StatementRejection> rejections = ports.rejections().all().stream()
                .filter(r -> r.accountId() != null && r.accountId() == account.accountId() && YearMonth.from(r.rejectedAt()).equals(cycle))
                .sorted(Comparator.comparing((com.mlcpu.cas.api.ports.RejectionRecord r) -> r.rejectedAt())
                        .thenComparing(r -> r.transaction().transactionId()))
                .map(r -> new StatementRejection(r.transaction().transactionId(), r.reason(), r.transaction().amount(), r.rejectedAt()))
                .toList();
        return new AccountStatement(account.accountId(), opening, closing, activity, interest, rejections);
    }

    /** The cycle an entry belongs to: its interest cycle, else the month it was processed in (null when it never was). */
    private static YearMonth cycleOf(LedgerEntry e) {
        if (e.interestCycle() != null) return e.interestCycle();
        LocalDateTime processed = e.processedTimestamp();
        return processed == null ? null : YearMonth.from(processed);
    }
}
