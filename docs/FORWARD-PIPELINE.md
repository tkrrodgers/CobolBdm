# The forward pipeline: from legacy COBOL to a service integrated into a host, reproducibly

**What this is.** The runbook for the whole chain: legacy COBOL, then Java, then a forward-engineered service, then that
service integrated into an ERP. It is written so that another engineer, or a model, can repeat it on any package. Every
stage is a command with fixed inputs and a recorded output. The model (Gemma 4) is called in two places only:
* the service's engine (design and code, FE sections 8 and 12);
* the holes of the host plugin (FE section 9.1, S8).

Everything Gemma is told comes from deterministic material: digests, generated code, facade digests, scenarios and judges.

**Designs this follows.** These are the owner's documents, kept with our appendix rows in the JavaToCOBOLTest repo root:
* `ForwardEngineeringFineract.md` (FE): the forward service and its integration into OFBiz.
* `GemmaJavaTemplates.md` (TD): the template system that generates everything except the holes.

Where the work changed the design, the change is a row in FE Appendix E or TD Appendix F.

---

## 1. The chain

| # | Stage | Kind | Input | Output | Gate |
|---|---|---|---|---|---|
| 1 | Legacy estate to faithful Java | deterministic (J1–J5 generator, clean room) | COBOL, copybooks, JCL, PROCs | the clean-room project, with the record kit and traces | G2: byte-identical to GnuCOBOL |
| 2 | SME package | SME (deterministic tools; the pools from `forward.json` `pools`) | stage 1, the specification | `forward/<pkg>`: `forward.json`, SPEC, RULES, API, digests, scenarios, reference, leak dictionary | Phase 0 gate (`<pkg>/gate.json`) |
| 2b | Legacy layer | deterministic (templates) | the record kit's layouts, `<pkg>/legacy/legacy-mapping.yaml` | the legacy records and `LegacyCodec` in the API module's `src/generated` | leak check; TG1; every record of the legacy book decodes |
| 3 | Engine design | **model** | the package | architecture and slice designs | schema and trace checks |
| 4 | Engine code | **model** (debug mode) | the design and the package | `<run>/implementation/java` | every scenario, architecture tests |
| 5 | Engine jars | deterministic | stage 4 | `<pkg>/host/build/engine-<label>/*.jar` | the service's own build |
| 6 | API model | deterministic | the API jar | `<pkg>/host/api-model.json` | sorted, reproducible |
| 7 | Service model | deterministic | the API model and `<pkg>/host/host-mapping.yaml` | `<pkg>/host/service-model.json` | builder checks; leak check |
| 8 | Plugin | deterministic (templates) | the service model and the environment profile | the plugin, `holes.json`, `generated.lock.json` | TG1 (two renders identical) |
| 9 | Host gate | deterministic | the SME's reference holes, host scenarios, mutants | `<pkg>/host/gate.json` | reference passes every scenario; every mutant fails one |
| 10 | Holes | **model** (debug mode) | `holes.json`, host digest, facade digest, scenarios | `<run>/host/java` | every host scenario, checkstyle 0 |
| 11 | Final | deterministic | stages 5, 8 and 10 | `<pkg>/host/final-<label>.json` | host scenarios; TG3 (generated code untouched) |
| 12 | Regression (G12) | deterministic, a checkout of its own | stages 5, 8 and 10 | `<pkg>/host/regression.json` | every OFBiz test has the same outcome with and without the plugin |
| 13 | Shadow run (G11) | deterministic, a checkout of its own | stages 5, 8 and 10; the clean-room project and its oracle | `<pkg>/host/shadow-<label>.json` | ten days of bookings in the host, each day's export run through the legacy job: every booking reconciled, no unexplained drift |

Stages 1 and 2 are documented where they live:
* the generator and clean room: `fineract-cobol-gen/docs`;
* the SME package: FE section 7, and each package's `tools/Test-ForwardGate.ps1`. The package's scenario pools are rebuilt
  from its `forward.json` `pools` section (`pipeline.sh pools`). The candidates they read, `sme/pool` (1.3 GB), are not
  committed: `sme/tools` builds them from the corpus, the clean-room oracles A and A′ and FNEM0300's CALL traces
  (`build_schedule_pool.py`, `build_*_candidates.py`, `trace_rates.py`, `make_rate_probes.py`).

This runbook covers stages 2b to 13.

