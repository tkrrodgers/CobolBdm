# Java JOBOL Auditor: is the generated Java modern, or COBOL in Java syntax?

Status: **Phase 1 implemented and verified. Phase 2 implemented, tested in isolation and shipped disabled: it is not enabled or registered with any session until approved.**
Date: 2026-10-31. Author: Claude (desktop session, acting as architect/SME), for the CobolBdm pipeline.
Branch: `feature/jobol-auditor`.

## 1. Problem

The pipeline converts COBOL to Java. GPT Sol 6.1 generates, and Claude Opus 5.5 (high) checks as a sub-agent. The goal is modern Java, not *JOBOL*: code that compiles and passes parity but keeps COBOL's shape. The usual signs are WORKING-STORAGE fields, paragraph methods, `'Y'`/`'N'` switches, FILE STATUS strings and PICTURE masks.

A generator trained on COBOL drifts toward JOBOL, and an LLM checker grading the same output drifts with it. The pipeline needs a **deterministic, model-free judge**: same input, same score, with each finding citing a file and line, a rule and a fix.

Generic static analysis does not solve this. SonarQube, PMD and Checkstyle see `private int dwRead` as a fine field and `void readRecord()` as a fine method. JOBOL is a *pattern of habits* across a class, not one bad line. The auditor measures those habits directly and publishes SARIF, so SonarQube and IDEs can still display the results.

## 2. Decisions

| Decision | Choice | Why |
|---|---|---|
| Engine | JavaParser 3.28.2 AST, Java 21 CLI (`tools/jobol-auditor`) | Several rules need class-level data flow, such as which methods write which fields. A pattern matcher (Semgrep, PMD XPath) cannot compute that. JavaParser and Gson are already in the local Gradle cache, so the build works offline. |
| Semgrep | Not used in Phase 1 (revised from my first recommendation) | It isn't installed, Windows support is limited, and the AST engine covers every rule the Semgrep set would have plus the metrics. It can come back later as a pre-commit linter with no change to the design. |
| SonarQube | Optional consumer of the SARIF report | Keep Sonar (Community Build is free) for generic quality and import JOBOL findings as external issues. The JOBOL gate does not depend on a Sonar server. |
| ArchUnit | Phase 1b template (§9) | Rule JOB001 already enforces the main architecture boundary at source level. ArchUnit adds a bytecode-level check inside each estate's own test suite. |
| Lanes | Path-glob lane policy (modern / compat / faithful / test) | The estate *intentionally* contains faithful COBOL-shaped code (`Prog_*`, `State_*`, `rt`) and fixed-width codecs. Scoring them would drown the signal. Only the modern lane is held to the full rules. |
| Determinism | No clock or randomness in the report. Inputs and output are sorted. The ruleset is hash-pinned. | Reports can be diffed between pipeline iterations. A baseline comparison is meaningful only when `rulesetHash` matches. |
| Model use | None in the auditor | It is a gate the generator cannot talk its way past. LLM judgement belongs in Phase 2 and 3, *on top of* the deterministic report. |

## 3. Architecture

```text
generated estate (Gradle project)            jobol.json (lanes, overrides, gate)
          |                                              |
          v                                              v
  walk *.java (skip build/, bin/, ...) --> lane by glob --> JavaParser AST (Java 21)
                                                             |
                                    Analyzer: 27 rules + metrics, per type declaration
                                                             |
                                Scorer: JOBOL index 0..100 per type, grade A..F
                                                             |
                Auditor: LOC-weighted estate index, lanes, gate, optional baseline delta
                                                             |
          +-------------------------+------------------------+---------------------------+
          v                         v                        v                           v
  jobol-report.json         jobol-report.md          jobol-report.sarif       exit code 0 / 1 / 2
  (pipeline + LLMs)         (people)                 (SonarQube, VS Code,     (CI / pipeline gate)
                                                      GitHub code scanning)
```

### 3.1 Lanes

The first glob that matches wins. Anything unmatched is **modern**. Defaults (`src/main/resources/default-jobol.json`, printable with `--print-config`):

