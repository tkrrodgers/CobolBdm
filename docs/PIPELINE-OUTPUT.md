# Pipeline output for Gemma 4: simpler material (JavaToCOBOLToJava.md section 5, status log 2026-10-11)

Branch `frontier-burndown` (worktree `CobolBdm-burndown`), not committed: the owner reviews.

**Status.** Phase A (items 1-4) done: kit v6 = arm B of the A/B. Phase B done for the whole estate: the typed reference of
all 16 subprograms passes its unchanged acceptance test (71/71 each), and the implementation step uses it as the model's
starting point when switched on (arm C). Gates: gradle 14/0, conformance 259/0, builds clean.

**Why.** Gemma 4 (31B, 131,072-token context) gets an implementation prompt per capability. FNEM0700's was 274,675
characters, 128,245 tokens: the packet JSON (81K characters), worked examples (72K) that print every packed and binary item
as escaped bytes (`LM-RP-EMI(1) «\x00\x00\x00\x00\x00\x00\x00\x00\f»` is packed 0.0000, about 20 tokens), the program's
statements twice (the packet's `rules[].statements[].cobol` and the AST outline) and the 262 KB model's layout twice (packet
and record kit). FNLS0200 stuck at 69/71 because its feedback showed only the REJECTS byte where the runs part (loan 54),
not the CALL whose results differ. What worked was verified, value-level facts (the probe's equivalences and callee
contracts: FNDKDFO 63 → 71/71, FNMY0100 4 → 71/71). The problem is the material, not the model.

**The A/B.** Every change is a new kit output, announced by `normalization/kit-features.json`; a kit without that file
(the frozen `publish-v5`) is rendered exactly as before, so old kit + this pipeline = the prompts and feedback Gemma had
(checked: the FNLS0200 prompt this pipeline assembles from publish-v5 is identical to the one Gemma received at 19:17, but
for CR/LF inside the `# Task` text, which the main tree's CRLF checkout of `ImplementStep.cs` puts there). Requirements
packets and their hashes, record kits, designs, tests and every other published file are unchanged. `MLCPU_KIT_FEATURES`
(`none`, `all`, or a list) overrides the file for experiments.

The new kit for the A/B: `work/pipeline-output/kit-v6/fineract-cobol-normalized-spring` (gitignored work folder), made from
the frozen kit without a republish:

```
dotnet MlCpu/src/MlCpu.DesignModel/bin/Debug/net10.0/MlCpu.DesignModel.dll kit-upgrade \
  --from <main>/projects/fineract-cobol/publish-v5/fineract-cobol-normalized-spring \
  --traces <main>/projects/fineract-cobol/work/calltrace --out <dir>/fineract-cobol-normalized-spring [--budget 70000]
```

It copies the project, renders every subprogram's `examples.md` again from the publish's own CALL traces with the code the
publish now uses, and writes `kit-features.json` (`examples-values`, `prompt-packet-compact`, `shadow-feedback`). A new
publish produces the same files (checked below).

## 1. Worked examples in values (kit feature `examples-values`)

`MlCpu.Kit.CallExamples` / `KitValues` / `RecordView` (`MlCpu.CobolKb.Knowledge/Examples/`, compiled into the Knowledge
library for the publish and linked as source into `MlCpu.DesignModel`, so examples and feedback share one value form):

- Every argument is decoded with the record kit's layouts (`record-layouts.json`): packed, binary/COMP-5 and zoned items as
  numbers with their PICTURE's scale (`LT-PRINCIPAL = 1000.0000`), text in quotes without trailing spaces, `(spaces)`, `x'00'`
  for a text, packed or zoned item never written (a binary item of x'00' bytes is 0); raw `«…»` bytes only for text with
  non-printable bytes or a number whose bytes are not valid in its USAGE. Dates stay numbers: a day number is shown as a
  date only where that is provable, and the examples do not prove it.
- Items still at their initial value (all spaces or all x'00') are left out of an argument's listing.
- OCCURS tables become rows of the occurrences in use (`LM-RP(1..6): 6 occurrence(s) in use; columns are LM-RP-<name>`),
  nested tables as `LM-IP(k,i)` rows; a column with one value in every row is given once; at most 12 rows (first 10, last 2).
  This also fixes a silent truncation of the old renderer, which expanded OCCURS into at most 2,000 leaves
  (`ReplayPlanBuilder.Leaves`), so LM-MODEL's later tables (LM-IP, 480 × 4 occurrences) never appeared.
- After the CALL, only what changed, `before → after`; a column that changes the same way in several occurrences once
  (`LM-RP-RF-PLUS1(1..6): 0.000000000000 → 1.000000000000`), many changed cells as a table of `before → after` cells.
- An argument that repeats the CALL shown before it in the same scenario is referred back to, exactly: `as CALL 16 left it`,
  `as in CALL 16`, or either `, except ITEM = value` for a few plain items.
- **Selection**: the CALLs that show a behaviour not shown yet come first, from every scenario (a greedy cover over: the
  values of selector items passed in, i.e. items with 88-level names, short text and small binary codes that vary over the
  traces; the codes left behind; the arguments and table columns changed), then further CALLs one scenario at a time, until
  the budget (unchanged: 70,000 characters). The old renderer gave each scenario an equal share in scenario order, so
  FNLS0200's examples covered 8 of 31 scenarios and never the `rejects` scenario; the new ones show 25 scenarios, including
  loan 34 (`RQ-FREQ-TYPE = 'MONTHLY'`, rejected) and loan 54 (`RQ-FREQ-TYPE = 'INVALID'`, accepted with LT-FREQ-TYPE 5),
  the exact behaviour Gemma got wrong.
- `ImplementStep.FitExamples` cuts examples in values by CALL inside a scenario as well as by scenario (the old form, by
  scenario only, is unchanged): FNEM0700's first scenario alone no longer pushes all examples out of the prompt.
- Step capabilities (FNLS0100, FNRP0100): their records are DISPLAY data (7 and 180 escaped bytes in all); the publish now
  shows packed and binary items of their records as numbers too, and `kit-upgrade` keeps their examples as published.
- Publish: `CapabilityKitBuilder.CallExamplesAsync` traces as before (every scenario for linkage up to 4 KB, which the probe
  needs; the first 10 scenarios otherwise) and renders with `CallExamples`; the layouts come from `RecordKitBuilder` and the
  88-level names from the linkage items (`KitExamples.Conditions`). Checked: `kit-harness examples` (the publish's inputs,
  built from the COBOL) and `kit-upgrade` (the published `record-layouts.json` and packet) give byte-identical
  `examples.md` for all 16 subprograms.

Samples: `docs/pipeline-output/FNLS0200.examples.v6.md`, `FNEM0700.examples.v6.md`.

**New conformance cases (suite `KitValues`, 15 checks).** `corpus/kitvalues/KITVAL01.cbl` DISPLAYs the items of a record
(packed S9(13)V9(4) positive, negative and zero, S9(3)V9(12), S9(31), unsigned packed, binary S9(4) negative, S9(9),
COMP-5 9(4) at 65535, signed zoned with negative and positive overpunch, 9(8), text, an 88-level code) and writes the record;
every item decoded from the written bytes by `KitValues` must equal what GnuCOBOL DISPLAYs. 15/15 pass.

## 2. One copy of the logic (kit feature `prompt-packet-compact`)

`PromptPacket.Compact` renders the prompt's copy of the packet (the file is untouched):
- statements by step id only (`"2000-FREQUENCY-TYPE:1:EVALUATE:L73"`) in `rules[]` and in the paragraphs' segment trees; rule
  ids, kinds, `when` conditions, reads and writes stay; segments keep ids, kinds and conditions, without the reads and
  writes each repeats from its children (the rules list them once) and without the repeated paragraph name;
- a `dataContracts` record that the record kit lists with every one of its fields and 88-level names is named with its kit
  class (`"fields": "59 fields: see record kit class LmModel"`) instead of repeated field by field.

The AST outline is then part of the core prompt (it carries the statements) and comes before the examples; if even that
does not fit the context, the full packet is used as before. A note under the packet heading says how to read it.

## 3. CALL-level shadow feedback (kit feature `shadow-feedback`)

When a subprogram capability fails acceptance, the difference probe's Gradle run also runs a shadow test
(`MlcpuDiffProbe.shadow()`, `Probe.cs`): each failing scenario once more, with every CALL of the target answered by the
faithful program on the caller's arguments and by the model's program on a copy (`ProgramRegistry.callFaithful`, as
CallTrace does); the run goes on with the faithful results, so every CALL has the faithful run's inputs. The first CALL of
each kind of difference (which bytes differ, how the CALL ended) is kept in full, up to 12. `ShadowFeedback` renders, at
the top of the feedback: per scenario the CALLs made and how many differ; the kinds of difference (the items that differ)
with their CALLs; for the first CALL of each kind what was passed in that matters (selectors: 88-level items and short
codes; small input records in full), every result item that differs with its value before the CALL, the faithful value and
the model's, what else the faithful CALL changed (that the model matched), and the packet rules that write those items.
Values are in the worked examples' form. Diagnostics verb: `MlCpu.DesignModel shadow --program PID --manifest m --project
dir --packet requirements.json --java dir --out dir` (the feedback an implementation run gives for that code).

**On Gemma's current FNLS0200 code** (`gemma/FNLS0200/java`, iteration 2, against the faithful FNLS0200 in the new kit;
full text `docs/pipeline-output/FNLS0200.shadow-feedback.txt`):

```
## CALL-level comparison: where your FNLS0200 leaves different results
Each CALL of the failing scenarios below was answered twice, by the faithful FNLS0200 and by yours, each on its own copy of the
caller's arguments; the run went on with the faithful results, so every CALL had the faithful run's inputs.
- 'oracle scenario rejects-p12-half_even': 26 CALL(s), 4 with different results (first: CALL 1).
Your results differ in 2 different ways (the items that differ); the first CALL of each is shown:
- LT-FREQ-TYPE: CALL 1 of 'oracle scenario rejects-p12-half_even'
- LT-SUBMITTED-DAY, LT-EXP-DISB-DAY, LT-SEED-DAY, LT-NUM-REPAYMENTS, ... : CALL 21 of 'oracle scenario rejects-p12-half_even'
...
### CALL 21 of 'oracle scenario rejects-p12-half_even'
Passed in (the items that select what the CALL does; small input records in full):
- FN-LOAN-REQUEST: RQ-REC-TYPE = 'LR', RQ-LOAN-ID = 54, ..., RQ-FREQ-TYPE = 'INVALID', ...
...
Results that differ (the value before the CALL; the faithful program's; yours):
- FN-LOAN-TERMS:
  LT-SUBMITTED-DAY: before 154498; faithful 154498; yours 0
  ... (13 more: the builder fields the faithful program sets after an 'INVALID' frequency)
  (the faithful CALL also changed LT-FREQ-TYPE (4 → 5), and yours did the same)
- FN-STATUS:
  ST-RC: before 0; faithful 0; yours 4
  ST-REJECT-CODE: before (spaces); faithful (spaces); yours 'RJFREQ01'
  ST-REJECT-SRC: before (spaces); faithful (spaces); yours 'LAT:592'
Rules of FNLS0200 that write these items (requirements packet; the AST outline has their statements):
- R-FNLS0200-004 in 2000-FREQUENCY-TYPE: EVALUATE RQ-FREQ-TYPE WHEN 'DAYS' (writes ST-RC, ST-REJECT-CODE, ST-REJECT-SRC)
...
```

It also found a second, silent defect the output comparison can never show: on an unknown frequency (`'MONTHLY'`, loan 34,
CALL 1) Gemma's code sets LT-FREQ-TYPE to 5 before rejecting, where the faithful program leaves it 0. The caller ignores the
field on a reject, so no output byte differs; grouping by kind of difference keeps it from hiding the 'INVALID' CALL.

## 4. Measurements without the GPU

`MlCpu.DesignModel prompt-tokens --old <publish-v5> --new <kit-v6> --designs <main>/projects/fineract-cobol/gemma --out dir
[--ctx N]` assembles every capability's implementation prompt exactly as an implementation run does
(`ImplementStep.PlanPrompt`, now shared by the run and the tool: reply budget, examples fitted, outline) with Gemma's
designs, for both kits, and counts tokens per section.

**Tokenizer: exact, not estimated.** No llama-server was running on :8190; llama.cpp's `llama-tokenize`
(`C:\models\llama.cpp-unsloth-b11539`, the server's build) loads only the vocabulary of the Gemma GGUF (about 2 s, no GPU)
and with `--no-escape --no-bos` counts like the server's `/tokenize`. Checked against the server: FNDKRND's recorded prompt
76,866 tokens + 125 (system prompt and chat template) = the 76,991 the server reported. (Without `--no-escape` the tool
turns the prompt's `\x00` texts into real bytes and undercounts by up to a third.) Gemma's tokenizer spends one token per
digit, so a decoded number is not free either: `1000.0000` is 9 tokens, against about 20 for its packed bytes.

**Uncut material** (`--ctx 4000000`: nothing left out), tokens, all 18 programs:

| Section | old kit | new kit | |
|---|---:|---:|---|
| packet | 391,008 | 215,608 | −45% |
| worked examples | 900,887 | 827,388 | −8%, with 1,550 CALLs shown instead of 1,015 |
| AST outline | 147,584 | 140,368 | (LF instead of CRLF line ends) |
| record kit, design, platform API, test, contracts, notes, task | 470,586 | 470,586 | unchanged |
| **prompt** | **1,910,065** | **1,653,950** | **−13%** |

Tokens per CALL shown in the examples: FNLS0200 1,092 → 563, FNEM0700 3,434 → 2,311, FNDKRND 184 → 99, FNMY0100 653 → 296.

| Program | old | new | | Program | old | new | |
|---|---:|---:|---:|---|---:|---:|---:|
| FNDG0100 | 92,289 | 81,159 | −12% | FNEM0600 | 104,392 | 94,737 | −9% |
| FNDKDFO | 96,455 | 78,584 | −19% | FNEM0700 | 128,880 | 107,244 | −17% |
| FNDKRND | 77,532 | 66,870 | −14% | FNEM0800 | 100,880 | 91,309 | −9% |
| FNDTKRN | 74,753 | 68,894 | −8% | FNEM0900 | 124,396 | 104,723 | −16% |
| FNEM0100 | 97,682 | 85,944 | −12% | FNLS0100 | 150,829 | 130,085 | −14% |
| FNEM0200 | 103,051 | 91,486 | −11% | FNLS0200 | 85,859 | 77,733 | −9% |
| FNEM0300 | 150,310 | 119,096 | −21% | FNLS0300 | 107,418 | 93,453 | −13% |
| FNEM0400 | 126,767 | 106,870 | −16% | FNMY0100 | 96,992 | 83,604 | −14% |
| FNEM0500 | 113,073 | 98,756 | −13% | FNRP0100 | 78,507 | 73,403 | −7% |

**As an implementation run assembles them** (131,072 context, 49,152-token reply): every prompt fills the room the reply
leaves (about 80,000 tokens), in both kits, so totals stay level (1,388,720 → 1,381,962); what changes is what fills it.
The AST outline is in **18 of 18** prompts (old kit: 4 of 18, because the full packet and the examples left no room),
the packets take 215,608 tokens instead of 391,008, the examples show **1,451 CALLs instead of 871**, FNEM0300's prompt
has examples at all (0 → 3 CALLs) and its reply budget is back to 49,152 (was lowered to 46,712). Full tables, every
section and characters per token: `docs/pipeline-output/prompt-tokens-uncut.md`, `prompt-tokens-as-assembled.md`.

Possible next reductions, measured but not changed: the platform API (8,292 tokens in every prompt, the complete source of
`com.mlcpu.platform`) is mostly irrelevant to a subprogram; the examples budget (70,000 characters) could be stated in tokens.

## Gates (Phase A)

- java-sidecar `gradle test`: 14 tests, 0 failures (unchanged code).
- Conformance gate on my :8089 sidecar (`MLCPU_JAVA_SIDECAR=http://127.0.0.1:8089`): **259 pass, 0 fail** (244 + 15 KitValues).
- `MlCpu.Host`, `MlCpu.DesignModel`, `MlCpu.CobolKb.Knowledge` build; no new warnings (the two, CS8619 in
  `EquivalenceProbeInputs.cs` and CS8714 in `GnuCobolOracle.cs`, were there before).

## Files (Phase A)

- `MlCpu/src/MlCpu.CobolKb.Knowledge/Examples/KitValues.cs`, `CallExamples.cs` (shared), `KitExamples.cs` (publish inputs)
- `MlCpu/src/MlCpu.CobolKb.Knowledge/NormalizedCapabilities.cs` (imports `kit-features.json`)
- `MlCpu/src/MlCpu.Host/Services/Workspace/CapabilityKitBuilder.cs` (examples in values, `kit-features.json`, packed/binary in step examples)
- `MlCpu/src/MlCpu.DesignModel/`: `KitFeatures.cs` (features, `kit-upgrade`), `PromptPacket.cs`, `ShadowFeedback.cs`,
  `PromptTokens.cs` (`prompt-tokens`, `LlamaTokenizer`), `ImplementStep.cs` (`PlanPrompt`, `FitExamples` by CALL, shadow run in
  the feedback, `FeedbackFor`), `Probe.cs` (the shadow test), `Program.cs` (verbs `kit-upgrade`, `shadow`, `prompt-tokens`),
  `MlCpu.DesignModel.csproj` (links the shared sources)
- `MlCpu/src/CobolConformance/`: `Suites.cs` (`KitValuesDecoding`), `Program.cs`, `corpus/kitvalues/KITVAL01.cbl`
- `docs/equivalence-probe/kit-harness/Program.cs` (verb `examples`)
- `docs/pipeline-output/` (reports and samples)

# Phase B: the typed-interface projection (kit feature `typed-reference`)

**What it is.** A deterministic rewrite of a subprogram's faithful behaviour into typed Java, judged by the capability's own
acceptance test, unchanged; when it passes, it replaces the COBOL outline as the model's starting point ("typed reference
implementation"). Code: `MlCpu/src/MlCpu.CobolKb.Knowledge/Typed/` (`TypedProjection`, `TypedRecords`, `TypedLogic`,
`VerifiedOperations`, about 2,500 lines). Inputs: the COBOL model (`CobolModelBuilder.BuildGroundedFull`), the
capability's `record-layouts.json` and record-kit sources, the probe's `equivalences.json` of its callees, and which programs'
own typed references passed. Output per program, a new kit output next to the packet (nothing published is changed):
`normalization/<PID>/typed-reference/*.java`, `gaps.txt`, `starting.txt`, `verified.txt` (the acceptance verdict) and
`normalization/<PID>/typed-reference.md` (the prompt section).

- **Records → interfaces** (`TypedRecords`). Every LINKAGE record and every WORKING-STORAGE area the program passes to a CALL
  becomes an interface of typed accessors implemented over the record kit's class (no offsets of its own): `BigDecimal` with the
  PICTURE's scale for packed and zoned items, `long` for binary, `String` for text (trailing spaces dropped, setters pad), each
  with its COBOL name and PICTURE in the Javadoc. 88-level names: an enum where an item has two or more single distinct values
  (`Status.Rc.OK`, `LoanTerms.FreqType.DAYS`, `DfParmsView.Op.MULTIPLY`), testers otherwise (`isMultiplesPresent()`). OCCURS
  tables are occurrence views (`model.rp(k).ip(j).outstBal()`). Names drop a short qualifier (`FN-LOAN-TERMS` → `LoanTerms`)
  and the common prefix of a record's items. A copy of a copybook under another prefix (COPY REPLACING, `MU-MODEL` over the
  kit class `LmModel`, whose kit layout names no accessors) takes the accessors of the same place in the record that has them.
- **Logic** (`TypedLogic`). Each paragraph is a private method whose Javadoc is the paragraph name (the packet's step ids);
  PERFORM (THRU, VARYING, UNTIL, TEST AFTER) becomes calls and loops, IF/EVALUATE `if` chains, GOBACK an unwind, CALL
  'CEE3ABD' `ModernStep.Abend`. WORKING-STORAGE is typed Java fields (`int` for signed binary up to 4 bytes, `BigDecimal` with
  scale, `String`, arrays for elementary OCCURS), numeric REDEFINES and digit groups as views of them. Every store follows the
  oracle's rules through a small `Storage` class (`decimal`/`decimalOverflow` for PICTURE truncation and ON SIZE ERROR,
  `binary`/`binaryOverflow` for binary-truncate no: size error from the field's capacity, two's-complement wrap otherwise,
  `integerOfDate`/`dateOfInteger` with GnuCOBOL's domain, `moveBytes` for group and reference-modified MOVEs). A store that
  cannot lose anything is a plain assignment. GO TO: no program of the estate uses one; like any construct the generator does
  not render, it would become a localized gap.
- **Verified CALLs** (`VerifiedOperations`). A CALL of a program whose CALL groups the equivalence probe verified becomes
  `<Callee>Operations.call(bytes...)`: dispatch on the function selector to the verified JDK operation per group (BigDecimal with
  MathContext and RoundingMode, LocalDate and ChronoUnit), with the probe's bindings, result forms (stripped, exact, day number,
  YYYYMMDD, dropped digits), exceptions and regions; only the groups for the selector codes this program can pass (`'OD', 'PC',
  'TD'` of FNMY0100 for FNLS0200). A group the probe did not verify, or a region it could not state as one relation, throws.
- **Bottom-up embedding.** A CALL of a program without verified operations whose own typed reference passed its acceptance
  test runs that reference, generated again over the caller's kit classes and nested in `<Callee>Reference` (its names cannot
  collide). The callee's own CALL areas that the caller's kit lacks (FNEM0700's `DF-PARMS` inside FNEM0500) come from the
  callee's kit, nested with a copy of the kit's `Pic` (whose helpers are package-private). The census runs bottom-up, so a
  program is generated after its callees passed.
- **No guessing.** What the generator cannot render is a gap: generated as `throw new UnsupportedOperationException("not
  projected: <step id> <COBOL>")` at that statement, listed in `gaps.txt` and in the prompt. A reference with gaps can still
  pass when no scenario reaches them (FNDKDFO: two regions of an FNDKRND group the probe left unrelated).

**The judge.** `MlCpu.DesignModel shadow --java normalization/<PID>/typed-reference` runs exactly the feedback an implementation
run gives for model code (`ImplementStep.FeedbackFor`: the contract check, Gradle compile, the capability's
`*EquivalenceTest`, 71 scenarios, unchanged), and the census (`work/pipeline-output/typed-all.sh`) writes its verdict to
`verified.txt`.

**In the implementation step** (`MlCpu.DesignModel`, kit feature `typed-reference`; `TypedReferenceKit`, `ImplementStep`). The
feature is not in kit-v6's `kit-features.json`: it is a third arm, switched on with `MLCPU_KIT_FEATURES=all` (or a list), so
the A/B of Phase A stays as it is. A typed reference is used only if its `verified.txt` says it passed (`acceptance N/N`).
Then:
- the record views, the verified operations, the embedded references and `Storage` are installed in the harness package
  before the reserved names are taken (the model cannot redefine them; the task lists them as existing names);
- the entry class and the logic are the model's starting files: they are pre-loaded into the model's file set, so the first
  answer may be SEARCH/REPLACE edits of them, and a file the model does not return stays as it is (returned unchanged, the
  starting files pass);
- the prompt carries `typed-reference.md` (the two starting files in full, the views' API one line per item, the operations'
  source, Storage's API) instead of the COBOL outline; the compact packet's note says the statements' logic is in the typed
  reference (one method per paragraph, named in its comment) and names records by their view (`FN-LOAN-TERMS (LoanTerms)`);
  the record-kit section is left out (the views replace it); the task gains the rule "start from the typed reference ...
  keep its behaviour while you bring the code to the design".

End to end without a model (`work/pipeline-output/typed-e2e.sh`: FNLS0200, Gemma's design, scripted reply of two
SEARCH/REPLACE edits renaming `principal()` to `computePrincipal()`): 10 given classes installed, starting files
`Fnls0200Logic`, `Fnls0200Program`; the edits applied; compiled; **acceptance 71/71** on the first attempt; the deliverable
holds only the two model files; the prompt has the typed reference and no AST outline.

## Phase B evidence: every subprogram's typed reference passes its acceptance test, unchanged

Census with the final generator (`work/pipeline-output/typed-all.sh`, bottom-up; `docs/pipeline-output/typed-census.txt`). Each
row is the capability's own `*EquivalenceTest` (71 oracle scenarios) run on the typed reference through
`ImplementStep.FeedbackFor`, exactly as on model code. Lines: all generated Java of the reference, embedded references
included; "logic" is the starting file the model edits.

| Program | acceptance | gaps | logic lines | all lines | verified operations used | embedded references |
|---|---|---:|---:|---:|---|---|
| FNDKRND | 71/71 | 0 | 267 | 636 | | |
| FNDKDFO | 71/71 | 2 | 382 | 1,166 | FNDKRND | |
| FNDTKRN | 71/71 | 0 | 393 | 795 | | |
| FNMY0100 | 71/71 | 0 | 454 | 2,613 | FNDKDFO, FNDKRND | |
| FNDG0100 | 71/71 | 0 | 232 | 1,844 | FNDTKRN | |
| FNEM0100 | 71/71 | 0 | 135 | 2,546 | | |
| FNEM0800 | 71/71 | 0 | 417 | 2,108 | | |
| **FNEM0700** | **71/71** | 0 | 592 | 3,828 | FNDKDFO, FNMY0100 | |
| FNEM0300 | 71/71 | 0 | 732 | 6,781 | FNDKDFO, FNDTKRN, FNMY0100 | FNEM0800 |
| **FNLS0200** | **71/71** | 0 | 211 | 2,671 | FNDTKRN, FNMY0100 | |
| FNEM0500 | 71/71 | 0 | 398 | 19,561 | | FNEM0300, FNEM0700, FNEM0800 |
| FNEM0900 | 71/71 | 0 | 472 | 28,885 | FNMY0100 | FNEM0500, FNEM0700 |
| FNEM0600 | 71/71 | 0 | 233 | 38,970 | | FNEM0300, FNEM0500, FNEM0700, FNEM0800 |
| FNEM0400 | 71/71 | 0 | 430 | 61,611 | FNDKDFO, FNMY0100 | FNEM0300, FNEM0500, FNEM0700, FNEM0800, FNEM0900 |
| FNEM0200 | 71/71 | 0 | 273 | 68,048 | | FNEM0400, FNEM0800 |
| FNLS0300 | 71/71 | 0 | 230 | 110,922 | FNMY0100 | FNEM0200, FNEM0600 |

**16 of 16 pass.** FNDKDFO's two gaps are the FNDKRND regions the probe could not state as one relation (`DK-PARM = 0`
shorten); no scenario reaches them. Determinism: generating all 16 again gives byte-identical Java for the 15 that had
passed before the last generator change (their verdicts stand); FNLS0300, the only one that changed, was tested again (71/71).
The census went 10/16 (first pass) → 12/16 → 16/16 as the generator learned what the estate needed; each fix is general:
COPY REPLACING copies of a record (`MU-`/`MC-`/`MJ-MODEL` over `LmModel`: by place), 88-level tests and SETs by value
inside OCCURS, reference-modified MOVE, EXIT PERFORM (a labeled `break`), CALL sites compared by kit class, an embedded
callee's own CALL areas from its own kit, the same copybook under another kit class (FNLS0300's `MaModel`).

