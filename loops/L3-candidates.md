# Loops 3-5: new J3S targets (lead's preparation, 2026-10-31 night)

| Candidate | Acquired | Readiness (workbench) | Blocker | Work to make it ready | Decision |
|---|---|---|---|---|---|
| JodaOrg/joda-money @39d5bbf (Apache 2.0, 40 files, 4.5K lines) | yes: workspace/packages/jodaorg-joda-money-39d5bbf, sha256 f926a880... | converter gaps | CurrencyUnit's static registry loads MoneyData.csv through getResourceAsStream/BufferedReader into a ConcurrentHashMap; every Money depends on it. org.joda.convert is annotation-only (not reached at run time). 4 thread uses (not on the replay path). | J3S: emulate ConcurrentHashMap/ConcurrentMap (on the deterministic JHashMap), and turn the currency resource into generated constant data (or a CURRFILE-style dataset, as Fineract's currency KSDS). Then a replay spec: Money/BigMoney arithmetic, rounding, conversion, allocation events. | **L3 candidate**, after loop 1 starts. The J3S work goes in JavaToCOBOLTest, not the pipeline's repo. |
| darmstrong1/jmortgage @ca5ce1d | yes (earlier) | converter gaps | Guava (30 imports), Joda-Time (7), 73 doubles | Two library emulations plus exact-double handling | Shelved |
| OpenGamma strata-basics (date logic) | no | not run | Joda-Beans, Guava, double year fractions | Large | Later, only if Guava emulation exists |
| Fineract savings interest posting | vendored | not run | Spring/JPA domain (not the embeddable module) | A port of the calculation core | Later |

Selection rule (lead): a target qualifies when its replayed API is deterministic, BigDecimal/LocalDate-based, and needs at
most two bounded J3S emulations. Every emulation must pass its own G0 differential gate against the JDK, as JHashMap and
FSJBDEC did.