| Lane | Default globs | Rules applied |
|---|---|---|
| test | `**/src/test/**` | none |
| faithful | `**/com/mlcpu/rt/**`, `**/legacy/**`, `**/transpile/**`, `**/characterization/**`, `**/replay/**`, `Prog_*`, `State_*`, `Baseline_*` | none (inventory only) |
| compat | `**/record/**`, `**/batch/platform/**`, `**/strangler/**`, `**/mapping/**`, `**/copybook/**`, `*Codec.java`, `**/codec/**`, `**/compat/**` | safety rules only: JOB022, JOB041, JOB052 |
| modern | everything else | all rules |

`modernLanePct` reports how much of the audited code sits in the modern lane. An estate can have a clean index and still be 96% faithful code behind thin facades. CardDemo today is exactly that case (§6).

### 3.2 Rule catalog

Each finding carries the rule, severity, file, line, owning type, a message, and the rule's fix. Run `jobol-audit --rules` for the catalog text.

| Rule | Name | Sev. | Detects (deterministically) |
|---|---|---|---|
| JOB001 | legacy-runtime-coupling | BLOCKER | imports of `com.mlcpu.rt`; uses of `CobolBase`, `CobField`, `Area`, `CobRt`, `@CobolProgram`, `ProgramState`, `Prog_*`/`State_*`/`Baseline_*` in modern code |
| JOB002 | perform-dispatch | BLOCKER | `perform(n, m)`-style calls; integer `switch` dispatching ≥ 80% no-arg paragraph calls |
| JOB003 | paragraph-names | MAJOR | `p0_MAINLINE`, `para1000`, `x2100ReadFile`, `read_2000` |
| JOB010 | cobol-prefix-names | MAJOR | `ws`/`dw`/`ls`/`lk`/`fd`/`wk`/`sw`/`ds`... storage prefixes on fields, locals, parameters |
| JOB011 | cobol-case-names | MAJOR | `acct_id`, `ACCT_ID` (non-static), `_se8`, `t20_`, method names with `_` |
| JOB012 | numbered-series | MAJOR | field series (`dwNum1..dwNum23`, `a24/a31`), `_2` disambiguation suffixes, local series (`flag, flag2, flag3`; `money..money6`) |
| JOB013 | filler-fields | MINOR | fields named `filler*` |
| JOB020 | working-storage-class | MAJOR / BLOCKER | > 8 (> 20 = BLOCKER) mutable fields each written by ≥ 2 methods: shared scratch state |
| JOB021 | paragraph-methods | MAJOR | ≥ 4 methods, and at least half of all methods, are `void`, take no parameters and write fields |
| JOB022 | mutable-static-state | MAJOR | non-final static fields (`static Object DEFAULT_DATE_FORMATTER`) |
| JOB030 | yn-string-flags | MAJOR | `"Y"`/`"N"` compared, assigned, initialised or used as `case` labels |
| JOB031 | file-status-codes | MAJOR | `"00"`, or `"10"`/`"23"`/`"35"`... compared against a status-like operand |
| JOB032 | status-flag-fields | MINOR | ≥ 2 mutable status / end-of-file fields steering control flow |
| JOB033 | string-code-sets | MAJOR | (1.1) `switch` over short string codes (`"H"`/`"P"`/`"E"`), or one condition comparing a value with ≥ 3 string literals |
| JOB040 | picture-literals | MAJOR | edit masks (`ZZZ,ZZZ,ZZ9`, `ZZ9.99`) and PICTURE clauses (`X(10)`, `S9(7)V99`) |
| JOB041 | floating-point-money | MAJOR | `double`/`float` amounts, balances, rates, interest, fees... (camelCase-token match) |
| JOB042 | positional-strings | MAJOR | `substring`/`raw`/`slice`/`subSequence` with literal offsets (also `244 + (k - 1) * 240`), `%-20s` / `%05d` fixed-width formatting (MAJOR, cap 3 since 1.1) |
| JOB043 | string-dates | MINOR | dates/timestamps typed `String` (fields, locals, parameters, record components) |
| JOB050 | priming-read | MINOR | `read(); while (...) { ...; read(); }` |
| JOB051 | trailing-return | MINOR | `return;` as the last statement of a void method |
| JOB052 | labeled-jumps | MAJOR | labeled statements (`block5:`) emulating GO TO |
| JOB053 | abend-exit | MINOR | `System.exit` outside `main` |
| JOB054 | long-method | MINOR / MAJOR | method body > 60 lines (> 150 = MAJOR) |
| JOB055 | display-io | MINOR | `System.out/err.print*` outside `main` |
| JOB056 | god-class | MAJOR | > 800 lines **with** shared state or paragraph methods, or > 2500 lines |
| JOB057 | statement-translation-comments | MINOR | comments replaying `MOVE X TO Y`, `PERFORM P`, `[PARA:1:MOVE:L181]` |
| JOB058 | repeated-size-guards | MAJOR | (1.1) `fits(x + 1, 9)`-style PIC 9(n) ON SIZE ERROR guards at ≥ 3 call sites in one type |

