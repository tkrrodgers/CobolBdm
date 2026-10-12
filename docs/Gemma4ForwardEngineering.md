# Gemma 4 as the forward-engineering step of the COBOL modernization pipeline

**What this document is.** The results of the 2026-10-10/11 proof of concept, the evidence for why Gemma 4 succeeded, and
a setup guide precise enough for another engineer or LLM to configure Gemma 4 as the last step of the pipeline. It also
documents the format of every input Gemma needs.

**Sources.** Every number below comes from the run records:
* `tools/eval/PROTOCOL-forward.md` (section 14);
* `tools/eval/PIPELINE-V2.md`;
* `tools/eval/DEVIATIONS.md`;
* `<evalRoot>\forward\REPORT.md`, `results.json` and the per-attempt `summary.json` files;
* the SME package in `forward/cas`.

`<evalRoot>` is `C:\Users\Tomrodgers\Source\modelEval`.

---

## Executive summary

**The question.** Can a local model, Gemma 4 31B, take requirements recovered from legacy COBOL and build a new service
that nobody has designed yet? Can it both design the service and write the code, and is a larger model, gpt-oss-120b, a
better designer?

**What we built for the test (the SME package, `forward/cas`).** A new **Customer Account Service**. It combines the
business rules of five COBOL programs from two unrelated estates:
* CardDemo: CBTRN02C posting, CBACT04C interest and CBTRN03C reporting;
* the IBM sample: SAM1 customer maintenance and SAM2 field updates.

It adds a **customer statement** that no legacy program has. The SME wrote:
* the specification;
* a fixed Java API;
* the design rules, enforced by architecture tests;
* one rule digest per legacy program, 68 numbered rules in total;
* a reference implementation;
* 285 acceptance scenarios.

Models see 58 of the scenarios; the other 227 are hidden from them. The scenarios were derived by running the faithful COBOL
translations, which are byte-identical to GnuCOBOL. The reference agrees with those programs on every scenario, except
where one of 11 written decisions deliberately fixes a legacy defect. It passes all 285 scenarios, with a mutation score of
98.2%.

**What we ran.**
1. **Design round.** Gemma and gpt-oss each designed the architecture three times, with seeds 42, 7 and 1234.
   * A blind scorer rated the six designs against a fixed rubric, without knowing the authors.
   * Each model's best design was detailed per implementation slice.
2. **Implementation round.** Gemma implemented both models' best designs, slice by slice: accounts, customers, reporting,
   statements. Each design was implemented three times, with seeds 42, 7 and 1234.
   * Arm S: Gemma's own design.
   * Arm X: gpt-oss's design.

**Results.**

| | Gemma 4 | gpt-oss-120b |
|---|---|---|
| Blind design score (mean of 3, out of 5) | 2.83 | **3.18** |
| Designs valid on the automated checks | 3/3 | 3/3 |
| Gemma implementing this model's design: hidden scenarios passed (mean over 3 seeds) | **100%** (227/227 in every seed) | 99.0% (226, 222, 226 of 227) |
| ... runs with every scenario exact | **3/3** | 0/3 |
| ... mean attempts / mean minutes | **9.7 / 15.2** | 30 / 44.4 |

The comparison run, Gemma on pipeline v2 against v1 (re-engineering the five programs byte for byte), gave this:

| | v1 | v2 |
|---|---|---|
| Programs passed | 5/5 | 5/5 |
| Attempts | 31 | 28 |
| Minutes | 64.6 | 40.9 |
| Services against the GnuCOBOL oracle | 17/17 job outputs | 17/17 job outputs |

**The conclusions.**
1. **Gemma 4 can do forward engineering end to end, design and code.** On its own design it built the whole service
   exactly in every seed: 285/285 scenarios, architecture tests 7/7. It took 7 to 12 attempts in 12 to 21 minutes of GPU
   time.
2. **gpt-oss-120b is not a better designer for this pipeline.**
   * Its designs read better. They were more complete and recorded every decision, so the blind rubric rated them higher.
   * Its margin, 0.35, is under the 0.5 required for the designer role.
   * Its designs stated three rules vaguely or wrongly. Gemma implemented those statements literally, so every run of arm
     X missed hidden scenarios, at three times the cost.
   * Decision: **Gemma 4 designs and implements.**
3. **The largest factor in Gemma's success was the quality of its inputs.** Gemma succeeded because:
   * the requirements were exact, numbered and verified against the legacy programs;
   * the API and the architecture rules were fixed and executable;
   * the work came in small vertical slices with all their context;
   * every mistake produced precise, machine-checked feedback.

   Section 3 gives the evidence for each.
4. **Models do not review designs usefully.** Asked to score each other's designs, both models gave almost everything 5/5.
   Design review needs the SME, or a blind scorer with a written rubric.

**What this means for the pipeline.** Gemma 4 can be set up as the last step, after re-engineering. It takes a
specification package (section 5), designs the target service, and implements it slice by slice against
oracle-derived scenarios. The step's quality depends most on the requirements package, not on the model. A design must
never contradict the digests. When it did, Gemma followed the design.

**Limits of the evidence.**
* One target service and one hardware setup.
* Arm X's attempt counts are inflated by a harness flaw (section 3.8): failures carried over from an earlier slice counted
  against later slices that could not fix them. Pass rates are unaffected.
* The statement feature has no legacy oracle; its expected values rest on the SME's specification.

---

## 1. Where forward engineering sits in the overall pipeline

```
 legacy estate (COBOL, copybooks, JCL, data)
        │
        ▼  1. Onboard (workbench Settings)               projects/<id>/source, project.json
        ▼  2. GnuCOBOL oracle (Settings)                 oracle outputs per job and scenario: the truth
        ▼  3. Transpile (Conversion)                     faithful Java per program, byte-identical to the oracle
        ▼  4. Publish (Conversion)                       the normalized Spring estate:
        │       publish/<estate>/normalization/<PID>/requirements.json   (requirements packet, section 5.1)
        │       .../record-kit.md, record-layouts.json, ast-outline.txt, examples.md (pipeline v2)
        │       src/test/.../<Capability>EquivalenceTest.java            (byte-for-byte acceptance)
        ▼  5. Re-engineering: Gemma 4, per program (Gemma 4 Modernization)
        │       design (mlcpu.capability-design.v1) -> Java on the platform layer, judged byte for byte
        │       result: the same jobs, idiomatic Java, 17/17 outputs equal to GnuCOBOL
        ▼  6. Forward engineering: Gemma 4, per target service (this document; Forward Engineering tab)
                SME package (forward/<service>): SPEC, RULES, digests, API, scenarios derived from step 3's faithful programs
                -> Gemma designs (1a architecture, 1b slices) -> Gemma implements slice by slice -> judged on hidden scenarios
```

Steps 1 to 5 preserve behaviour. Step 6 builds something new on top of the recovered rules:
* new module boundaries and a domain model;
* new features, in the POC the customer statement;
* new semantics where the business decides to fix a legacy defect.

