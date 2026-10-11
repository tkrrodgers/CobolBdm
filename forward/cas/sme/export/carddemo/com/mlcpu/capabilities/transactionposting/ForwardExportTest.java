package com.mlcpu.capabilities.transactionposting;

import static com.mlcpu.forwardexport.Jw.obj;

import com.mlcpu.carddemo.data.RecordLayouts;
import com.mlcpu.forwardexport.CardDemoJson;
import com.mlcpu.forwardexport.Jw;
import com.mlcpu.rt.StepEquivalence;
import java.io.IOException;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.TreeMap;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/**
 * Forward-engineering export (forward/cas): every CBTRN02C equivalence scenario run by the faithful program and decoded into the
 * Customer Account Service's terms. Batch drafts use the first 40 daily transactions (the CardDemo day whole); single drafts post
 * one daily transaction of a scenario against its state.
 */
class ForwardExportTest {
    static final String Q = TransactionPostingEquivalenceTest.Q;
    static final String AT = "2022-07-05T10:11:12.000000";

    @BeforeAll
    static void oracle() {
        StepEquivalence.pinOracleEnvironment("2022-07-05T10:11:12-07:00");
    }

    @Test
    void export() throws IOException {
        String out = Jw.outDir() + "/accounts";
        Set<String> skip = Set.of("positive numbers stored unsigned (F-sign)", "F-sign with zero amounts", "distinct category-balance fillers (Q-04)",
                "balances overflow their pictures", "account file missing (abend)", "daily file missing (abend)");
        int n = 0;
        for (var args : TransactionPostingEquivalenceTest.scenarios().toList()) {
            String name = (String) args.get()[0];
            if (skip.contains(name)) continue;
            @SuppressWarnings("unchecked") Map<String, byte[]> inputs = (Map<String, byte[]>) args.get()[1];
            n++;
            boolean whole = name.equals("CardDemo day");
            Map<String, byte[]> batch = whole ? inputs : firstDaily(inputs, 40);
            Jw.save(out, String.format("batch-%02d.json", n), draft(name, batch, "postBatch"));
            // Single transactions: every one of the CardDemo day; the first 12 of each other scenario.
            byte[] daily = inputs.get(Q + "DALYTRAN.PS");
            List<byte[]> records = daily == null ? List.of() : StepEquivalence.records(daily, 350);
            int singles = whole ? records.size() : Math.min(12, records.size());
            for (int i = 0; i < singles; i++) {
                Map<String, byte[]> one = new TreeMap<>(inputs);
                one.put(Q + "DALYTRAN.PS", records.get(i).clone());
                Jw.save(out, String.format("single-%02d-%03d.json", n, i), draft(name + " / transaction " + (i + 1), prune(one), "postTransaction"));
            }
        }
    }

    static Map<String, byte[]> firstDaily(Map<String, byte[]> in, int count) {
        Map<String, byte[]> m = new TreeMap<>(in);
        byte[] daily = m.get(Q + "DALYTRAN.PS");
        if (daily != null) m.put(Q + "DALYTRAN.PS", StepEquivalence.firstRecords(daily, 350, count));
        return prune(m);
    }

    /** Keeps only the cross-references, accounts and category balances the daily transactions reach. */
    static Map<String, byte[]> prune(Map<String, byte[]> in) {
        Map<String, byte[]> m = new TreeMap<>(in);
        Set<String> cards = new HashSet<>();
        if (m.get(Q + "DALYTRAN.PS") != null)
            for (byte[] r : StepEquivalence.records(m.get(Q + "DALYTRAN.PS"), 350)) cards.add(RecordLayouts.transaction(r).cardNumber());
        Set<Long> accounts = new HashSet<>();
        m.computeIfPresent(Q + "CARDXREF.VSAM.KSDS", (k, f) -> StepEquivalence.editRecords(f, 50, (i, r) -> {
            var x = RecordLayouts.crossReference(r);
            if (!cards.contains(x.cardNumber())) return null;
            accounts.add(x.accountId());
            return r;
        }));
        m.computeIfPresent(Q + "ACCTDATA.VSAM.KSDS", (k, f) -> StepEquivalence.editRecords(f, 300, (i, r) -> accounts.contains(RecordLayouts.account(r).id()) ? r : null));
        m.computeIfPresent(Q + "TCATBALF.VSAM.KSDS", (k, f) -> StepEquivalence.editRecords(f, 50,
                (i, r) -> accounts.contains(RecordLayouts.categoryBalance(r).key().accountId()) ? r : null));
        return m;
    }