**When a model's failure shows the package is wrong.** A sweep (S5, privileged) or the shadow run (G11) can show a model's
engine wrong where every scenario passes. First check the digest against the reference on one such loan. When the digest is
the one at fault, the SME repairs it the way FE 7.2 step 3 says:
* restate the rule with a worked example, checked on the reference by the pool;
* add a mutant that makes the model's mistake;
* add candidates that kill it, with C's values (traced probes);
* rebuild the pools and pass the gate.

Then the model repairs its slice in debug mode: a warm start, with the other slices resumed. The loans run's RATE-05 is the
example (FE Appendix E, 2026-10-15).

## 2. Commands (the loans service into OFBiz)

These are the exact commands of the loans run. Every path is relative to the CobolBdm root, except the eval directory
`C:\Users\Tomrodgers\Source\modelEval`.

```bash
# once per OFBiz pin: H2 closes (and so flushes) at exit, loadAll, the base snapshot
bash tools/forward/ofbiz-host.sh init

# 2: the SME's engine scenario pools (forward.json "pools", in order), then the Phase 0 gate (forward/loans/gate.json)
FCG=/c/Users/Tomrodgers/JavaToCOBOLTest/fineract-cobol-gen bash tools/forward/pipeline.sh pools
powershell -NoProfile -File forward/loans/tools/Test-ForwardGate.ps1

# 3-4: Gemma designs and implements the engine (llama-server with the frozen settings; debug mode). The loans run took the
# slices one at a time with RESUME/WARM; the result is modelEval/forward-loans/gemma-final.
PHASES="architecture slices implement" SLICES=calendar,rates,terms,schedule,disclosure,servicing TAG=final \
  bash /c/Users/Tomrodgers/Source/modelEval/forward-loans/run.sh

# 2b, 5-9: deterministic, from the package (the SME's host scenarios first, then the host gate on the reference engine)
FCG=/c/Users/Tomrodgers/JavaToCOBOLTest/fineract-cobol-gen bash tools/forward/pipeline.sh legacy api host-scenarios
IMPL=forward/loans/reference ENGINE_LABEL=reference bash tools/forward/pipeline.sh gate engine prepare host-gate
IMPL=/c/Users/Tomrodgers/Source/modelEval/forward-loans/gemma-final/implementation/java ENGINE_LABEL=gemma \
  bash tools/forward/pipeline.sh engine prepare

# 3-4 for a slice added later (S9, migration): its design only, the passing slices resumed, a slice the package's growth
# broke continued from its best code. The design directory starts as a copy of the earlier run's.
PREV='C:\Users\Tomrodgers\Source\modelEval\forward-loans\gemma-final\implementation'
PHASES="slices implement" ONLY=migration SLICES=calendar,rates,terms,schedule,disclosure,servicing,migration TAG=s9 \
  RESUME="$PREV" WARM="$PREV" bash /c/Users/Tomrodgers/Source/modelEval/forward-loans/run.sh

# 3-4 after the package was repaired (RATE-05): the same design, implement only; the slices that still pass are resumed,
# the others continue from their code (debug mode). Then the engine's jars, and the sweeps (PoolRunner with the engine's
# jars first on the classpath: pool/schedule, pool/privileged).
PREV='C:\Users\Tomrodgers\Source\modelEval\forward-loans\gemma-s9\implementation'
DESIGN='C:\Users\Tomrodgers\Source\modelEval\forward-loans\gemma-s9\design' PHASES=implement TAG=rate05 \
  SLICES=calendar,rates,terms,schedule,disclosure,servicing,migration RESUME="$PREV" WARM="$PREV" \
  bash /c/Users/Tomrodgers/Source/modelEval/forward-loans/run.sh
IMPL=/c/Users/Tomrodgers/Source/modelEval/forward-loans/gemma-rate05/implementation/java ENGINE_LABEL=gemma-rate05 \
  bash tools/forward/pipeline.sh engine

# 10: Gemma writes the holes (debug mode, 15 attempts, plateau 3)
bash /c/Users/Tomrodgers/Source/modelEval/forward-loans/run-host.sh

# 11: the holes judged on the prepared plugin (Gemma's engine), and the lock
HOLES=/c/Users/Tomrodgers/Source/modelEval/forward-loans/gemma-host/host/java LABEL=gemma bash tools/forward/pipeline.sh final

# 12: G12 in a second checkout (init there once: OFBIZ=<it> SNAPSHOTS=<its> ofbiz-host.sh init)
REGRESSION_OFBIZ=/c/Users/Tomrodgers/Source/ofbiz-g12 REGRESSION_SNAPSHOTS=/c/Users/Tomrodgers/Source/ofbiz-g12-snapshots \
  ENGINE_LABEL=gemma HOLES=/c/Users/Tomrodgers/Source/modelEval/forward-loans/gemma-host/host/java bash tools/forward/pipeline.sh regression

# 13: G11 in the second checkout: the stage prepares it with the engine (ENGINE_LABEL) and HOLES, then the shadow run; the
# CobolBdm host (MLCPU_HOST) runs the legacy job with its GnuCOBOL oracle, on the shadow days alone (its oracle API's only=),
# and the clean-room project is left as it was. SHADOW_DAYS (10), SHADOW_PER_DAY (500), OFBIZ_HEAP (6g).
REGRESSION_OFBIZ=/c/Users/Tomrodgers/Source/ofbiz-g12 REGRESSION_SNAPSHOTS=/c/Users/Tomrodgers/Source/ofbiz-g12-snapshots \
  ENGINE_LABEL=gemma-rate05 HOLES=/c/Users/Tomrodgers/Source/modelEval/forward-loans/gemma-host-v2/host/java LABEL=gemma \
  FCG=/c/Users/Tomrodgers/JavaToCOBOLTest/fineract-cobol-gen MLCPU_HOST=http://localhost:5100 bash tools/forward/pipeline.sh shadow
```