**Its two inputs come from earlier steps:**
* **The requirements packets (step 4).** They are the source of the rule digests. Gemma also reads the full packets when it
  details each slice's design (1b).
* **The faithful programs (step 3).** Running them on the published equivalence-test inputs produces the expected values of
  the acceptance scenarios (section 5.7). This is why the scenarios can be trusted: they are not anyone's opinion of the
  legacy behaviour, they are its output.

---

## 2. The proof of concept in numbers

### 2.1 Re-engineering on pipeline v2: Gemma against Gemma (arm A, seed 42)

| Program | v1 attempts | v2 attempts | v2 attempt sequence (C = compile errors, x/y = scenarios) |
|---|---|---|---|
| CBTRN02C | 5 | 5 | C6 C1 3/23 22/23 **23/23** |
| CBACT04C | 10 | 6 | C1 C1 3/17 15/17 16/17 **17/17** |
| CBTRN03C | 6 | 11 | C19 C19 C1 3/23 C1 4/23 18/23 C1 20/23 20/23 **23/23** |
| SAM1 | 8 | 5 | C14 C1 C3 0/13 **13/13** |
| SAM2 | 2 | 1 | **13/13** |

* 28 attempts against 31 (−10%) and 40.9 minutes against 64.6 (−37%).
* CardDemo services: 122/122 tests and 11/11 job outputs. zopeneditor: 65/65 and 6/6.
* Pipeline v2 added the generated record kit, field-level difference feedback, SEARCH/REPLACE repairs and import
  completion. The same repair mechanics carry into forward engineering (section 3.6).

### 2.2 Phase 0: the SME package and its quality gate

| Item | Value |
|---|---|
| Bounded contexts and implementation slices | accounts (CBTRN02C, CBACT04C), customers (SAM1, SAM2), reporting (CBTRN03C), statements (new) |
| Rules | 68: POST-01..13, INT-01..12, RPT-01..09, CUST-01..10, UPD-01..09, AVG-01, STMT-01..10, IDEM-01..04 |
| Decisions where the service deliberately differs from legacy | 11 (D-1 to D-11, `forward/cas/SPEC.md` section 8) |
| Scenarios (public / hidden) | 285 (58 / 227). Per slice: accounts 37/142, customers 15/55, reporting 4/18, statements 2/12 |
| Reference implementation | 285/285 scenarios, architecture tests 7/7 |
| Mutation score (PIT, context modules) | 98.2% (161 of 164 mutants killed) |
| Cross-check against the faithful programs | 200 scenarios agree; 44 differ only where a decision D-n applies |

### 2.3 Design round (step 1a, blind SME rubric; consistency re-score within one point)

| Model | Seed | Rubric (of 5) | C1 domain | C2 rules | C3 money | C4 idempotency | C5 statement | C6 simplicity | Tokens generated | Minutes |
|---|---|---|---|---|---|---|---|---|---|---|
| gpt-oss-120b | 42 | **3.35** | 3 | 4 | 3 | 4 | 3 | 3 | 18,826 | 2.0 |
| gpt-oss-120b | 7 | 3.30 | 3 | 3 | 4 | 4 | 3 | 3 | 13,615 | 1.4 |
| gpt-oss-120b | 1234 | 2.90 | 3 | 3 | 3 | 3 | 3 | 2 | 16,888 | 1.7 |
| Gemma 4 | 7 | **3.00** | 3 | 3 | 3 | 4 | 2 | 3 | 8,605 | 2.1 |
| Gemma 4 | 42 | 2.75 | 2 | 3 | 3 | 3 | 3 | 3 | 7,331 | 1.9 |
| Gemma 4 | 1234 | 2.75 | 2 | 3 | 3 | 4 | 2 | 3 | 13,430 (2 attempts) | 3.3 |

The rubric's weights are C1 25%, C2 20%, C3 15%, C4 15%, C5 15%, C6 10% (`forward/cas/rubric.md`). The best design of
each model, gpt-oss seed 42 and Gemma seed 7, went on to the slice designs (1b). Every slice design of both models passed
its checks: gpt-oss needed one second attempt and Gemma two.

### 2.4 Implementation round (Gemma 4; 15 attempts per slice; pass = architecture green, all public, hidden ≥ 90%)

| Arm | Design by | Seed | Hidden | Exact | Slices fully passed | Attempts per slice (accounts, customers, reporting, statements) | Total attempts | Minutes |
|---|---|---|---|---|---|---|---|---|
| S | Gemma 4 | 42 | 227/227 | yes | 4/4 | 2, 3, 1, 1 | 7 | 11.6 |
| S | Gemma 4 | 7 | 227/227 | yes | 4/4 | 8, 2, 1, 1 | 12 | 20.8 |
| S | Gemma 4 | 1234 | 227/227 | yes | 4/4 | 3, 1, 1, 5 | 10 | 13.3 |
| X | gpt-oss-120b | 42 | 226/227 | no | 3/4 | 4, 6, 7, 8 | 25 | 35.5 |
| X | gpt-oss-120b | 7 | 222/227 | no | 2/4 | 7, 7, 7, 7 | 28 | 48.6 |
| X | gpt-oss-120b | 1234 | 226/227 | no | 3/4 | 6, 12, 7, 12 | 37 | 49.1 |

Every run had architecture tests 7/7 and public scenarios 58/58, so every run passed. Only arm S was exact.

---

## 3. What made Gemma 4 successful: the evidence

### 3.1 Exact, numbered, verified requirements