    static Map<String, Object> draft(String name, Map<String, byte[]> in, String command) throws IOException {
        Map<String, Long> accountOf = CardDemoJson.accountOfCard(in.get(Q + "CARDXREF.VSAM.KSDS"));
        Map<String, Object> initial = obj("accounts", CardDemoJson.accounts(in.get(Q + "ACCTDATA.VSAM.KSDS")),
                "cards", CardDemoJson.cards(in.get(Q + "CARDXREF.VSAM.KSDS")),
                "categoryBalances", CardDemoJson.categoryBalances(in.get(Q + "TCATBALF.VSAM.KSDS")),
                "ledger", CardDemoJson.ledger(in.get(Q + "TRANSACT.VSAM.KSDS"), accountOf));
        List<Object> txs = CardDemoJson.inputs(in.get(Q + "DALYTRAN.PS"));
        Object input = command.equals("postBatch") ? obj("key", "batch-1", "transactions", txs) : obj("key", "post-1", "transaction", txs.get(0));

        StepEquivalence.Outcome o = StepEquivalence.run(TransactionPostingEquivalenceTest.step(), in, "legacy");
        // Outcomes: posted when the id reached TRANSACT (and was not there before), else the reason from the reject file.
        Set<String> before = new HashSet<>();
        for (Object e : CardDemoJson.ledger(in.get(Q + "TRANSACT.VSAM.KSDS"), accountOf)) before.add((String) ((Map<?, ?>) e).get("transactionId"));
        byte[] transact = o.files().get("data/" + Q + "TRANSACT.VSAM.KSDS");
        Set<String> after = new HashSet<>();
        List<Object> ledgerAfter = CardDemoJson.ledger(transact, accountOf);
        for (Object e : ledgerAfter) after.add((String) ((Map<?, ?>) e).get("transactionId"));
        Map<String, Integer> reasons = new TreeMap<>();
        List<Object> rejects = new ArrayList<>();
        byte[] rejs = o.files().get("data/" + Q + "DALYREJS");
        if (rejs != null)
            for (byte[] r : StepEquivalence.records(rejs, 430)) {
                var t = RecordLayouts.transaction(java.util.Arrays.copyOf(r, 350));
                int code = Integer.parseInt(new String(r, 350, 4, java.nio.charset.StandardCharsets.ISO_8859_1).trim());
                reasons.put(Jw.text(t.id()), code);
                rejects.add(obj("transactionId", Jw.text(t.id()), "reason", code, "description", Jw.text(new String(r, 354, 76, java.nio.charset.StandardCharsets.ISO_8859_1))));
            }
        List<Object> outcomes = new ArrayList<>();
        for (Object t : txs) {
            String id = (String) ((Map<?, ?>) t).get("transactionId");
            outcomes.add(reasons.containsKey(id) ? obj("transactionId", id, "reason", reasons.get(id))
                    : after.contains(id) && !before.contains(id) ? obj("transactionId", id, "posted", true) : obj("transactionId", id, "unknown", true));
        }
        Map<String, Object> legacy = obj("returnCode", o.returnCode(), "detail", o.detail(), "outcomes", outcomes,
                "accounts", CardDemoJson.accounts(o.files().get("data/" + Q + "ACCTDATA.VSAM.KSDS")),
                "categoryBalances", CardDemoJson.categoryBalances(o.files().get("data/" + Q + "TCATBALF.VSAM.KSDS")),
                "ledger", ledgerAfter, "rejects", rejects, "sysout", CardDemoJson.sysout(o));
        return obj("name", name, "context", "accounts", "program", "CBTRN02C", "at", AT, "initial", initial, "command", command, "input", input,
                "legacy", legacy);
    }
}