**FNLS0200** (12 files; logic 211 lines): views `LoanRequest`, `CurrencyRec`, `MathContextRecord`, `LoanTerms`, `Status`,
`DtParmsView`, `MoneyRequest`; CALLs of FNDTKRN and FNMY0100 are `FndtkrnOperations` / `Fnmy0100Operations` (only the groups
of the codes FNLS0200 passes). The behaviour Gemma got wrong is now a plain line of the starting file:

```java
    /** 2000-FREQUENCY-TYPE */
    private void frequencyType() {
        if ("DAYS".equals(loanRequest.freqType())) {
            loanTerms.freqType(LoanTerms.FreqType.DAYS);
        ...
        } else if ("INVALID".equals(loanRequest.freqType())) {
            loanTerms.freqType(LoanTerms.FreqType.INVALID);
        } else {
            status.rc(Status.Rc.REJECTED);
            status.rejectCode("RJFREQ01");
            status.rejectSrc("LAT:592");
        }
    }
```

**FNEM0700** (12 files; logic 592 lines): `LmModel`'s 262 KB as occurrence views (`model.rp(k).ip(j).outstBal(sum)`), the
DF arithmetic as `FndkdfoOperations.call(dfParms.bytes())` after typed setters (`dfParms.op(DfParmsView.Op.MULTIPLY)`),
money rounding as `Fnmy0100Operations`, every COMPUTE with its size-error test (`Storage.decimalOverflow(v2, 17, 4)`).
Full prompt sections: `docs/pipeline-output/FNLS0200.typed-reference.md`, `FNEM0700.typed-reference.md`; FNLS0200's Java:
`docs/pipeline-output/typed-reference-FNLS0200/`.