`pipeline.sh` reads everything that varies by package from the package itself:
* `forward.json`: the service, its API module and package, and its `host` section, which names the host lane's script;
* `host/host-mapping.yaml`;
* the SME's reference holes, scenarios and mutants.

The host lane's script (`tools/forward/ofbiz-host.sh` for OFBiz) knows the host. Each verb does one job:
* `render`: model, conform, render, scenarios and facade;
* `prepare`: compile and checkstyle, load the plugin's data, snapshot;
* `judge`: holes, compile and checkstyle, restore the snapshot, run the tests;
* `mutants`, `gate`, `regression`, `paths`, `lock-check`;
* `shadow`: G11. One OFBiz test run, because OFBiz's test runner rolls the database back after a run: the days' bookings
  and exports, then the legacy job (a scenario step that runs a command: `shadow_oracle.py legacy`), then each day
  reconciled by the migration service in SHADOW mode.

A verb that writes into a tree (`judge`, `regression`, `shadow`) takes a lock in its snapshots directory, so two runs never
share a tree.

## Results of the loans run (2026-10-15)

| What | Result |
|---|---|
| Engine (S1-S7, Gemma) | every scenario of its 6 slices (`gemma-final`) |
| Host gate | reference holes 73/73 host tests; 17/17 semantic mutants killed |
| Gemma's engine in OFBiz | with the reference holes: public 21/21, hidden 51/51 |
| S8 holes (Gemma), v1 material | passed: 10 attempts, 16 min (9 failures, all OFBiz Java API guesses) |
| S8 holes (Gemma), v2 material | passed: 2 attempts, 4 min (facade with imports and signatures; import repair) |
| S8 with the v3 tidy | v2's first reply passes as it is (an unused import removed) |
| G12 | 642 OFBiz tests, the same outcome with the plugin as without |
| Legacy book (S9, engine level) | 3,031 loans: 2,559 MATCH, 310 LEGACY_REJECT, 153 explained (D-2 16, D-3 113, D-5 17, D-6 7), 9 undecodable, no DIFF |
| Migration in OFBiz (reference engine) | 78/78 host tests, the 5 migration scenarios included |
| Phase 0 gate with the migration slice | passed: 1,752 scenarios, PIT per module at least 87.2% (migration 97.2%), 94 rules traced, 0 of 136,530 differ |
| S9 engine (Gemma) | migration designed in 1 attempt and implemented in 5 (10 min, debug mode); servicing repaired for SRV-11 in 1; public 326/326, hidden 1,311/1,311 |
| All Gemma in OFBiz | Gemma's engine and holes in the generated plugin: 78/78 host tests, TG3 unchanged |
| G12, all Gemma | 642 OFBiz tests, the same outcome with Gemma's plugin as without |
| G11, reference engine | 10 days, 5,000 bookings, every one reconciled: 4,943 MATCH, 57 EXPLAINED D-2, 0 DIFF |
| G11, Gemma's engine (`gemma-s9`) | not passed: 9 loans on the precision-3 days. The cause was an SME digest error (RATE-05: the legacy rounds the day count to the precision) |
| RATE-05 repaired (SME) | digest and worked example, mutant RATE-05-B, 168 traced probe candidates, 6 new scenarios; gate passed |
| Gemma's repair (`gemma-rate05`) | rates passed on attempt 1 (one line, edits only); S5 sweep 0 of 136,530, privileged 0 of 652 |
| All Gemma, repaired engine | host tests 78/78 (TG3 unchanged); G10 (3,031 legacy loans) the reference's counts, 0 DIFF; G11 passed (the reference's counts); G12 642/642 |
| `pipeline.sh pools` | the eight engine pools rebuilt from `forward.json` byte-identically (11 privileged scenarios' stale rule tags refreshed) |

## 3. What a package provides

| File | What | Who writes it |
|---|---|---|
| `forward.json` | `mlcpu.forward-package.v1`: service, modules, slices, rule patterns, entry point, testing text, leak dictionary. Its `host` section names the judge script, host digests, facade digest, generated context types, scenario sets, examples and code rules | SME |
| `SPEC.md`, `RULES.md`, `digests/*.md` | the service's specification, design rules and rule digests (each rule verified by a mutant) | SME |
| `service/` | the fixed API, architecture tests, acceptance runner, scenarios | SME |
| `reference/` | the SME's implementation, which passes every scenario | SME |
| `gate.json` | the engine's Phase 0 gate | `tools/Test-ForwardGate.ps1` |
| `host/host-mapping.yaml` | where each API value lives in the host: plugin, enumerations, aggregates (codec prefixes, column renames, field types, children), ports (generated or hole), services, SECAs, seed and demo data, the facade lists, test data | SME |
| `host/reference/` | the SME's reference holes | SME |
| `host/digests/*.md` | the host rules as the holes see them | SME |
| `host/scenarios/` | host scenarios (public, hidden, smoke) and their runner settings | SME generator (`sme/.../OfbizPool`) |
| `host/mutants.json` | one or more semantic mutants per host rule | SME |
| `host/api-model.json`, `host/service-model.json`, `host/ofbiz-facade.md` | derived | the pipeline |
| `host/gate.json`, `host/mutants-result.json`, `host/final-*.json` | results | the pipeline |

A new host needs three things:
* an environment profile (`environments/<host>.yaml`);
* a template bundle (`templates/H-<HOST>-*`);
* a host lane script with the same verbs.

The harness (`fwd-host`) and `pipeline.sh` do not change.

## 4. How Gemma is directed (and what it never sees)

**Engine prompts.** These come from the package only:
* the specification, rules and API;
* the digests of the slice;
* the design;
* the public scenarios;
* the walks of failing public scenarios.

A leak checker blocks any prompt that carries a source-system name.

**Host prompts (`fwd-host`).** These hold:
* the holes' contracts from `holes.json`;
* the API types the holes implement, and every type those reach;
* the generated types the holes use (`LoansPortContext`);
* the host digest;
* the facade digest, generated from the pinned OFBiz source;
* the public host scenarios, two of them in full;
* the code rules (OFBiz checkstyle).

**Repairs (debug mode).** A repair turn sends a focused prompt with:
* the contract;
* the digest;
* the types and facade entries that the code or feedback names;
* the current code;
* the scenarios that pass;
* the feedback: build errors with lines, public failures with the differing rows, hidden failures as a count.

Three further rules apply:
* An attempt that loses a passing public scenario is told which (the ratchet).
* Two attempts worse than the best lead back to the best (the roll-back).
* With half or more passing, the model may only send edits.

**What Gemma never sees:**
* the hidden scenarios;
* the reference;
* the mutants;
* OFBiz's source, other than the facade digest;
* any generated file it may not change. It sees the generated types it uses, marked "do not change".

## 5. Reproducibility

Each of these is pinned:
* **Models:** the Gemma 4 31B BF16 weights, llama.cpp b11539, context 131,072, sampling (temperature 1.0, top-p 0.95,
  top-k 64), seed 42, MTP drafter.
* **Code:** the harness commit (`run.log` names it, and flags uncommitted changes).
* **OFBiz:** the commit (`environments/ofbiz-plugin.yaml`, `45506b377c`), JDK 21 compiling with `--release 17`.

Each of these is checked:
* the template catalog version and each template's version (stamped into every file);
* the model hash (stamped into every file);
* TG1 on every render;
* TG3 at the end.

The gates (`gate.json`, `host/gate.json`) must pass before a model runs, so a model is never judged by scenarios that cannot
see a rule.
