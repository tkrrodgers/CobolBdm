package com.mlcpu.capabilities.interestcalculation;

import static com.mlcpu.forwardexport.Jw.obj;

import com.mlcpu.forwardexport.CardDemoJson;
import com.mlcpu.forwardexport.Jw;
import com.mlcpu.rt.StepEquivalence;
import java.io.IOException;
import java.util.Map;
import java.util.Set;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/** Forward-engineering export (forward/cas): every CBACT04C equivalence scenario run by the faithful program and decoded. */
class ForwardExportTest {
    static final String AT = "2022-07-05T10:11:12.000000";

    @BeforeAll
    static void oracle() {
        StepEquivalence.pinOracleEnvironment("2022-07-05T10:11:12-07:00");
    }

    @Test
    void export() throws IOException {
        String out = Jw.outDir() + "/accounts";
        Set<String> skip = Set.of("interest overflows its picture", "positive numbers stored unsigned (F-sign)", "short PARM");
        int n = 0;
        for (var args : InterestCalculationEquivalenceTest.scenarios().toList()) {
            String name = (String) args.get()[0];
            if (skip.contains(name)) continue;
            String parm = (String) args.get()[1];
            @SuppressWarnings("unchecked") Map<String, byte[]> in = (Map<String, byte[]>) args.get()[2];
            n++;
            String q = InterestCalculationEquivalenceTest.Q;
            Map<String, Object> initial = obj("accounts", CardDemoJson.accounts(in.get(q + "ACCTDATA.VSAM.KSDS")),
                    "cards", CardDemoJson.cards(in.get(q + "CARDXREF.VSAM.KSDS")),
                    "categoryBalances", CardDemoJson.categoryBalances(in.get(q + "TCATBALF.VSAM.KSDS")),
                    "disclosureRates", CardDemoJson.disclosureRates(in.get(q + "DISCGRP.VSAM.KSDS")));
            String cycle = parm.substring(0, 4) + "-" + parm.substring(4, 6);
            StepEquivalence.Outcome o = StepEquivalence.run(InterestCalculationEquivalenceTest.step(parm), in, "legacy");
            Map<String, Long> accountOf = CardDemoJson.accountOfCard(in.get(q + "CARDXREF.VSAM.KSDS"));
            byte[] systran = o.files().entrySet().stream().filter(e -> e.getKey().contains("SYSTRAN")).map(Map.Entry::getValue).findFirst().orElse(null);
            Map<String, Object> legacy = obj("returnCode", o.returnCode(), "detail", o.detail(), "parm", parm,
                    "transactions", CardDemoJson.ledger(systran, accountOf),
                    "accounts", CardDemoJson.accounts(o.files().get("data/" + q + "ACCTDATA.VSAM.KSDS")), "sysout", CardDemoJson.sysout(o));
            Jw.save(out, String.format("interest-%02d.json", n), obj("name", name, "context", "accounts", "program", "CBACT04C", "at", AT,
                    "initial", initial, "command", "accrueInterest", "input", obj("key", "interest-1", "cycle", cycle), "legacy", legacy));
        }
    }
}