**Tokens (arm C).** `prompt-tokens` with `MLCPU_KIT_FEATURES=all` (exact Gemma counts, as assembled; report
`docs/pipeline-output/prompt-tokens-typed-reference.md`). In the prompt the typed reference replaces both the AST outline and
the record-kit section:

| Program | B: outline | B: record kit | B: both | C: typed reference | | CALLs shown B → C |
|---|---:|---:|---:|---:|---:|---|
| FNDG0100 | 4,242 | 3,572 | 7,814 | 7,048 | −10% | 36 → 36 |
| FNDKDFO | 4,168 | 1,618 | 5,786 | 7,638 | +32% | 320 → 313 |
| FNDKRND | 2,792 | 1,094 | 3,886 | 4,187 | +8% | 404 → 404 |
| FNDTKRN | 4,083 | 1,238 | 5,321 | 6,303 | +18% | 421 → 421 |
| FNEM0100 | 4,437 | 6,312 | 10,749 | 6,769 | −37% | 8 → 9 |
| FNEM0200 | 7,056 | 6,987 | 14,043 | 9,138 | −35% | 9 → 10 |
| FNEM0300 | 14,013 | 8,793 | 22,806 | 20,109 | −12% | 3 → 4 |
| FNEM0400 | 9,464 | 8,638 | 18,102 | 15,403 | −15% | 5 → 6 |
| FNEM0500 | 9,959 | 6,929 | 16,888 | 12,977 | −23% | 11 → 14 |
| FNEM0600 | 7,929 | 7,032 | 14,961 | 11,254 | −25% | 13 → 15 |
| FNEM0700 | 11,450 | 7,377 | 18,827 | 16,025 | −15% | 7 → 8 |
| FNEM0800 | 6,707 | 4,854 | 11,561 | 8,292 | −28% | 16 → 17 |
| FNEM0900 | 11,088 | 6,926 | 18,014 | 16,119 | −11% | 8 → 9 |
| FNLS0200 | 5,293 | 5,113 | 10,406 | 9,230 | −11% | 68 → 68 |
| FNLS0300 | 6,772 | 8,256 | 15,028 | 11,646 | −23% | 7 → 8 |
| FNMY0100 | 7,186 | 4,392 | 11,578 | 10,712 | −7% | 115 → 117 |
| **16 subprograms** | 116,639 | 89,131 | 205,770 | **172,850** | **−16%** | 1,451 → 1,459 |

