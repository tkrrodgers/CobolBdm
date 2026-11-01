# Loop 4 handoff: Joda-Money (new J3S-T target, prepared by the lead overnight)

Source: JodaOrg/joda-money @39d5bbf91c01dcac8234cc2fb3ac167320df670c (Apache-2.0), main code 4.5K lines. Workbench readiness
was "converter gaps": a static currency registry loaded from class-path CSVs through regex into a ConcurrentHashMap.

J3S conversion: run 20261101-034452-joda-money
(C:/Users/Tomrodgers/JavaToCOBOLTest/claude-in-app/workspace/runs/20261101-034452-joda-money).
- target/: 27 event types.
  - Currency lookups: CU and C2 (of, including unknown and malformed codes), CN (numeric), CC (country), RC (registered).
  - BigMoney: BM, OT, O2, BP, BS, BX, BD, WS, RD, WC, CP, CV, TM, NI.
  - Money: MO, MP, MM, MX, MD, MN, MR, MC.
- Scenarios: accept-01 (30 cases) and replay-01..05 (300 each), with edges, 1% damaged records and invalid inputs (unknown
  currency or country, negative or same-currency rates, UNNECESSARY rounding, divide by zero).
- check: 0 unsupported constructs (285 reachable methods). Build reproducible (72 members); cobc clean; 63 programs, 65,280
  generated lines.
- G2: 6/6 PASS byte-identical against oracle A (12,845 records, 811 rejects). The lead verified target/g2/g2.json (05:08).
- Estate: .../runs/20261101-034452-joda-money/estate: 65 programs including the kernels, 10 copybooks, 1 JCL, 6
  scenarios, 65,774 lines.

J3S generator additions (JavaToCOBOLTest-l3 worktree, branch feature/l3-target, commit 21735cd on c054e42; not merged,
not pushed):
- Emulations: ConcurrentHashMap (the JDK 25 node table, reproduced for iteration order), ConcurrentSkipListMap (String
  keys), Pattern/Matcher (a subset in Java's matching order), class-path resources embedded at build time (new profile key
  `resources`), Class/ClassLoader/reflection (profile key `reflection`), and small JDK members.
- G0: 569,713 records with 0 differences; all 11 mutants caught.
- Regression: the Fineract (156), kontor-zins (39) and loan-amortization-calculator (68) builds are byte-identical.

Open problems, for Tomrodgers and the J3S owner:
1. **J3S BUG (high priority, not fixed):** `==` between two Object-typed values ignores the value, so two boxed Integers
   with different values compare identical (Objects.equals(Integer, Integer) is always true). The agent reports that
   fixing it changes the Fineract estate. Either Fineract reaches such comparisons and G2 passed only because the
   compared values happened to be equal, or the comparisons sit in unreached code. This needs a reachability analysis
   and a targeted G2 case before anything else relies on boxed-value identity. Joda-Money's replay avoids
   plus(Iterable)/total, which use `==` on BigDecimal.
2. A Class object created mid-case is cached across the per-case heap reset (Joda-Money creates its Class objects in
   class initialization, so it is not affected).
3. Deliberate traps (abend 31): the CurrencyUnit failure handlers, a throw inside a try-with-resources body,
   ConcurrentHashMap tree bins, non-String skip-list keys, regex outside the subset, malformed UTF-8.
4. Not replayed: registerCurrency (static state across cases), plus(Iterable)/total, getNumeric3Code.
5. Capacities are set from measured peaks (1,839 of 4,000 strings), not proven.

Pipeline steps: as loop 3.
1. Onboard via the host; the oracle must equal reference.json on 6/6.
2. Audit "loop4 iter0".
3. Wire GN4 for the event job.
4. Exit gate: JOBOL PASS, build clean, GN4 6/6 plus error paths, all regression baselines green (loop 3's final added).

Why it is a strong loop: 65 programs; a registry built at class initialization from embedded data; regex parsing;
currency-scale rules; rounding modes; conversion. It is the first estate whose COBOL contains emulated JDK collection and
regex machinery, which the modernizer should recognize and replace with the JDK behind differential gates, the loop-1
kernel method.