Rules can be disabled, re-weighted, re-capped or re-laned per estate in `jobol.json` (`"rules": {"JOB055": {"enabled": false}}`). Overrides change `rulesetHash`, so reports with different rules are never compared as if equal.

### 3.3 Scoring and gate

- **Type index**: for each rule, the heaviest occurrences up to the rule's cap are weighted (BLOCKER 30, MAJOR 8, MINOR 2), summed, and clamped to 100. Caps (1–5) stop one noisy rule from dominating. The index therefore rises with the number of *distinct* COBOL habits, which is what separates JOBOL from a merely long class.
- **Grades**: A ≤ 10, B ≤ 25, C ≤ 45, D ≤ 70, F > 70.
- **Estate index**: the LOC-weighted mean over modern-lane types.
- **Default gate**, which fails with exit 1:
  - no modern-lane code at all
  - any BLOCKER in the modern or compat lanes
  - estate index > 25
  - any modern type graded F
  - with `--baseline`, the index regressed by more than `maxRegression` (default 0)
- **Metrics per type**: methods, fields, mutable fields, scratch fields, paragraph methods, final-field %, average and maximum method lines, average parameters, value-returning %, branches.
- **Modern-idiom signals**, reported per KLOC but deliberately left out of the score: records, enums, interfaces, sealed types, Optional, streams, lambdas, method references, switch expressions, `var`, BigDecimal, java.time.

## 4. Implementation (Phase 1, delivered)

| File | Role |
|---|---|
| `tools/jobol-auditor/build.gradle.kts` | Java 21 application; javaparser-core 3.28.2, gson 2.11.0, JUnit 5.11.3 |
| `src/main/java/com/mlcpu/jobol/Main.java` | CLI, console summary, exit codes |
| `Auditor.java` | file walk, lanes, parse, aggregation, gate, baseline comparison, ruleset hash |
| `Analyzer.java` | single-pass AST walk per compilation unit; type-level field-write analysis |
| `Rules.java` | the catalog: id, severity, lanes, cap, summary, fix |
| `Scorer.java` | index and grades |
| `Config.java`, `Glob.java`, `Zone.java` | lane policy and overrides |
| `Report.java`, `TypeReport.java`, `Finding.java` | report model (JSON schema by construction) |
| `Writers.java` | JSON, Markdown, SARIF 2.1.0 |
| `jobol-audit.ps1`, `jobol-audit.sh` | runners: Gradle distribution when installed, otherwise a `javac` build from the Gradle cache (no network, no daemon). The fallback exists because this desktop session cannot open Gradle's loopback socket. |
| `src/test/...` | JUnit tests plus a JOBOL fixture and a modern fixture |

## 5. Verification

- **Unit tests: 9 of 9 pass.** They cover:
  - the JOBOL fixture hits 21 named rules and grades F/100
  - the modern fixture (records, enum, Optional, streams, constructor injection) scores 0 and grade A
  - byte-identical JSON across runs
  - a baseline compared with itself shows no change
  - SARIF result count equals the finding count
  - CLI exit codes 0, 1 and 2
  - lane globs, glob semantics, edit-mask and tokenizer edge cases

  They were run here through the JUnit Platform launcher against the `javac` build, because Gradle cannot open a loopback socket in this session. `gradle test` runs the same tests.