The room freed goes to the worked examples (512,398 → 550,472 tokens over the 16 prompts). Embedded references cost no
prompt tokens (the prompt names them; they are given classes). Steps (FNLS0100, FNRP0100) are unchanged.

## What generalizes, and what does not (yet)

- **Generalizes**: views over the record kit (every record of the estate; no offsets of their own), Storage's store rules,
  the verified operations (every caller of the four probed programs), and bottom-up embedding up to five levels
  (FNLS0300 → FNEM0200 → FNEM0400 → FNEM0900 → FNEM0500 → FNEM0700). The projection needs only what the pipeline already
  produces (COBOL model, record kit, probe results, acceptance test), and its output is judged by the unchanged test.
- **Size**: an embedded reference is generated again per caller (FNEM0400 holds FNEM0500 twice, directly and inside
  FNEM0900): FNLS0300's given classes are 110,922 lines. Compile and test stay under a minute; sharing one copy per program
  across callers would need the kit classes to be shared too (each capability has its own record kit package).
- **Not done**: `LocalDate` typing of record items (day numbers stay `long` as COBOL keeps them; the verified FNDTKRN
  operations use `LocalDate` and `ChronoUnit` inside, e.g. `ChronoUnit.DAYS.between(LocalDate.of(1600, 12, 31), r)`, but
  which items of the records hold day numbers is not proven by anything the pipeline has); GO TO (no program of the estate has one; it would be a
  gap); EXIT PERFORM CYCLE, PERFORM TIMES and CALL through a data item (gaps if met); a subprogram's DISPLAY goes to a
  `console` string that an abend carries (a `ModernProgram` has no console). Embedding needs the callee's own reference
  to pass first; a program with gaps that its scenarios reach does not pass and is not embedded.