**What Gemma got.** One digest per legacy program, in a fixed form. Each forward rule has an id, one precise statement
and the legacy origin (the packet's rule ids and quirks). Numeric rules come with worked examples. For example, UPD-03
reads update amounts with two implied decimals: `000012345` is 123.45, and `+000000099` reads its first nine characters,
so it is 0.09. Order and precedence rules are stated outright: POST-06 "103 wins over 102", UPD-05 "INVALID_ACTION wins
over NOT_NUMERIC". Every behaviour the new service changes is a numbered decision with its reason.

**Why it mattered:**
* Arm S passed every hidden scenario in all three seeds, including all the subtle numeric rules: interest truncated to the
  cent, unsigned 7- and 5-digit SAM fields, whole-part averages. The digests state each of these exactly.
* The digests themselves had been checked against the legacy programs before any model ran. That check found two errors
  in the SME's first reading of SAM1/SAM2:
  * a 10-character update value is read only to its 9th character;
  * a `PERFORM ... WITH TEST AFTER` loop writes a 13th month into the occupation field.

  Both were fixed in the digests and in decision D-11. Without the check, the requirements would have taught Gemma two
  wrong rules, and the scenarios would have agreed with them.
* When a model reads a vague or wrong statement, it implements that statement. This is the clearest lesson of arm X
  (section 3.3).

**Rule for the pipeline.** Generate the digests from the requirements packets and verify them against the faithful
programs. Never hand Gemma requirements that have not been executed against the oracle.

### 3.2 A fixed API and architecture rules that are tests

**What Gemma got:**
* the complete source of `cas-api`: use-case interface, commands, results, typed rejections with the legacy reason codes,
  persistence snapshots, outbound ports and `CasPorts`;
* the entry point `com.mlcpu.cas.application.CasApplication.create(CasPorts)`;
* `RULES.md`, design rules A-1 to A-11, each enforced by an ArchUnit test:
  * hexagonal dependencies;
  * no I/O, clock or randomness in the domain;
  * final fields;
  * BigDecimal money;
  * no frameworks;
  * package placement.

**Why it mattered:**
* The entry point and the module contract were never broken: no contract problem about them in any run.
* The architecture tests were green at the end of all 6 runs. The only violation Gemma made in arm S was a non-final field
  (S seed 42, customers attempt 1: `contextFieldsAreFinal()`). The test reported it in full, and Gemma fixed it with two
  SEARCH/REPLACE edits in the next attempt.
* Fixing the module set and packages (`cas-accounts`, `cas-customers`, `cas-reporting`, `cas-statements`,
  `cas-application`) left the design work where it belongs: the domain model, rule placement, idempotency and the new
  feature.

### 3.3 Gemma's own design restated the rules faithfully, and Gemma implements designs literally

**Evidence from arm S.** Gemma's chosen architecture (seed 7) is short:
* about 24,000 characters;
* 68 rule traces, 4 decisions, 2 risks.

It states the invariants as the digests do: "Projected cycle balance must not exceed credit limit (POST-04)". Its slice
designs carry short, exact implementation notes, for example:
* "Use setScale(2, RoundingMode.DOWN) for truncation toward zero as per INT-07";
* "accrueInterest must process CategoryBalanceRecords in the order returned by CategoryBalances.all()";
* "accrueInterest must implement a staging phase ... so that if ReferenceDataMissing is thrown, no modifications are
  committed (INT-10)".

**Evidence from arm X.** gpt-oss's designs were longer and scored higher on completeness, but contained statements that
contradict the digests:
* "Current balance never exceeds credit limit" (POST-04 tests the projected cycle balance, not the current balance);
* an invented rule: "Category balances are never negative; attempts to post a transaction that would cause a negative
  balance are rejected as OVERLIMIT";
* UPD-05 traced to a `validateAction` method with no statement of the precedence;
* no statement that a closed account is skipped (INT-02) before its card is looked up (INT-04).

Gemma, implementing gpt-oss's design, made exactly these mistakes:

| Seed | Hidden failure in arm X | Rule | Cause in the design |
|---|---|---|---|
| 42, 7 | `cust-047` / `cust-048`: expected `INVALID_ACTION`, got `NOT_NUMERIC` / `INVALID_FIELD` | UPD-05 precedence | Validation order unstated; the code threw on the first failure |
| 7 | `post-batch-01`, `post-batch-04`, `post-limit-sequence-*`: over-limit transactions posted | POST-04 | Wrong invariant; the code summed credit and debit |
| 1234 | `interest-skipped-without-card`: threw `ReferenceDataMissing("card")` | INT-02 before INT-04 | Order unstated; the code looked up the card first |

The digests in the same prompt state the first two rules explicitly. The third follows from INT-02's "skipped entirely: no
charge, no change", but no rule states the order, which is a lesson for digest writing too. Arm S got all three right in
every seed. When the design and the digest disagree, Gemma follows the design.

**Rules for the pipeline:**
* Let Gemma design. Its designs are less complete on paper, but they do not contradict the requirements.
* Check every design against the digests before implementation. The automated checks used here (schema, every rule
  traced, every API method assigned, allowed dependencies) caught none of these errors, because they check structure, not
  meaning. A design lint that compares invariants and notes with the digest text, or an SME review, belongs between steps
  1b and 2.
* In the implementation prompt, the digests come before the design, and the task says "every rule of SPEC.md and the
  digests, exactly". Keep both.

### 3.4 Vertical slices: one bounded context per conversation

**How the work was cut.** Four slices, in dependency order. Each slice is one conversation in which Gemma writes its
context module plus the application classes for its use cases.

| Slice | Context module | Use cases | Scenarios (public/hidden) | First prompt (tokens, arm S) |
|---|---|---|---|---|
| accounts | `cas-accounts` | postTransaction, postBatch, accrueInterest | 37/142 | 32,526 |
| customers | `cas-customers` | applyCustomerChanges | 15/55 | about 36,000 |
| reporting | `cas-reporting` | transactionReport | 4/18 | about 31,000 |
| statements | `cas-statements` | customerStatement | 2/12 | about 32,000 |

**Why it mattered:**
* **Small, complete prompts.** Every implementation prompt was 31,000 to 39,000 tokens against a 131,072-token context.
  That leaves room for Gemma's thinking and a reply of up to 49,152 tokens, and nothing had to be truncated.
* **The code already built goes into the prompt**, so a later slice wires itself to earlier ones without guessing. In arm
  S, later slices often passed on the first attempt: reporting 3 of 3 seeds, statements 2 of 3, customers 1 of 3.
* **Every attempt is judged on every scenario of every slice built so far.** A later slice cannot silently break an earlier
  one. No arm S run regressed an earlier slice.
* **The best attempt carries forward.** A slice that ends on a worse attempt does not poison the next slice.
* **Measured cost per slice** in arm S, mean over three seeds:
  * accounts 4.3 attempts (the largest slice, two legacy programs);
  * customers 2.0;
  * reporting 1.0;
  * statements 2.3.

### 3.5 Acceptance scenarios from the oracle, and feedback down to the first differing value

**What Gemma got back after each attempt:**
* **Compile errors:** each error once, with the numbered source lines around it. For a syntax error, only the first
  occurrence per file.
* **Architecture tests:** every failing test, with every violation.
* **Public scenarios:** the scenario id and the first differences by JSON path, for example:
  ```
  - public accounts interest-preclosed:
    step 1 accrueInterest.result.charges[0].interest: expected "4.29", got "4.30"
    state.accounts[1].currentBalance: expected "-617.27", got "-617.26"
  ```
* **Hidden scenarios:** only a count per slice: "Hidden scenarios that pass: accounts 137/142 (no details: check your code
  against every rule of SPEC.md and the digests)."

**Why it mattered:**
* **Public failures were fixed fast.** For example: S seed 7, accounts, 3 interest scenarios failing, fixed within 2
  attempts; S seed 1234, statements, `stmt-migrated-ledger`, fixed by the fourth attempt (one of them a compile error).
* **Gemma fixed hidden failures from the count alone,** by re-reading the rules:
  * S seed 42, customers: hidden 194 → 197 of 197 in one attempt;
  * S seed 7, accounts: 137 → 142 of 142;
  * S seed 1234, statements: 225 → 227 of 227.

  In each case every public scenario already passed and Gemma had no detail. This is the best evidence that Gemma
  understood the rules and was not fitting the tests.
* **The scenarios are exact by construction.** BigDecimal values compare by value and scale, timestamps to the
  microsecond, results and final store contents field by field. "Close enough" never passes.

### 3.6 Repair mechanics from pipeline v2, and a stable prompt prefix