- **Hub tests: 5 of 5 pass** (§8.6), in throwaway temp stores.
- **Control**: the auditor's own hand-written source scores B (11.3), with 24 of 25 types graded A. The remaining type is `Analyzer`, flagged for containing the `"Y"`/`"00"` literals it detects.

## 6. Calibration on real pipeline output (2026-10-31)

| Estate | Index | Modern lane | Grades A/B/C/D/F | Gate | What it found |
|---|---|---|---|---|---|
| `ModernizedJava/fineract-xl` | 38.8 (C) | 82.7% | 63/5/1/1/3 | FAIL | See below |
| `ModernizedJava/loanamorti` | 34.7 (C) | 53.4% | 17/1/0/0/1 | FAIL | `Lcle0100Batch` 100 F, same pattern; `AnnualPaymentLoanCalculator` B (`bigDecimal2/3` locals) |
| `work/export/carddemo-batch-spring` | 25.3 (C) | **3.6%** | 65/0/10/11/2 | FAIL | See below |

**fineract-xl.** `Fsle0100Batch` and `Fsls0100Batch` score 100 (F):
- 60+ `dw*` fields, with `dwNum1..dwNum23`
- `"00"` file-status checks and `ZZZ,ZZZ,ZZ9` masks
- void paragraph methods and a priming read

The lifted `ProgressiveEMICalculator` scores 98 (F): `block5:` GO-TO labels, `t20_`, `flag/flag2/flag3`.

**CardDemo.** 50 BLOCKERs. Each `*Service` delegates to `Prog_*`, and `JobRunner` and `JobDefinition` run the faithful `JobEngine`. The modern workflows are 16-line facades.

Every flagged class was checked by hand. The lifted Fineract classes are pipeline output (their Javadoc cites `legacy PEC:182-192`), not upstream Fineract, so the findings are true positives. Calibration produced three rule changes:
- JOB056 now requires shared state.
- Static `UPPER_SNAKE` fields are left to JOB022.
- Local numbered series and JOB043 were added.

Not deterministically detectable, so left to the Phase 3 judge:
- COBOL 30-character name truncation (`disbursementDueDat`, `accountExpiraionDate`)
- whether a class is a *thin facade over faithful code* beyond the import check
- whether names describe the business meaning

## 7. Using it in the pipeline (Phase 1, available now)

```bash
tools/jobol-auditor/jobol-audit.sh <estate> --out work/jobol/<estate>/iter-N --baseline work/jobol/<estate>/iter-N-1/jobol-report.json
```

The generation loop (GPT Sol 6.1 generates, Opus checks):

1. Generate or modernize the estate.
2. Run the audit with the previous iteration as the baseline.
3. Gate FAIL: feed `jobol-report.json` (worst types first, each finding with its `fix`) back to the generator as the change list. The checker verifies findings were *resolved*, not suppressed, by comparing `newFindings`/`resolvedFindings`.
4. Never relax the gate or move code into a non-modern lane to pass. Lane globs are part of `rulesetHash`, so such a change shows up as "different ruleset: not comparable".

`tools/jobol-auditor/PIPELINE-INSTRUCTIONS.md` is the prompt-ready version for the generator and checker. SonarQube: set `sonar.sarifReportPaths=<out>/jobol-report.sarif` on the analysis to import the findings as external issues. VS Code: open the `.sarif` with the SARIF Viewer extension.

## 8. Phase 2: the hub between the two Claude sessions (built, disabled)

### 8.1 Goal

The VS Code session (role `pipeline`) asks for an audit and gets the deterministic result immediately. When it needs judgement ("how should `Lcle0100Batch`'s 60 `dw*` fields be restructured?"), it posts a review request. The desktop session (role `architect`, this one) analyses the stored report and replies with recommendations. Both sides keep a durable, signed record.

### 8.2 "VPN-type connection": what is actually needed