- **The model's task changes**: from "re-derive the program from the outline" to "refactor a passing program into the
  design". Whether Gemma keeps 71/71 while refactoring is the A/B/C question for the GPU; shadow feedback (Phase A) runs on
  whatever it returns.

## Gates (final, after Phase B)

- java-sidecar `gradle test --rerun`: 14 tests, 0 failures.
- Conformance gate on my :8089 sidecar: **259 pass, 0 fail** (244 + 15 KitValues). Phase B adds no conformance case: its check
  is the census above (16 programs × 71 scenarios of the unchanged acceptance tests), which exercises every Storage rule
  against the faithful programs.
- `MlCpu.Host` (full rebuild), `MlCpu.DesignModel` (full rebuild), `MlCpu.CobolKb.Knowledge`: 0 errors; warnings only the two
  that were there before (CS8619 `EquivalenceProbeInputs.cs`, CS8714 `GnuCobolOracle.cs`).
- Arms A and B reproduce: both kits' prompts measured again with the final code give Phase A's token counts exactly.

## Files (Phase B)

- `MlCpu/src/MlCpu.CobolKb.Knowledge/Typed/`: `TypedProjection.cs` (build, embedding, prompt section, Storage),
  `TypedRecords.cs` (views), `TypedLogic.cs` (paragraphs → methods), `VerifiedOperations.cs` (probe groups → JDK operations)
- `MlCpu/src/MlCpu.DesignModel/`: `TypedReferenceKit.cs` (load when verified, install the given classes), `KitFeatures.cs`
  (feature `typed-reference`), `ImplementStep.cs` (given classes before reserved names, starting files pre-loaded, prompt
  with the typed reference instead of the outline, task rule), `PromptPacket.cs` (records named by view), `PromptTokens.cs`
  (section "typed reference")
- `docs/equivalence-probe/kit-harness/Program.cs` (verbs `typed`, `semtree`)
- `work/pipeline-output/typed-all.sh` (census), `typed-e2e.sh` and `typed-e2e/exchange/reply-1.md` (end to end), gitignored
- `docs/pipeline-output/`: `typed-census.txt`, `FNLS0200.typed-reference.md`, `FNEM0700.typed-reference.md`,
  `typed-reference-FNLS0200/*.java`, `prompt-tokens-typed-reference.md`

To run arm C: `kit-harness typed <kit> <cbl>...` (or `typed-all.sh`, which also writes `verified.txt`), then any
implementation run with `MLCPU_KIT_FEATURES=examples-values,prompt-packet-compact,shadow-feedback,typed-reference`.