* **SEARCH/REPLACE edits.** After the first attempt, Gemma may answer with edit blocks instead of whole files. Arm S's nine
  edit repairs used one to four edit blocks each, with replies of 400 to 6,500 tokens (five of them under 3,000), against
  2,400 to 9,300 for a whole slice.
* **Missing imports are added by the harness** for cas-api classes, the service's own classes and common `java.*`
  classes, so an import never costs an attempt. In arm X seed 1234's first accounts attempt, 15 imports were added.
* **A stable prefix and the KV cache.** Each repair request is:
  * the same system prompt;
  * the same full slice prompt;
  * the current files, as the assistant turn;
  * the new feedback.

  llama.cpp reuses its prompt cache for the identical prefix, so a repair processed only 200 to 6,000 new prompt tokens
  instead of 31,000 to 39,000. Do not rewrite or reorder the prompt between attempts.

### 3.7 Runtime: Gemma 4 31B at full precision with its draft model

* BF16 weights (46.5 GB on disk) with the MTP draft model for speculative decoding: **60 to 72 tokens/s** generated, the
  same output distribution.
* About 72 to 74 GiB of GPU memory on an RTX PRO 6000 (96 GiB), with a 131,072-token context and one slot. Loads in 25 to
  31 seconds.
* Google's sampling (temperature 1.0, top-p 0.95, top-k 64), thinking on. Section 4.5 has the exact settings.
* Typical times:
  * an architecture: 2 minutes, 7,000 to 9,000 tokens;
  * a slice design: 1 to 2.5 minutes;
  * a first slice implementation: 0.7 to 2.7 minutes;
  * a repair: 0.2 to 2 minutes, plus about 15 seconds of judging (Gradle build and 285 scenarios).

### 3.8 What did not help, and what hurt

| Observation | Evidence | Fix for the next round |
|---|---|---|
| **A design that contradicts the requirements** | Arm X: 0/3 exact, three times the attempts (section 3.3) | Gemma designs; lint designs against the digests |
| **Failures carried over from an earlier slice counted against later slices** | Arm X: reporting and statements ran 7 to 12 attempts after their own scenarios passed. Gemma kept trying to change modules the slice may not touch (contract problems), or replied with no code. | The plateau rule and the hidden-count feedback should use the slice's own scenarios, while still reporting regressions of earlier slices |
| **A file returned twice in one reply** (Gemma corrected itself mid-reply) | S seed 42, accounts attempt 1: a contract problem, one attempt lost | Accept the later copy |
| **Peer review by models** | Both models scored almost every design 5/5; the blind SME scores ranged 2.75 to 3.35 | Do not use model review as a quality gate |
| **The automated design checks** | All 6 architectures and all 8 slice designs passed; they could not see the contradictions | Keep them for structure; add a semantic check |
| **A thin domain model is not penalized by the tests** | Arm S's code is plain services over the port records (11 classes, 959 lines at seed 42), yet exact | Scenarios judge behaviour; structure needs the rubric or extra architecture rules |

---

## 4. Setup guide: Gemma 4 as the last step

This section is written so that another engineer or LLM can reproduce the setup for a new target service.

### 4.1 Prerequisites

| Item | Value used in the POC |
|---|---|
| GPU | NVIDIA RTX PRO 6000 Blackwell (96 GiB). Gemma BF16 uses about 72 to 74 GiB at a 131k context |
| System RAM | 256 GB (llama-server working set about 69 GB) |
| Inference runtime | llama.cpp `llama-server`, build unslothai b11539 (`C:\models\llama.cpp-unsloth-b11539`) |
| Model | `C:\models\gemma-4-31B-it-GGUF\BF16\gemma-4-31B-it-BF16-00001-of-00002.gguf` (first shard of two) |
| Draft model | `C:\models\gemma-4-31B-it-GGUF\MTP\mtp-gemma-4-31B-it-BF16.gguf` |
| Harness | `MlCpu.DesignModel` (.NET 10), built in Release from this repository |
| Java | JDK 21, Gradle 9.4. The judge runs `gradle --offline`, so populate the dependency cache once online (JUnit 5.11.3, Jackson 2.22.1, ArchUnit 1.4.1) |
| Upstream outputs | The published estates of step 4 (`<evalRoot>\workspace-v2\projects\<project>\publish\...`) with their requirements packets and equivalence tests |

### 4.2 Inputs Gemma needs (checklist)

Before Gemma runs, these must exist and pass the Phase 0 gate. Section 5 gives each format.