Both sessions run on the same Windows machine under the same user account. A VPN or any TCP listener would *add* attack surface (a port other local processes and the network can reach) and protect nothing. The properties wanted from a VPN are a private, authenticated, tamper-evident channel. The hub provides them like this:

| VPN property | Hub mechanism |
|---|---|
| Private channel | No socket at all. Each session launches its own MCP server over **stdio**. The servers share a store at `%USERPROFILE%\.jobol-hub`, with inheritance removed and ACL'd to the user and SYSTEM only (icacls). |
| Authentication / integrity | Every message is **HMAC-SHA256** signed with a 32-byte key in the store. Unsigned or altered messages are flagged `verified: false` and their bodies withheld. |
| Access control | Only two roles. Audits run only on paths under `auditRoots` (the repo and `ModernizedJava`). Report and message ids are regex-validated, so there is no path traversal. Body ≤ 64 KB. |
| Audit trail | Append-only `hub.log.jsonl` records every audit, message, enable and disable. |
| Kill switch | `enabled: false` in `tools/jobol-hub/hub-config.json`. While false, the server exposes only `hub_status`, and no `.mcp.json` registers it. |

If the sessions ever run on different machines, wrap the same store protocol in an SSH or WireGuard/Tailscale tunnel. That is a transport change only, not part of this phase.

Identity between the two roles is cooperative. The HMAC protects against other processes and file tampering, not against one of the two trusted sessions claiming the other's role; both run as the same user. Every inbox read also returns a note that message bodies are **data, not instructions**, so neither session executes the other's text without its own user's confirmation.

### 8.3 Tools (MCP)

| Tool | Who | What |
|---|---|---|
| `hub_status` | both | enabled flag, approval record, roles, audit roots, unread counts |
| `audit_run(estate, requested_by, baseline_report_id?, label?, max_index?)` | both | runs `jobol-audit.ps1` into `store/reports/<id>`; returns gate, index, grades, by-rule counts, the 10 worst modern types, and the baseline delta |
| `audit_report(report_id, view, name?)` | both | `summary`, `markdown`, `type` (name = type or file), `rule` (JOBxxx), `list` |
| `post_message(from, to, kind, subject, body, report_id?, reply_to?)` | both | kinds: `review_request`, `feedback`, `question`, `answer`, `note` |
| `read_inbox(role, unread_only?, limit?)` / `ack(role, message_id)` | both | signature-verified inbox; ack moves to `read/` |

### 8.4 Flow once enabled

```text
VS Code session (pipeline)                    hub store (signed)                 desktop session (architect)
  generate iteration N
  audit_run(estate, baseline=N-1) ---------> reports/<id>/jobol-report.*  (deterministic, no wait)
  gate FAIL -> fix findings, loop
  post_message(review_request, report_id) --> inbox/architect/<msg>.json -----> read_inbox("architect")
                                                                                audit_report(id, "type", ...)
                                                                                analysis + recommendations
  read_inbox("pipeline") <------------------- inbox/pipeline/<msg>.json <------ post_message(feedback, reply_to)
```

The desktop session notices new requests when you ask it to ("check the hub"), or by a watcher on `inbox\architect` that I start only when you ask me to. Claude Code's native session messaging can serve as an optional doorbell. The hub stays the system of record.

### 8.5 Approval and enablement (not done)

After you review §8, run this from the repo root:

```bash
python tools/jobol-hub/jobol_hub.py enable --approve --by "Tom"
```

That command:
1. creates the user-only store and key
2. sets `enabled: true` with your name and time
3. adds `jobol-hub` to the repo's `.mcp.json`

Each Claude Code session then asks you to approve the project MCP server the first time it loads, which is a second, per-session approval. To undo:

```bash
python tools/jobol-hub/jobol_hub.py disable
```

Add `--purge` to also delete messages, reports and the key.

### 8.6 Verified so far

Five tests run against temp stores only:
- a disabled server exposes only `hub_status` and creates no store
- signed round trip, reply and ack
- a tampered message is withheld
- `audit_run` and its baseline with the real auditor; audit-root and report-id traversal guards
- `enable` refuses without `--approve`, and `disable` removes registration and store