| # | Input | Where (POC) | Used in |
|---|---|---|---|
| 1 | Specification | `forward/cas/SPEC.md` | 1a, 1b, 2 |
| 2 | Design rules | `forward/cas/RULES.md` | 1a, 1b, 2 |
| 3 | The fixed API (Java source) | `forward/cas/service/cas-api/src/main/java/...` | 1a, 1b, 2 |
| 4 | Rule digests, one per legacy program | `forward/cas/digests/<PID>.md` | 1a (all), 1b and 2 (the slice's) |
| 5 | Requirements packets | `<publish>/normalization/<PID>/requirements.json` | 1b (the slice's, in full) |
| 6 | JSON Schemas of the designs | `forward/cas/schemas/*.schema.json` | 1a, 1b |
| 7 | The judge build: API, architecture tests, acceptance runner, in-memory ports, scenarios | `forward/cas/service/` | 2 (the in-memory ports and public examples also go into the prompt) |
| 8 | Scenario manifest | `forward/cas/scenarios.json` | 2 (the public list) |
| 9 | Run configuration | `tools/eval/eval-config-forward.json` | runner |

Not shown to Gemma:
* the hidden scenarios' contents (only counts are reported);
* the reference implementation (`forward/cas/reference`);
* the rubric (`forward/cas/rubric.md`).

### 4.3 Procedure

1. **Build the SME package (Phase 0, CPU only).** For a new target service, write `SPEC.md`, `RULES.md`, the digests and
   the API. Then build the scenarios from the faithful programs and write a reference implementation. Use the POC's tools
   as templates:
   * `forward/cas/sme/export/<estate>/.../ForwardExportTest.java` runs each published equivalence scenario through the
     faithful program and decodes its inputs and outputs into the API's JSON (the legacy drafts);
   * `forward/cas/sme/src/test/java/.../ScenarioBuild.java` (with `Tags`, `CrossCheck`, `Authored`):
     * fills in expected values from the reference;
     * cross-checks them against the faithful programs (`cross-check.md`);
     * adds SME-authored scenarios;
     * tags rules;
     * splits about 20% public by stratified sampling on each scenario's rarest rule (seed 20260010), with at least one
       public scenario for every rule that has three or more;
   * `forward/cas/tools/Build-Scenarios.ps1` runs both.
2. **Pass the gate.** Run `forward/cas/tools/Test-ForwardGate.ps1`; it writes `forward/cas/gate.json`. The gate requires:
   * the reference passes every scenario and every architecture test;
   * a mutation score of at least 85%;
   * every rule tagged on a scenario;
   * every rule with three or more scenarios has a public one.

   Every cross-check difference must be explained by a decision D-n. If not, fix the reference, the digest or the decoder
   first.
3. **Design (1a), Gemma.** `Invoke-ForwardEval.ps1 -Phase architecture -Model gemma-4-31B-it` writes
   `<evalRoot>\forward\designs\gemma-4-31B-it\seed-<n>\architecture.json` for each configured seed. In production one seed
   is enough. To choose among several, score them against `rubric.md`.
4. **Check the design against the digests** (the lesson of section 3.3). Read every invariant, policy and note. Any
   statement that contradicts or weakens a digest rule must be corrected before going on. This step was not automated in
   the POC.
5. **Slice designs (1b), Gemma.** `Invoke-ForwardEval.ps1 -Phase slices -Model gemma-4-31B-it -Seed <n>` writes
   `slice-<name>.json` next to the architecture. Check them as in step 4.
6. **Implementation (2), Gemma.** Run `Invoke-ForwardEval.ps1 -Phase implement -Model gemma-4-31B-it -Arm S
   -Design <designs\gemma-4-31B-it\seed-n> -Designer gemma-4-31B-it`. It writes `implementation\S-gemma-4-31B-it\`:
   * `summary.json`;
   * per slice: the prompt, every attempt and its reasoning, the slice's best code;
   * `java\`: the final code, in the module layout.
7. **Verify and publish.** The final judgement in `summary.json` must show:
   * every slice compiled;
   * architecture tests green;
   * all public scenarios passing;
   * hidden at least 90%, and 100% for exact.

   Then run `tools/eval/New-ForwardReport.ps1` to write `results.json` and `REPORT.md`. The workbench's Forward Engineering
   page (`/forward`) shows them with the code.

To run several steps unattended (one GPU job at a time; it keeps Windows awake):

```powershell
powershell -ExecutionPolicy Bypass -File tools\eval\Start-ForwardQueue.ps1 -Steps "Phase=architecture;Model=gemma-4-31B-it|Phase=slices;Model=gemma-4-31B-it;Seed=7|Phase=implement;Model=gemma-4-31B-it;Arm=S;Design=C:\...\forward\designs\gemma-4-31B-it\seed-7;Designer=gemma-4-31B-it"
```

### 4.4 The harness commands (what the runner calls)

```text
dotnet MlCpu.DesignModel.dll fwd-architecture --cas forward\cas --out <dir> --seed 42 --attempts 3 --max-tokens 32768 <model options>
dotnet MlCpu.DesignModel.dll fwd-slices       --cas forward\cas --architecture <dir>\architecture.json
                                              --normalization "<carddemo publish>\normalization;<zopeneditor publish>\normalization"
                                              --out <dir> --seed 7 --attempts 3 --max-tokens 45000 <model options>
dotnet MlCpu.DesignModel.dll fwd-implement    --cas forward\cas --design <dir with architecture.json and slice-*.json>
                                              --arm S --designer <label> --impl-attempts 15 --max-tokens 49152 --plateau 3
                                              --test-timeout-min 15 --harness <scratch dir> --out <dir> --seed 42 <model options>
<model options> = --model <gguf> --name gemma-4-31B-it --server http://127.0.0.1:8190 --no-fallback --ctx 131072
                  --reasoning high --impl-reasoning high
```

`fwd-review` (peer review) exists, but section 3.8 shows it is not useful. For dry runs without a model, use
`--backend scripted --exchange <dir>`: call N returns `reply-N.md`.

### 4.5 Runtime settings (exact)

```text
llama-server -m <gemma BF16 shard 1> -c 131072 -np 1 --host 127.0.0.1 --port 8190 --jinja -fa on --no-webui -to 14400
             --reasoning-format auto --seed <seed> --temp 1 --top-p 0.95 --top-k 64 --min-p 0
             --spec-type draft-mtp --spec-draft-model <mtp-gemma-4-31B-it-BF16.gguf>
```

Each request goes to `POST /v1/chat/completions` with:
* `stream: true`, `seed: <seed>`, `cache_prompt: true`;
* `max_tokens` of 32,768 for an architecture, 45,000 for a slice design, 49,152 for an implementation;
* `chat_template_kwargs: { "reasoning_effort": "high", "enable_thinking": true }`.

Gemma's template has no effort levels; thinking is on. Keep the conversation shape stable: system, user (the full prompt),
then for repairs the assistant turn (current files) and a user turn (feedback). No fallback model.

### 4.6 Stop rules and acceptance

* **A slice ends** when any of these holds:
  * it compiles, the architecture tests are green and every scenario passes, public and hidden;
  * three consecutive attempts pass every public scenario without improving the hidden count (the plateau);
  * 15 attempts are used.
* **The slice's deliverable** is its best attempt: architecture green first, then the most scenarios passed. The next
  slice starts from it.
* **The service passes** with architecture green, all public scenarios and at least 90% of the hidden ones. It is **exact**
  at 100%.
* **The contract Gemma must keep** (checked before compiling; a breach is fed back as a contract problem):
  * the package decides the module;
  * a slice may change only its context module and `cas-application`;
  * simple class names are unique;
  * one top-level type per file;
  * the entry point `CasApplication.create(CasPorts)` exists.

### 4.7 Writing the package for a new set of requirements

* **Bounded contexts.** One module per business context, not per legacy program. Two programs that change the same
  aggregate share a context: posting and interest both change the card account.
* **The API.** Fix the use cases, the commands and results, typed rejections carrying the legacy codes, persistence
  snapshots and outbound ports. State each port's ordering (key order, append order) in its Javadoc, because results
  depend on it.
* **The digests.**
  * One rule per row, with an id, the exact statement and the legacy origin.
  * Numbers: scale, truncation, implied decimals and unsigned fields, with worked examples.
  * State every order and precedence explicitly ("A wins over B"; "checked first").
  * List what is not carried forward.
* **Decisions.** Every deliberate deviation from legacy becomes a numbered decision with its reason. The cross-check must
  explain every difference by one.
* **Scenarios:**
  * every equivalence scenario of the published estate, run through the faithful program;
  * single-transaction cuts, so each rule has small examples;
  * SME-authored scenarios for idempotency, ordering, cross-context flows and new features.
* **The gate before any model runs.** It is the evidence that the scenarios can judge.

---

## 5. Input formats (reference)

### 5.1 Requirements packet (`mlcpu.requirements-packet.v1`, step 4 output)

One JSON document per legacy program, extracted by static analysis of the COBOL source:
`<publish>/normalization/<PID>/requirements.json`. Gemma reads it in full in the slice design (1b). The digests (5.4) are
distilled from it.

| Field | Content |
|---|---|
| `schema`, `programId`, `requirementsHash` | Identity; the hash pins the packet |
| `source` | `file`, `sha256`, `lines`, `copybooks[]` |
| `purpose` | Header comments, the capability name, package, domain, naming confidence, rationale, citations |
| `invocation[]` | Each job step running the program: `job`, `step`, `stepCitation`, `dds[]` |
| `dataContracts[]` | Records: `record`, `file`, `length`, `fields[]` (each `name`, `offset`, `length`, `pic`, `usage`, `scale`, `signed`, `type`) |
| `ports[]` | Files: `file`, `dd`, `organization`, `access`, `alternateKeys`, `direction`, `statusField`, `operations`, `recordLength` |
| `controlFlags[]` | Flag fields and their values |
| `errorHandling` | Error paragraphs and the behaviour on unexpected status |
| `paragraphs[]` | `paragraph`, `line`, `endLine`, `role`, `segments` |
| `rules[]` | `id` (R-PID-nnn), `paragraph`, `kind`, `when[]` (the guarding conditions), `statements[]` (`stepId`, `line`, `verb`, `cobol`), `reads[]`, `writes[]` |
| `observableOutputs` | `console[]` (DISPLAY statements), `returnCode[]`, `files[]` (with volatile fields and layouts) |
| `legacyQuirks[]` | `id` (Q-nn), `kind` (for example `last-assignment-wins`, `unreachable-branch`, `stale-record-on-invalid-key`), `paragraph`, `stepId`, `description`, `evidence` |
| `semantics[]` | COBOL semantics that apply (receivers keep their PICTURE, truncation, and so on) |
| `acceptance` | The oracle, equality, clock pinning, regression command |
| `designContract` | The re-engineering design's output contract and rules |

Example rule:

```json
{"id": "R-CBTRN02C-009", "paragraph": "1500-B-LOOKUP-ACCT", "kind": "logic",
 "when": ["READ ACCOUNT-FILE INTO ACCOUNT-RECORD INVALID KEY", "IF ACCT-CREDIT-LIMIT >= WS-TEMP-BAL"],
 "statements": [{"stepId": "1500-B-LOOKUP-ACCT:8:MOVE:L410", "line": 410, "verb": "MOVE", "cobol": "MOVE 102 TO WS-VALIDATION-FAIL-REASON"}],
 "reads": [], "writes": ["WS-VALIDATION-FAIL-REASON", "WS-VALIDATION-FAIL-REASON-DESC"]}
```

### 5.2 Specification (`SPEC.md`)

Markdown, about 13,000 characters in the POC, in these sections:

1. **Purpose:** what the service replaces and adds.
2. **Bounded contexts and modules:** a table of context, module and package, responsibility, and rule prefixes; the
   entry-point contract; the allowed dependencies.
3. **Glossary:** each business term defined once (account, card, transaction, ledger, cycle, ...), with scales and formats.
4. **Use cases:** a table of method, kind and summary; how failures are reported: outcome values versus a typed exception,
   and all-or-nothing operations.
5. **Cross-cutting rules with ids:** idempotency (IDEM-01..04).
6. **New features with ids:** the customer statement (STMT-01..10).
7. **Data and number rules:** scales, truncation, value comparison, list ordering.
8. **Decisions D-n:** a table of id, legacy behaviour (with its packet origin), what the service does, and why.
9. **Non-goals.**

Rules are written `**STMT-07:** statement ...` in the spec, and as `| POST-04 | ... |` table rows in the digests. The
tooling finds rule ids by these two patterns.

### 5.3 Design rules (`RULES.md`)

A table with columns Rule, Statement and Enforced by. "Enforced by" names the ArchUnit test method or the scenarios. The
POC's rules:
* A-1: a context depends only on the API, the JDK and itself.
* A-2: the application depends on the API and the contexts.
* A-3: no `java.io`, `java.nio`, `java.net`, `System`, `Thread` or randomness in the contexts, and no clock other than
  `CasPorts.clock()`.
* A-4: final fields.
* A-5: no `double` or `float`, and no `BigDecimal` built from a double.
* A-6: no frameworks.
* A-7: packages.
* A-8: the entry point.
* A-9: typed rejections.
* A-10: idempotency.
* A-11: all-or-nothing interest.

The ArchUnit tests (`service/cas-architecture`) evaluate every rule and report all violations at once.

### 5.4 Rule digest (`digests/<PID>.md`)

```markdown
# Rule digest: CBTRN02C (daily transaction posting) → context `accounts`, rules POST
**Legacy program:** what it reads, writes and decides (one paragraph).
**In the service:** which use cases implement it.

## Validation, in this order
| Rule | Statement | Legacy origin |
|---|---|---|
| POST-04 | Over limit: the projected cycle balance is `currentCycleCredit - currentCycleDebit + amount`. When the credit limit is **less than** it, the reason is `OVERLIMIT` (102). A limit equal to the projected balance passes. | R-CBTRN02C-008, -009 |
| POST-06 | POST-04 and POST-05 are both evaluated. When both fail, the reason is `EXPIRED_ACCOUNT` (103). | Quirk Q-03 (kept as the business rule) |

## Numbers
(scales, truncation, worked examples)

## Not carried forward
(legacy channels, abends replaced by typed failures, quirks without effect; each with its decision D-n)
```

At 2,000 to 3,000 tokens per program, the five digests total about 21,000 characters.

### 5.5 The fixed API (`cas-api`)

Java 21 source in `com.mlcpu.cas.api..`, about 25,000 characters in 52 files. Gemma sees it complete in every prompt. It
follows these conventions:

| Kind | Convention | Example |
|---|---|---|
| Use cases | One interface with a method per use case, each documented with its rule prefix | `CustomerAccountService.postTransaction(PostTransaction)` |
| Commands | Records carrying an `IdempotencyKey` | `record PostBatch(IdempotencyKey key, List<TransactionInput> transactions)` |
| Results | Records, or sealed interfaces with record cases | `sealed interface PostingOutcome permits Posted, Rejected` |
| Rejections | Enums carrying the legacy code and text | `OVERLIMIT(102, "OVERLIMIT TRANSACTION")` |
| Failures | One typed exception for missing reference data | `ReferenceDataMissing(what, key)` |
| Persistence | Snapshot records and port interfaces, with ordering in the Javadoc | `CategoryBalances.all()`: key order (account, type, category) |
| Wiring | One record of all outbound ports, including `java.time.Clock` | `record CasPorts(Accounts accounts, ..., Clock clock)` |
| Entry point | Specified in SPEC.md, implemented by the model | `CasApplication.create(CasPorts)` |

Lists in records are copied with `List.copyOf` in compact constructors. Money is `BigDecimal` with 2 decimals; the
12-month average has scale 0.

### 5.6 Design documents (JSON Schemas in `forward/cas/schemas`)

**`mlcpu.service-design.v1` (step 1a, the architecture).** Required fields:

| Field | Content |
|---|---|
| `summary` | The architecture in a few sentences |
| `modules[]` | Exactly the five implementation modules: `module`, `responsibility`, `dependsOn[]`, `packages[]` |
| `domainModel[]` | One per context: `aggregates[]` (`name`, `root`, `invariants[]`, `members[]`), `valueObjects[]`, `domainServices[]`, `policies[]` (`name`, `rule`) |
| `useCases[]` | One per API method: `method`, `class`, `flow[]`, `idempotency`, `consistency` |
| `ruleTrace[]` | Every rule exactly once: `ruleId`, `module`, `class`, `method`, `note` |
| `decisions[]`, `risks[]` | `id`, `decision`, `rationale`; `risk`, `mitigation` |

Automated checks, fed back to the model on failure:
* schema;
* all five modules;
* package roots;
* allowed dependencies;
* every context present;
* every API method assigned to an application class;
* every rule traced exactly once to a class in the right module.

**`mlcpu.slice-design.v1` (step 1b, per slice).** Fields: `slice`, `classes[]` (`name` fully qualified, `module`, `kind`,
`responsibility`, `fields[]`, `methods[]` with `signature`, `purpose`, `rules[]`), `ruleTrace[]` (the slice's rules, each
to a class of `classes[]`) and `notes[]`. The notes carry numeric details, ordering and edge cases. Checks:
* classes only in the slice's module or `cas-application`;
* unique simple names;
* every rule of the slice traced to a listed class.

**`mlcpu.design-review.v1`** (peer review): scores C1 to C6, each with a justification. It is not recommended
(section 3.8).

### 5.7 Acceptance scenarios

**The manifest, `forward/cas/scenarios.json` (`mlcpu.cas-scenarios.v1`).** One entry per scenario:

```json
{ "id": "post-310", "slice": "accounts", "visibility": "public", "primaryRule": "POST-02",
  "rules": ["POST-02", "POST-11"], "origin": "legacy CBTRN02C: 'unknown cards (reason 100) / transaction 10' (...)" }
```

**A scenario file**, `service/cas-acceptance/src/test/resources/scenarios/<visibility>/<slice>/<id>.json`:

```json
{
  "id": "post-310", "slice": "accounts", "origin": "...", "rules": ["POST-02", "POST-11"],
  "initial": { "accounts": [], "cards": [], "categoryBalances": [], "ledger": [] },
  "steps": [ {
    "at": "2022-07-05T10:11:12.000000",
    "command": "postTransaction",
    "input": { "key": "post-1", "transaction": { "transactionId": "0000000021711604", "typeCode": "01", "categoryCode": 1,
               "source": "POS TERM", "description": "Purchase at Gleason, Shanahan and Reynolds", "amount": "416.11",
               "merchantId": 800000000, "merchantName": "...", "merchantCity": "Myrticeport", "merchantZip": "21768-0823",
               "cardNumber": "9999999999999999", "originTimestamp": "2022-06-10T19:27:53.000000" } },
    "expect": { "result": { "@type": "Rejected", "transactionId": "0000000021711604", "reason": "INVALID_CARD" } }
  } ],
  "expectState": { "accounts": [], "categoryBalances": [], "ledger": [],
                   "rejections": [ { "transaction": { ... }, "accountId": null, "reason": "INVALID_CARD",
                                     "rejectedAt": "2022-07-05T10:11:12.000000" } ] }
}
```

**`initial` stores** (any subset): `accounts`, `cards`, `categoryBalances`, `disclosureRates`, `transactionTypes`,
`transactionCategories`, `ledger`, `rejections`, `customers`, `customerLinks`. Each is a list of the API's snapshot
records.

**`command`** is one of the use-case methods: `postTransaction`, `postBatch`, `accrueInterest`, `applyCustomerChanges`,
`transactionReport`, `customerStatement`.

**`expect`** is either `{"result": <canonical value>}` or `{"throws": {"type": "ReferenceDataMissing", "what": "...",
"key": "..."}}`. Any other exception surfaces as `{"throws": {"type", "message", "at"}}`, and the test fails.

**`expectState`** names the stores to compare after the last step, in the order the ports list them.

**Canonical values** (both directions; `Json.java` in the acceptance runner):

| Java type | JSON |
|---|---|
| record | Object with the components in declaration order; a case of a sealed interface also carries `"@type": "<SimpleName>"` |
| `BigDecimal` | String with its scale: `"12.30"`, not `"12.3"` |
| `int`, `long` | Number (compared by value) |
| enum | Its name: `"OVERLIMIT"` |
| `LocalDate` / `LocalDateTime` / `YearMonth` | `"2022-07-05"` / `"2022-07-05T10:11:12.000000"` / `"2022-07"` |
| record with one `String` component (input only) | The plain string: `"key": "post-1"` |

**Failure messages** list the first differences by path, up to 5 per scenario:
`step 1 postBatch.result.outcomes[128].reason: expected "OVERLIMIT", got (nothing)`.

**Test names** are `public <slice> <id>` or `hidden <slice> <id>`. The judge reports public failures in full and hidden
results as counts.

### 5.8 The in-memory ports (shown to Gemma in every implementation prompt)

`service/cas-acceptance/.../InMemoryPorts.java`:
* accounts by id (sorted);
* cards by number, with `findByAccountId` in card-number order;
* category balances in key order;
* disclosure rates, types and categories by key;
* the ledger in append order, with `append` throwing on a duplicate transaction id;
* the rejection log in append order;
* customers by 6-character key (sorted);
* customer links by id;
* the command journal as a map;
* a clock set to each step's `at`, in UTC.

`LocalDateTime.now(ports.clock())` is the service's "now".

### 5.9 Run configuration (`tools/eval/eval-config-forward.json`)

```json
{
  "evalRoot": "C:\\Users\\Tomrodgers\\Source\\modelEval", "forward": "forward", "cas": "forward\\cas",
  "llamaDir": "C:\\models\\llama.cpp-unsloth-b11539", "port": 8190, "context": 131072,
  "normalization": ["<carddemo publish>\\normalization", "<zopeneditor publish>\\normalization"],
  "design": { "seeds": [42, 7, 1234], "attempts": 3, "maxTokens": 32768, "slicesMaxTokens": 45000 },
  "review": { "attempts": 3, "maxTokens": 24576 },
  "implementation": { "seed": 42, "attempts": 15, "maxTokens": 49152, "plateau": 3, "testTimeoutMinutes": 15 },
  "models": [ { "name": "gemma-4-31B-it", "roles": ["design", "implementation"], "gguf": "...BF16-00001-of-00002.gguf",
                "sampling": { "temp": 1.0, "top_p": 0.95, "top_k": 64, "min_p": 0.0 },
                "serverArgs": "--spec-type draft-mtp --spec-draft-model ...mtp-gemma-4-31B-it-BF16.gguf", "reasoning": "high" } ]
}
```

The runner refuses to start unless `forward/cas/gate.json` says `"pass": true`. It records the harness commit, and
whether the tree had uncommitted changes, with every run.

### 5.10 Prompt layouts (what Gemma reads)

All prompts are Markdown with fenced code and JSON. The section order matters: the requirements come before the design.

**1a, the architecture** (about 20,000 tokens; system prompt: senior software architect, "return ONE JSON document ...
nothing else"):

```
SPEC.md
# Design rules (RULES.md)
# The given API: cas-api (complete source; it is fixed)   (every file in a ```java block)
# Rule digest digests/<PID>.md                            (all five)
# JSON Schema (mlcpu.service-design.v1)
# Task: modules, domain model per context, one flow per API method (idempotency, what is written when),
        trace of every rule (all 68 ids listed), decisions and risks; implementable from the document alone
```

**1b, a slice design** (about 20,000 tokens for statements, 50,000 to 78,000 with the packets):

```
SPEC.md / RULES.md / the API
# Your architecture (mlcpu.service-design.v1)
# Rule digests of the slice
# Requirements packet of <PID>   (complete JSON, one per legacy program of the slice)
# JSON Schema (mlcpu.slice-design.v1)
# Task: every class of the context module and the application classes for the slice's use cases; fields, signatures,
        purpose, rules; trace every rule of the slice; notes on numbers, ordering and edge cases
```

**2, implementation of a slice** (31,000 to 39,000 tokens; system prompt: senior Java 21 engineer; idiomatic,
immutable records, BigDecimal money, no frameworks; answer with Java files only, each in a ```java block, or
SEARCH/REPLACE edits when repairing):

```
SPEC.md
# Design rules (RULES.md)
# The given API: cas-api
# Rule digest digests/<PID>.md                            (the slice's)
# The design you implement
## Architecture (mlcpu.service-design.v1)
## Slice design: <slice> (mlcpu.slice-design.v1)
# How the service is tested                               (scenario mechanics, value comparison, public and hidden)
## The in-memory adapters                                 (InMemoryPorts.java, complete)
# Code already built (earlier slices; it must keep passing their scenarios)   (every file, with its module path)
# Public scenarios of the slice "<slice>" (n)             (id, origin, rules; the three smallest files in full, each up to 9,000 characters)
# Task: contract (packages decide modules; only this slice's module and cas-application; unique simple names; allowed
        dependencies; the entry point; later slices' methods may throw UnsupportedOperationException; every rule exactly)
```

### 5.11 Gemma's reply format

* **Complete files.** One per fenced block that starts with ```` ```java ````. The first line is the package declaration,
  and each file holds one top-level type.
* **Repairs.** Edits, or complete files:
  ```
  <<<<<<< SEARCH Name.java
  the lines to replace, copied exactly from the current file
  =======
  the lines that take their place
  >>>>>>> REPLACE
  ```
  A complete file in the same reply wins over edits to it. An edit that does not apply is reported, and its file stays
  unchanged.
* **Designs.** One JSON document. The harness accepts it bare or in a ```` ```json ```` fence.

### 5.12 Feedback formats (sent after each attempt)

**Compile errors** (a syntax error shows only its first occurrence, with numbered source lines):

```
The code does not compile:
InterestPolicy.java:13: error: ';' expected
    12:     static boolean charges(BigDecimal annualRatePercent) {
>   13:         return annualRatePercent.signum() != 0
    14:     }
(further errors in this file follow from this one)
```

**A compiled attempt:**

```
Result: architecture 7/7; public 34/37; hidden 130/142 (accounts 164/179).

Architecture tests that fail (RULES.md):
- contextFieldsAreFinal(): Architecture Violation [...] Field <...> is not final ...

Public scenarios that fail (3; the first differences of each):
- public accounts interest-preclosed:
  step 1 accrueInterest.result.charges[0].interest: expected "4.29", got "4.30"

Hidden scenarios that pass: accounts 130/142 (no details: check your code against every rule of SPEC.md and the digests).

Fix the causes. <the edit instructions>
```

**Contract problems** (no compile): for example "X.java is in cas-customers, which this slice does not change (it may
change cas-reporting and cas-application)", or "the entry point ... is missing".