Enabled on 2026-10-31 after approval in chat; `hub-config.json` records it.

**Store location.** The store must not live under `%LOCALAPPDATA%`. The Claude desktop app is a packaged (MSIX) Windows app, so Windows silently redirects its writes there into `AppData\Local\Packages\Claude_<id>\LocalCache`. The first store, created from the desktop session, was therefore invisible to the VS Code session, which reported "signing key missing".

The store now lives at `%USERPROFILE%\.jobol-hub`, which is not redirected. `enable` refuses to run when the hub is already enabled but the key is not visible, so a session that cannot see the store can never create a second one. Inbox reads and acks are now logged.

## 9. Ruleset history

| Version | Ruleset hash (default config) | Change | Why |
|---|---|---|---|
| 1.0.0 | `5b4a57962c17fb8b` | Initial 25 rules, calibrated on three estates | §6 |
| 1.1.0 | `0018a27e11dbcfd2` | JOB033 and JOB058 added; JOB042 widened to `raw`/`slice` offsets and raised to MAJOR | See below |

**Why 1.1.** In the first architect review over the hub, loanamorti iteration 4 restructured `Lcle0100Batch` from 100 to 0 under 1.0. Reading the code showed COBOL data representation that 1.0 could not see:
- 21 byte-offset `event.raw(13, 1)` calls
- `"H"`/`"P"`/`"E"` and `"S"`/`"R"` record-type and status strings switched on in place of enums
- a 7-way rounding-mode `equals` chain
- the PIC 9(9) overflow guard copied 8 times

**Calibration.** Caps are 3 for JOB033 and JOB042, so data-representation smells alone grade a class D, never F. F stays reserved for structurally COBOL code. Results under 1.1:

| Estate | `Lcle0100Batch` | Estate index |
|---|---|---|
| original loanamorti | 100 (F) | 40.1 (C) |
| iteration 4 | 56 (D) | 22.2 (B), gate still passes |

**Comparability.** 1.0 and 1.1 reports are not comparable: their ruleset hashes differ. Iteration 4 was re-audited under 1.1 as the new baseline.

## 10. Decisions log

| Date | Decision | By | Basis |
|---|---|---|---|
| 2026-10-31 | Phase 2 hub enabled | Tomrodgers (chat) | §8 reviewed |
| 2026-10-31 | Ruleset 1.1.0 adopted; 1.0 baselines superseded | Architect | §9 |
| 2026-10-31 | **SYSOUT exclusion v2 approved** for GN4 parity: only the J3S END statistics block, plus trailing libcob implicit-CLOSE warnings. sha256 `faf2fc2c…f958`. Any change is a new version needing approval. | Tomrodgers (chat), architect recommendation | Exact allowlist, guard for FSEM0100 error/abend lines, position-scoped, per-case counts asserted, negative test |
| 2026-10-31 | **Decimal capacity limit: preserve legacy abend 37** at the J3S decimal sites (lift-track item 0). Corrected 21:35 from the pipeline's measurement: this is not ARITH(EXTEND)'s 31 digits but FSJBDEC's big-value capacity (at most 2223 base-1e9 limbs stored, 2225 per register). The loanamorti boundary is term 1331 (computes) / 1332 (abends at APLC:225). Logged as a known legacy limitation for forward engineering; computing or rejecting such inputs is a separate business decision. | Tomrodgers (chat), architect recommendation | Negative test `fsem0100-abend-3500-months`: legacy abends at APLC:225, modern completed with RC 0 |

## 11. Roadmap

- **Phase 1b.** An ArchUnit test template for estates: modern packages may not depend on `com.mlcpu.rt` or `..legacy..`. ArchUnit is already in the Gradle cache. Also a Gradle `jobolAudit` task in generated estates, so `gradle check` runs the gate.
- **Phase 3: LLM rubric judge**, fed the deterministic report and never replacing it. Score 1–5 per worst-N class on business naming, cohesion, immutability, testability and truncated names. It must cite JOB findings, run at a fixed rubric version, and is kept advisory to the gate.
- **Trend dashboard**: index per iteration and estate from stored reports, once the hub is enabled.