### 5.13 Outputs

| File | Content |
|---|---|
| `designs/<model>/seed-<n>/architecture.json`, `slice-<name>.json` | The designs, with `*.prompt.md`, `*.attempt<k>.txt`, `*.reasoning.txt` and `*.run.json` (validity, attempts, tokens, minutes) |
| `implementation/<arm>-<model>[-seed-<n>]/summary.json` (`mlcpu.forward-implementation-run.v1`) | Per slice: attempts, best attempt, how it ended, minutes, the best verdict. Per attempt: tokens, files returned, edits applied, imports added, the verdict (compiled, architecture, public and hidden per slice, failing public names). Also the final verdict, `passed` and `exact` |
| `implementation/.../<slice>/` | `implementation.prompt.md`, `attempt<k>.txt`, `attempt<k>.reasoning.txt`, `java/` (the slice's best code) |
| `implementation/.../java/` | The final service code, in the module layout (`cas-<module>/src/main/java/...`) |
| `results.json`, `REPORT.md` | The design round, arms and seeds, per-arm means and the decision (`New-ForwardReport.ps1`), read by the workbench's Forward Engineering page |

---

## 6. Recommendations for running this in production

1. **Use Gemma 4 for both roles.** It designed in 2 minutes and implemented its own design exactly in every seed.
2. **Invest in the requirements package; it is the main lever.**
   * Generate digests from the requirements packets.
   * Verify every digest and decision against the faithful programs (the cross-check).
   * State every order and precedence.
   * Gate the scenarios (mutation score, traceability) before any model runs.
3. **Add a semantic design check** between design and implementation. Every invariant, policy and note must agree with a
   digest rule. This would have caught every arm X failure.
4. **Fix the two harness issues found:**
   * the plateau and the hidden feedback per slice should use the slice's own scenarios;
   * accept the later copy of a file returned twice.
5. **Keep the slices small**, one bounded context each, with the code already built in the prompt. Keep the prompt prefix
   stable for the KV cache.
6. **Keep hidden scenarios.** Gemma's fixes from the hidden count alone are the evidence that it implements rules, not
   tests.
7. **Do not use model peer review** as a gate.
8. **Next round (deferred in the POC):** phase 3 integration.
   * Legacy adapters, so the new service can run on the legacy record layouts.
   * Data migration with reconciliation.
   * A shadow run against the faithful programs.

## 7. File index

| What | Where |
|---|---|
| Protocol, decisions, results | `tools/eval/PROTOCOL-forward.md` (section 14: results) |
| Pipeline v2 (re-engineering) and its results | `tools/eval/PIPELINE-V2.md` |
| Deviations during the runs | `tools/eval/DEVIATIONS.md` |
| SME package | `forward/cas/` (`README.md`, `SPEC.md`, `RULES.md`, `digests/`, `schemas/`, `rubric.md`, `scenarios.json`, `cross-check.md`, `gate.json`, `service/`, `reference/`, `sme/`, `tools/`) |
| Harness | `MlCpu/src/MlCpu.DesignModel/Forward/` (`ForwardInputs`, `ForwardDesign`, `ForwardImplement`, `ForwardJudge`) |
| Runner, queue, report, blinding | `tools/eval/Invoke-ForwardEval.ps1`, `Start-ForwardQueue.ps1`, `New-ForwardReport.ps1`, `New-BlindDesignSet.ps1`, `eval-config-forward.json` |
| Workbench page | `MlCpu/src/MlCpu.Host/Components/Pages/Forward.razor`, `Services/Workspace/ForwardService.cs` (`MlCpu:ForwardRoot`) |
| Run outputs | `<evalRoot>\forward\` (designs, blind set and scores, reviews, implementations, `REPORT.md`, `results.json`) |
