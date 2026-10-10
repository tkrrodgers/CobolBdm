# COBOL BDM — COBOL to Java

Implementation of `CobolToJavaProcess.md`, guided by `CobolToJavaAnswers.md`, proven end to end on
[AWS CardDemo](https://github.com/aws-samples/aws-mainframe-modernization-carddemo).

## Result

The CardDemo batch flow converted to Java produces output **byte-identical** to the original COBOL, as run by GnuCOBOL:
24 of 24 comparisons match (`work/parity/parity-report.md`).

| Step | Program | Compared |
|---|---|---|
| S1–S3 | CBACT02C, CBACT03C, CBCUS01C | SYSOUT, return code |
| S4 | CBTRN01C | SYSOUT (1,808 lines), return code |
| S5 POSTTRAN | CBTRN02C | TRANSACT, DALYREJS, ACCTDATA, TCATBAL, SYSOUT, return code 4 |
| S6 INTCALC | CBACT04C (PARM) | SYSTRAN, ACCTDATA (alternate-key reads), SYSOUT |
| S7 COMBTRAN | sort + reload | TRANSACT |
| S8 TRANREPT | sort/INCLUDE + CBTRN03C | TRANDALY, TRANREPT (519-line report), SYSOUT |

All 12 CardDemo batch programs transpile and compile. CBSTM03A has 9 frontiers: z/OS control blocks via `SET ADDRESS OF`, and `ALTER`.
CBACT01C needs the assembler routine COBDATFT, so it is not in the parity run.

The exported **Spring Boot** jobs (`<job>-spring`), driven by the harvested JCL, also match the oracle byte for byte:
11 of 11 outputs (`tools/carddemo/Invoke-SpringJobParity.ps1`).

| Job | Steps run by the generated `JobEngine` | Outputs matched |
|---|---|---|
| POSTTRAN | CBTRN02C (MAXCC 4, as on the oracle) | TRANSACT, DALYREJS, ACCTDATA, TCATBALF, SYSOUT |
| INTCALC | CBACT04C with PARM from JCL | SYSTRAN(+1), ACCTDATA, SYSOUT |
| TRANREPT | PROC REPROC → IDCAMS REPRO, SORT with SYMNAMES/INCLUDE, CBTRN03C | TRANSACT.DALY, TRANREPT, SYSOUT |

SYSOUT is compared line by line, because GnuCOBOL on Windows ends lines with CRLF and the JVM uses LF.

Projections 2–10 are applied to every exported project. The projected code still matches the oracle byte for byte: 11/11 from the
per-job projects, and 11/11 from the domain-partitioned estate project (`carddemo-batch-spring`: 10 jobs, 11 programs, 7 Gradle
modules, 141 tests). With the wall clock instead of a pinned one, `RecordCompare` driven by `replay/ReplayPlan.json` reports
`EQUIVALENT_EXCEPT_VOLATILE`: every differing byte lies in the plan's clock-derived fields (POSTTRAN TRAN-PROC-TS 262/262;
INTCALC TRAN-ORIG-TS and TRAN-PROC-TS 50/50).

Conformance gate (`dotnet run --project MlCpu/src/CobolConformance`): 131 checks, all pass. Lanes: parser acceptance, JCL,
job resolution, DD binding, replay plan, partitioning, lowering, truth tables (hand-computed expected values), GnuCOBOL
parity, and projections end to end. `PARITY03`/`LOWER05` cover WRITE ADVANCING and `PERFORM … WITH TEST AFTER`.

### A second estate: IBM Z Open Editor sample (SAM1, SAM2)

Onboarded from `zopeneditor-sample-main` with no estate-specific code: SAM1 (a sequential master-file update over a
variable-length customer file, a 132-byte print report written with `WRITE … ADVANCING`) and SAM2 (a subprogram SAM1 CALLs
through a data-name). The published estate passes 61/61 tests, and job ZDERUN matches GnuCOBOL on 6/6 outputs (CUSTOUT,
CUSTRPT, SYSOUT) over two scenarios: the sample data and `all-transactions`, an authored scenario that reaches every
SAM1/SAM2 path. It found three runtime/transpiler defects, now fixed: `WRITE … ADVANCING` was a frontier (now GnuCOBOL's
exact control bytes), `PERFORM VARYING … WITH TEST AFTER` ran one pass too few, and files left open at GOBACK were not
closed implicitly. The conformance case for them found a fourth: control characters in literals were emitted as `\u000a`
escapes, which Java turns into a line break before lexing.

## Workbench: several COBOL projects (`MlCpu.Host`)

`dotnet run --project MlCpu\src\MlCpu.Host --urls http://localhost:5100`. The header shows the active project on every page.

| Page | What it does |
|---|---|
| **Settings** | Onboard a project (git clone, or copy, into `projects/<id>/source`), select the active one, see its layout, programs (deduplicated by PROGRAM-ID; kinds batch / subprogram / online / unreferenced), datasets and jobs; build &amp; run the GnuCOBOL oracle; Gemma model and llama.cpp paths. |
| **Conversion** | *Transpile all* (progress per program, Java viewer with frontiers), then *Publish* (default `projects/<id>/publish`) the normalized Spring estate with the capabilities layer and the modernization kit, then *Build &amp; test*: `gradle test` and every job against the GnuCOBOL oracle. |
| **Graphs** | *Rebuild all graphs*: COBOL-85 AST (`graphs/ast/<PID>.ast.json` + outline), DDG (`graphs/ddg/<PID>.ddg.json`), call graph (`graphs/callgraph.json`). Cytoscape views of the estate call graph and a program's paragraph graph; the PDG of a paragraph is built in memory on click. |
| **Gemma 4 Modernization** | Enabled once the project is published. Choose N programs, *Design* (Gemma's designs rendered), *Publish design to service* (Gemma implements each design against its equivalence test; its code replaces the capability classes in a copy of the published project), *Test with GnuCOBOL*. |
| **View Gemma 4's Code** | Enabled once Gemma has written code for the active project. A VS Code–style explorer of `projects/<id>/gemma/<PID>/` (its classes under `java/`, the design, the implementation prompt, every attempt with its reasoning; each program badged with its acceptance result). Files open in tabs in a read-only Monaco editor (BlazorMonaco's bundle); each tab keeps its scroll position and cursor. |
| Jobs, Parity | As before; Parity runs the CardDemo harness for the CardDemo project. |

Every long action is a background operation with a progress bar and a live log (navigating away does not stop it); the same
actions are headless under `/api` (see Run).

| Project | Programs | GnuCOBOL oracle | Published estate: tests · job outputs vs GnuCOBOL |
|---|---|---|---|
| carddemo (profile `tools/profiles/carddemo.profile.json`) | 31 (11 batch, 2 subprogram, 17 online, 1 unreferenced) | CardDemo harness: 25 executables, 24/24 comparisons | 116/116 · 11/11 |
| zopeneditor-sample (derived, no profile) | 2 (SAM1 batch, SAM2 subprogram); SAM1LIB and the `multiroot` copies excluded as duplicate PROGRAM-IDs | generic: 1 executable, job ZDERUN, 2 scenarios | 61/61 · 6/6 |

**Gemma 4 through the workbench** (gemma-4-31B-it BF16 with its MTP draft model, llama.cpp; every design valid on the first attempt):

| Program | Kind | Acceptance (equivalence test) | Attempt | Minutes | Notes |
|---|---|---|---|---|---|
| CBTRN02C | step | 23/23 | 8 | 18.0 | one pass (the earlier CLI run in decision 16: attempt 4) |
| CBACT04C | step | 17/17 | 4 | 7.4 | one pass (earlier: attempt 7) |
| CBTRN03C | step | 23/23 | 12 | 20.4 | one pass (earlier: attempt 15) |
| SAM2 | subprogram | 13/13 | 6 | 12.2 | second pass, after the `by-reference-writes` quirk and all-scenario CALL traces; the first stalled at 11/13 |
| SAM1 | step | 13/13 | 12 | 25.1 | third pass. With the order-of-effects trace and the `test-after-overrun` quirk it reached one remaining issue (the totals lines' columns) but stayed at 0/13 for 20 attempts; the AST outline in the implementation prompt and record-level difference feedback closed it |

- The services assembled from Gemma's code pass every test and match GnuCOBOL byte for byte on every job output, run with `--mlcpu.implementation=normalized`:
  - CardDemo: 116/116 tests, 11/11 outputs over POSTTRAN, INTCALC and TRANREPT.
  - zopeneditor-sample: 61/61 tests, 6/6 outputs over ZDERUN's two scenarios. Gemma's SAM1 CALLs Gemma's SAM2 through `ctx.call`.
- Caveat: in the CardDemo runs the AST outline reached the design prompts but not the implementation prompts. A line-ending bug, since fixed, prevented it, and the corrected run records say so.
- Pass histories: `projects/zopeneditor-sample/gemma/RUNS.md`.

## Layout

```
MlCpu/                 C# (.NET 10): MlCpu.Core, MlCpu.CobolKb, MlCpu.CobolKb.Knowledge (JCL harvester, KB, DdBindingPlanner,
                       ReplayPlanBuilder, RequirementsPacketBuilder, NormalizedCapabilities), MlCpu.Cobol.Analysis (incl. Projections/),
                       MlCpu.JavaBridge (CLI), MlCpu.Host (Blazor), MlCpu.DesignModel (local models: design + implementation),
                       CobolConformance (gate + corpus/)
java-sidecar/          Java 21: com.mlcpu.sidecar (HTTP :8079, emitter, compiler) + com.mlcpu.rt (runtime, JobEngine, StepRunner, DataPrep,
                       SortReplay, RecordCompare, TruthRunner, StepEquivalence)
capabilities/carddemo/ normalized capabilities: capability-manifest.json, normalization/<PID>/ (requirements, design, design-conformance),
                       evidence/, src/ (com.mlcpu.carddemo platform/domain/data + com.mlcpu.capabilities.*)
tools/Start-Sidecar.ps1
tools/carddemo/Invoke-CardDemoParity.ps1   GnuCOBOL oracle vs generated Java (per step)
tools/carddemo/Invoke-SpringJobParity.ps1  exported Spring jobs vs the same oracle outputs (run after the above); [-Estate <dir>]
tools/Collect-CapabilityEvidence.ps1       promotion evidence for the normalized capabilities (equivalence, parity, JaCoCo, PIT)
tools/Invoke-ModelCapabilityRuns.ps1       local models design and implement each capability; judged by the equivalence tests
tools/New-WorkedExamples.ps1               normalization/<PID>/examples.md traced from the faithful program (generators in tools/examples/)
tools/Test-ModelService.ps1                a model's code for every program as one service: full test suite + jobs vs the GnuCOBOL oracle
tools/profiles/*.profile.json              overrides for a known estate, matched by detection files (CardDemo: layout, script oracle, authored capabilities)
tools/antlr/                               ANTLR 4.13.1 tool (regenerates MlCpu.Cobol.Antlr/Generated from Grammar/Cobol85.g4)
tools/Restart-Host.ps1                     stop the host on :5100 and rebuild it
MlCpu/src/MlCpu.Cobol.Antlr/               grammars-v4 COBOL-85 parser (generated C#), ProLeap-style preprocessing, AST JSON and outline
MlCpu/src/MlCpu.Host/Services/Workspace/   projects, onboarding, GnuCOBOL oracle (cached), graphs, conversion, capability kit, job parity, Gemma
capabilities/platform/                     the platform layer for generated capability kits (com.mlcpu.platform: datasets incl. variable-length
                                           and print files, field codecs incl. packed/binary) and the generic parity notes
projects/<id>/                             project.json (tracked), scenarios/ (tracked); source/, oracle/, graphs/, transpile/, capabilities/,
                                           publish/, runs/, gemma/ are generated and git-ignored
```

## Run

```powershell
powershell -ExecutionPolicy Bypass -File tools\Start-Sidecar.ps1                       # 127.0.0.1:8079
dotnet run --project MlCpu\src\MlCpu.Host --urls http://localhost:5100                 # Blazor UI + /health, /migrate/job-java/{job}
powershell -ExecutionPolicy Bypass -File tools\carddemo\Invoke-CardDemoParity.ps1      # full conversion + parity
dotnet run --project MlCpu\src\MlCpu.JavaBridge -- --file <prog.cbl> --spill-dir <dir> # one program
dotnet run --project MlCpu\src\MlCpu.JavaBridge -- --kb work\kb-site --estate ..\carddemo\app   # KB: jobs.jsonl, programs.jsonl
Invoke-RestMethod -Method Post "http://localhost:5100/migrate/job-java/TRANREPT?exportDir=$PWD\work\export"   # Spring project
Invoke-RestMethod -Method Post "http://localhost:5100/migrate/estate-java?jobs=POSTTRAN,INTCALC,TRANREPT&partition=true&exportDir=$PWD\work\export"
powershell -ExecutionPolicy Bypass -File tools\carddemo\Invoke-SpringJobParity.ps1    # run exported jobs vs oracle (+ wall-clock RecordCompare)
dotnet run --project MlCpu\src\MlCpu.JavaBridge -- --replay-plan POSTTRAN --estate ..\carddemo\app --out work\dataplan\POSTTRAN
dotnet run --project MlCpu\src\CobolConformance                                       # conformance gate (sidecar up; GnuCOBOL for parity)
cd java-sidecar; gradle test                                                            # runtime golden vectors
dotnet run --project MlCpu\src\MlCpu.JavaBridge -- --requirements POSTTRAN,INTCALC,TRANREPT --estate ..\carddemo\app --out capabilities\carddemo\normalization
Invoke-RestMethod -Method Post "http://localhost:5100/migrate/estate-java?jobs=POSTTRAN,INTCALC,TRANREPT&partition=false&name=carddemo-normalized&exportDir=$PWD\work\export"
powershell -ExecutionPolicy Bypass -File tools\Collect-CapabilityEvidence.ps1          # evidence/<PID>.evidence.json (after the export above)
powershell -ExecutionPolicy Bypass -File tools\Invoke-ModelCapabilityRuns.ps1          # local models -> ..\modelDesignsAndCodeOutput\<model>\<PID>
dotnet run --project MlCpu\src\MlCpu.DesignModel -- design --packet capabilities\carddemo\normalization\CBTRN02C\requirements.json --out work\design\CBTRN02C\design.json
powershell -ExecutionPolicy Bypass -File tools\New-WorkedExamples.ps1 -Program CBTRN03C   # worked examples (after the export above)
powershell -ExecutionPolicy Bypass -File tools\Test-ModelService.ps1 -ModelOutput ..\modelDesignsAndCodeOutput\gemma-4-31B-it-full-service
```

Workspace API (each POST starts a background operation and returns it; poll `GET /api/operations/{id}`):

```powershell
$h = "http://localhost:5100/api"
Invoke-RestMethod -Method Post "$h/projects/onboard?name=zopeneditor-sample&source=C:\Users\Tomrodgers\Source\zopeneditor-sample-main"
Invoke-RestMethod -Method Post "$h/projects/zopeneditor-sample/activate"
Invoke-RestMethod -Method Post "$h/projects/zopeneditor-sample/oracle"            # ?force=true recompiles despite the cache
Invoke-RestMethod -Method Post "$h/projects/zopeneditor-sample/graphs"
Invoke-RestMethod -Method Post "$h/projects/zopeneditor-sample/transpile"
Invoke-RestMethod -Method Post "$h/projects/zopeneditor-sample/publish"           # ?dir=<target>
Invoke-RestMethod -Method Post "$h/projects/zopeneditor-sample/test"
Invoke-RestMethod -Method Post "$h/projects/zopeneditor-sample/gemma/design?programs=SAM1,SAM2&outline=true"
Invoke-RestMethod -Method Post "$h/projects/zopeneditor-sample/gemma/publish?programs=SAM1,SAM2&attempts=15"
Invoke-RestMethod -Method Post "$h/projects/zopeneditor-sample/gemma/test"
Invoke-RestMethod "$h/operations?project=zopeneditor-sample"
dotnet run --project MlCpu\src\MlCpu.JavaBridge -- --ast <file|dir> --out <dir>     # COBOL-85 parse: <PID>.ast.json + <PID>.outline.txt
```

The active project decides the estate roots (code, copybooks, JCL, PROCs, control cards); without one, `MlCpu:CorpusRoot` in
`MlCpu.Host/appsettings.json` points at the estate (default `../carddemo/app`). `MlCpu:WorkspaceRoot` (default `projects/`),
`GemmaModel` and `LlamaDir` can be set there or in `%LOCALAPPDATA%\MlCpu\settings.json` (the Settings page writes it).

## Architecture decisions (made as SME/architect; deviations from the spec are deliberate and listed)

1. **Byte-backed storage.** Every 01/77 record and FD area is a byte `Area`; data items are `CobField` views that decode and encode by USAGE.
   - The spec's typed-field model plus `image()` is replaced, because record bytes are always authoritative (I9).
   - REDEFINES, group moves, READ INTO and file images are therefore exact by construction.
   - The `State_*`/typed-facade projections (§8, steps 3–10) are **not applied**; `projection-ledger.json` in every published project says so.
2. **Semantic tree in the IR.** C# parses the COBOL once and exports IR v1.2: the flat statements with guards (§7.1), plus a resolved semantic tree (`mlcpu.semantic-tree.v1`) with typed operands.
   - The sidecar renders from that tree and never parses COBOL (I3).
   - The spec's `mlcpu.semantic-ast.v1` and the LIR route are not reproduced.
3. **Faithful or abstain.** Anything that can't be lowered becomes a frontier comment plus `CobRt.frontier(...)`, which stops the run if reached.
   - SEARCH is abstained whole, so its bodies are never emitted unguarded (fixes D1).
4. **Reference output.** CardDemo ships inputs but no captured outputs, so the parity reference is GnuCOBOL 3.3 running the original source (§5.6.5).
   - Flags: `-std=ibm -fsign=EBCDIC`, with `COB_CURRENT_DATE` pinned to the hundredth of a second.
   - The Java side runs with `MLCPU_DISPLAY_NUMERIC=gnucobol` and `MLCPU_BINARY_TRUNCATE=no`, matching the reference's dialect.
   - The runtime's defaults stay z/OS-style (Appendix A).
5. **Utilities** (IDCAMS REPRO, DFSORT SORT/INCLUDE) are `DataPrep` and `SortReplay`. Both pipelines use the same tools, so only the converted programs differ.
6. **Host.** Per your decision, `MlCpu.Host` is Blazor Server; it also exposes the headless endpoints from §10.
7. **Program graphs** (`ProgramGraphBuilder`, §5.4.4) are built over the semantic tree:
   - a statement CFG and a paragraph call graph (perform, perform-thru, goto, alter, fallthrough), with PERFORM-context-sensitive reachability;
   - control dependences, and data dependences from reaching definitions over **byte ranges per storage area**, so group, REDEFINES and FD-record overlaps are honoured;
   - dead paragraphs, CALL sites (dynamic targets are closed by reaching definitions), and the estate call graph (`EstateCallGraph`).
   - They are exported in the IR as `graph`, by `JavaBridge --graph <dir> --estate <code root>`, and on the UI's *Graphs* page. The dataflow converges in at most 5 passes on CardDemo (the cap of 50 raises a frontier).
8. **JCL harvester** (`JclHarvester`, Knowledge project):
   - handles continuations, in-stream data, `SET` symbols and `IF/THEN/ELSE` depth (steps inside are marked conditional);
   - expands in-stream and cataloged PROCs: defaults first, then caller overrides; nested PROCs; a cycle guard; `PROCSTEP.DD` overrides and added DDs; `&SYM` substitution;
   - records each DD's direction (consumer/producer/sysout/instream), GDG generation, member, DUMMY, and DCB LRECL;
   - each step keeps its citation and invocation chain (e.g. `PROC REPROC > TRANREPT.jcl:23`).
   - `KnowledgeBase.Build` writes `kb/jobs.jsonl`, `programs.jsonl` (calls, files, jobs, upstream/downstream lineage) and `meta.json`. On CardDemo it finds 38 jobs, 101 steps and 31 programs.
9. **Paragraph partitioning** (`BulkPartitionPlanner`, §8 / Q12):
   - Each paragraph gets one effect class. Impurity wins, in the order SQL > CICS > I/O > CALL > console > time > orchestration > domain.
   - Domain paragraphs are clustered by the alias groups they write. Output is `partition-plan/<PID>.json` plus `summary.csv`.
   - Finding: CardDemo's batch leaves all mix I/O with logic, so CBTRN02C, CBACT04C and CBTRN03C have **no pure-domain paragraphs**. Extracting domain services needs paragraph splitting (projection 5), not just clustering.
10. **Characterisation tests** (§8.12):
    - `CharacterizationSeedBuilder` seeds every reachable paragraph whose PERFORM closure is free of file, SQL and CICS effects.
    - Seeds are PIC-derived (zeros, ones, boundary-high, boundary-low, smallest-unit, distinct-markers), plus up to 20 reach seeds taken from condition literals. Duplicates are removed.
    - Inputs are the closure's upward-exposed reads.
    - The generated JUnit test runs the paragraph on `Baseline_<PID>` (frozen transliteration) and `Prog_<PID>` (the refactoring target) through `runParagraph`. It asserts that storage, DISPLAY output and outcome are equal, and pins them to `characterization/<PID>.golden`.
11. **Spring project** (`JavaProjectAssembler`): `<job>-spring` uses Spring Boot 3.3.4 (non-web).
    - `JobRunner` (an ApplicationRunner, whose exit code is MAXCC) runs the generated `JobDefinition`. That definition has a typed `StepId` enum, the harvested steps and DDs, and a program map with no reflection.
    - It runs on the runtime's `JobEngine`: converted programs, SORT (SYMNAMES), IDCAMS REPRO/DELETE/DEFINE, IEBGENER and IEFBR14.
    - Datasets are files named by DSN (a GDG maps to its base name). Control-card members come from `ctl/`.
    - Settings are `mlcpu.*` properties. COND is logged but not evaluated, and steps inside JCL `IF` are skipped.
    - Several jobs can go into one project (`/migrate/estate-java`); `--mlcpu.job=<JOB>` selects the job.
12. **Data layer** (§5.5.2, §5.6.4):
    - `DdBindingPlanner` binds each step's program call closure (SELECT/ASSIGN, device prefixes stripped) to its DDs. Statuses are Bound, Dummy, NoDdInStep, UnresolvedSymbolic, NoLayout, MissingCopybook and LengthMismatch (the JCL LRECL vs the FD record length, +4 for V). Each binding records the FD layout, the direction from OPEN, and citations.
    - `ReplayPlanBuilder` adds leaf layouts per file; per output, the written shapes (WRITE FROM operand, role HDR/DTL/TLR); writers; and **volatile fields**. These come from a byte-range taint pass seeded by `FUNCTION CURRENT-DATE` and `ACCEPT FROM DATE|TIME|DAY`, carried through byte-mapped MOVEs, arithmetic, STRING/UNSTRING and WRITE FROM.
    - Each project gets `DdBindingPlan.json` and `replay/ReplayPlan.json` (`-<JOB>` suffix in estate projects). `com.mlcpu.rt.RecordCompare` reads them and writes `mlcpu.record-comparison.v1`.
13. **Projections 2–10** (`MlCpu.Cobol.Analysis/Projections`, §8, Q13) are textual rewrites with brace matching (no Java AST). Each gates itself, records verbatim skip reasons, and writes its ledger (§7.11). `projections.json` summarises them.
    - **2 Composition.** Typed entity records for the shapes each program reads INTO / writes FROM (else FD records), with byte codecs that reuse the core's exact `CobField` factories, plus round-trip tests. Also `usecases/<PID>Command|Result` and a `services/<PID>Service` that runs the faithful core and refuses unbound DDs. The gate refuses OCCURS, REDEFINES, edited and floating leaves.
    - **3 State split.** `Prog_<PID> extends State_<PID>`. Areas, views, files, `_init`, `_linkage`, `_fields` and the constants move; privates become protected.
    - **4 Shared copybooks.** A copybook record with an identical layout in two or more programs becomes `core/copybook/Storage_<CPY>_<RECORD>`. The state classes alias its views and keep their own field names; conflicting revisions get a SHA suffix. On the CardDemo estate this gives 5 shared types.
    - **5 Domain clusters.** `domain-candidates/<PID>.json` with the Q12 `CandidateHash`, and a fail-closed `DomainPromotionPolicy` manifest. A cluster approved by evidence or by `CertifiedDomainClusterCatalog` moves verbatim into an inner class; a body that mentions `this` is refused.
    - **6 Certified calculations.** A closed paragraph in `CertifiedCalculationCatalog` becomes `capabilities.<pkg>.<Cap>Calculator`: typed Input/Result records, its own storage, and the generated body verbatim, so the arithmetic is the core's. A `*DifferentialTest` compares it with `Baseline_<PID>` over the catalog vectors. A `ShadowReady` slice also runs both at run time and throws on drift. A changed semantic fingerprint or contract is refused and recorded in `calculation-projection-rejections.json`.
    - **7 Orchestration.** Candidates carry the Q12 hash (Java method names excluded). An approved, representable entry (one that touches no program storage once PERFORMs and conditions become port calls) moves to `capabilities.<pkg>.<Workflow>` over a typed `Port`; the program implements the port. The policy needs scenarios, transcript and state equality, coverage, mutation and SME approval.
    - **8 Facades.** `capabilities/<pkg>/<Workflow>` over the typed service, plus tests. Names come from `ProgramSemanticNameCatalog`: CardDemo entries written in the SME role from program headers and JCL, **awaiting human confirmation**.
    - **9 Capability packages.** Implementations move to `capabilities/<pkg>/legacy/{Prog,State,Baseline}_<PID>`; `com.mlcpu.core.Prog_<PID>` stays as the compatibility entry point.
    - **10 Domain partition** (estate projects only). Gradle modules `estate-common`, `estate-app` and `domain-<id>`, assigned by the catalog's taxonomy. Shared or cross-reached programs go to common. Cross-domain CALLs stay name-resolved at run time and are recorded as seams.
    - **Outcome on CardDemo.** 2, 3, 4, 8, 9 and 10 apply. 5, 6 and 7 fail closed: there is no promotion evidence or certified catalog entry, and every CardDemo entry paragraph moves data directly. The conformance corpus (`projection/CALCDEMO.cbl`) exercises 5, 6 and 7 applied, with fixture catalogs and evidence, and runs their generated tests.
14. **Conformance gate** (§11.1, `MlCpu/src/CobolConformance`). An executable harness that prints PASS/FAIL/BLOCKED and exits 1 on FAIL or 3 when only BLOCKED. A missing sidecar, JDK or GnuCOBOL blocks its lane; it never passes.
    - The corpus has `manifest.tsv` (accept/xfail), `lower-manifest.tsv` (mustContain / mustNotFrontier / mustFrontier), `truth-manifest.tsv` (cases `in=v;…=>expected` run through `TruthRunner` on the generated Java), `parity/` (DISPLAY output vs `cobc -std=ibm -fsign=EBCDIC`), `job-control/` (conditional, in-stream DATA/DLM, nested cataloged PROCs, and a job that produces every DD-binding status) and `projection/`.
    - It found a real defect, now fixed: `DD DATA,DLM=` was not honoured, so `//` data lines ended the in-stream block.
15. **Normalized capabilities** (`capabilities/carddemo`, `NormalizedCapabilities`). Idiomatic Java for CBTRN02C, CBACT04C and CBTRN03C, next to the faithful programs.
    - `JavaBridge --requirements` writes a hashed requirements packet per program (`mlcpu.requirements-packet.v1`). A local model designs from it (`mlcpu.capability-design.v1`). The Java implements the design on a small platform layer (`com.mlcpu.carddemo.platform`).
    - `design-conformance.json` traces every design element, rule and quirk to code and test scenarios. It also records deviations, implementation defects and SME findings.
    - `*EquivalenceTest` runs the normalized step against the faithful program in every scenario: all datasets byte for byte, SYSOUT and the return code. They are also compared with the GnuCOBOL oracle.
    - Becoming the default needs evidence and an SME approval, both pinned to the requirements and source hashes. The evidence covers equivalence, oracle parity, coverage (≥ 90% line, ≥ 80% branch) and mutation score (≥ 80%). Evidence is current (2026-10-06). No approvals exist yet, so the faithful programs stay the default.
    - Defect X-01, fixed 2026-10-06: account rewrites re-encoded every numeric field, so positive amounts stored unsigned (F-sign) came back signed. Rewrites now touch only the fields the program assigns. The F-sign scenarios guard the fix in both directions.
16. **Local models** (`MlCpu.DesignModel`, `tools/Invoke-ModelCapabilityRuns.ps1`).
    - The backend is llama.cpp `llama-server`: OpenAI-compatible API, streamed, prompt cache, memory-mapped weights, MoE experts placed by `--fit`. LM-Kit is the fallback when the server cannot start or a request fails.
    - `design` validates the design against the packet: every rule, quirk and output.
    - `implement` has the model write the whole capability on the platform layer only. A harness (the export minus the authored classes) compiles it and runs the program's equivalence test. Compiler errors and failing scenarios go back to the model. The best attempt is kept.
    - `run` does both for several programs with one model load. The output goes to `..\modelDesignsAndCodeOutput\<model>\<PID>\`, with a comparison in its `README.md`.
    - Guidance (`--with-examples`, or `--examples`/`--notes`): `normalization/<PID>/examples.md` and `normalization/parity-notes.md` go into the implementation prompt.
      - The examples are traced from the faithful program on inputs that are not acceptance scenarios. The generators check every claim against the output before writing it.
      - The parity notes are general COBOL storage rules: storage default, INITIALIZE skipping FILLER, MOVE padding, REWRITE of assigned fields only, truncation, and abends skipping CLOSE.
    - Feedback after a failing run lists the failing scenarios with the first difference per output. `MlcpuDiffProbe` then adds every differing region of every output for the first failing scenario: it reuses the acceptance test's own `scenarios()`, `step()` and setup.
    - **Result:** Gemma 4 31B (BF16, MTP draft) wrote the whole normalized service, with all three designs valid on the first attempt.
      - CBTRN02C passed 23/23 on attempt 4, CBACT04C 17/17 on attempt 7, and CBTRN03C 23/23 on attempt 15.
      - Assembled by `Test-ModelService.ps1`: 116/116 tests pass and 11/11 job outputs match the GnuCOBOL oracle, with every program step in Gemma's code.
      - Without the guidance, the best local result was 3/23 (`..\modelDesignsAndCodeOutput\README.md`).

17. **Projects.** A project is a directory under the workspace (`projects/<id>/project.json`, `mlcpu.project.v1`).
    - Onboarding clones a git repository (or URL) or copies a directory into `source/`, so the oracle, graphs and publishes never touch the origin.
    - The scan classifies members by content and extension. Programs are keyed by PROGRAM-ID: the member named like it wins, then the shallower path (zopeneditor: `COBOL/SAM1.cbl` over `multiroot/sam/SAM1.cbl` and `COBOL/SAM1LIB.cbl`, which also declares SAM1).
    - Program kinds come from the harvested JCL (a step runs it: batch) and the CALL closure, dynamic targets included (only CALLed: subprogram); EXEC CICS makes it online.
    - The oracle's jobs are the JCL jobs that run an estate batch program. Their initial datasets are the DSNs a step reads before any step writes them. Each one is shaped by the reading FD (INDEXED → ksds with the RECORD KEY offsets, RECORDING V / VARYING → variable, LINE SEQUENTIAL → line, else fixed) and filled from the data file whose name qualifiers appear in the DSN (`SAMPLE.CUSTFILE.txt` → `IBMUSER.SAMPLE.CUSTFILE`).
    - A profile (`tools/profiles/*.profile.json`, matched by detection files) overrides any of it: CardDemo keeps its proven script oracle and its authored capabilities.
    - `project.json` can be edited by hand; the host re-reads it when it changes.
18. **GnuCOBOL oracle, built once** (`GnuCobolOracle`). One executable per main program (plus a PARM driver when the step passes a PARM and the program has USING), with its CALL closure and a CEE3ABD stub, compiled with the project's cobc flags and copybook roots.
    - `oracle/bin/build-manifest.json` keys each executable by a SHA-256 of its sources, every copybook it expands, the stubs, the flags and the cobc version; a run rebuilds only what changed. The KSDS load/unload utilities are cached the same way. The CardDemo harness got the same cache and a `-Work` directory per project.
    - Jobs run in order on one data directory: COBOL steps on GnuCOBOL (INDEXED datasets loaded into the file handler before the step and unloaded after it), utilities with the job engine's semantics (IEFBR14 and compile/link steps as no-ops, IEBGENER, IDCAMS REPRO/DELETE/DEFINE, SORT through `SortReplay`). Datasets are files named by DSN, SYSOUT is `<STEPID>.SYSOUT.txt`, exactly as the Java side names them.
    - Each run keeps a snapshot per job (`jobs/<JOB>/in`, `out`) and per step (`steps/<JOB>.<STEP>/in`, `out`, `step.json`). The job snapshots become the job checks; the step snapshots become the capability tests' resources. Scenarios (`scenarios/<name>/`) override source files by name.
19. **Graphs** (`GraphService`, `MlCpu.Cobol.Antlr`).
    - The AST comes from the grammars-v4 COBOL-85 grammar (ProLeap; ANTLR 4.13.1, C# target, generated code checked in). The input is the analyser's own front end (stripping, continuations, COPY … REPLACING), adjusted to the ProLeap preprocessor's contract: comment entries tagged `*>CE`, EXEC blocks `*>EXECSQL`/`*>EXECCICS`, separator commas made spaces.
    - It parses SLL first and falls back to LL with error recovery, with a time bound. On CardDemo all 14 batch programs parse cleanly (20–1,300 ms each); the 17 online ones fail only on the IBM-supplied CICS copybooks (DFHAID, DFHBMSCA) the repository does not ship.
    - The AST JSON collapses single-child rule chains. The outline renders the data division (levels, PICTURE, USAGE, OCCURS, REDEFINES and VALUE clauses) and the procedure division (paragraphs, nested IF/ELSE, EVALUATE/WHEN, AT END, inline PERFORM), with program line numbers and literals untouched.
    - The DDG JSON is the reaching-definitions graph over byte ranges. The call graph JSON holds jobs → programs (EXEC), programs → programs (CALL) and, per program, paragraphs → paragraphs. The PDG is held in memory per program.
    - Two front-end defects surfaced and were fixed: `COPY 'NAME'` (quoted) and a COPY whose period is on the next line were not expanded.
20. **The capabilities layer for any estate** (`CapabilityKitBuilder`). CardDemo's capabilities are authored. For any other estate, publish generates them per program:
    - a placeholder entry class that runs the faithful program: `JobEngine.runFaithful` for a step, `ProgramRegistry.callFaithful` for a subprogram;
    - an equivalence test over every oracle scenario, plus record-level variations of each input (empty, first record only, reversed, duplicated), normalized vs faithful, and normalized vs the GnuCOBOL oracle with the return code;
    - worked examples decoded field by field from the oracle's step snapshots, or for a subprogram every CALL's arguments before and after (`com.mlcpu.rt.CallTrace`);
    - the AST outline, the manifest (kind, package, host step), the platform layer and the parity notes.
    - Subprograms are first-class: `ModernProgram` (`call(byte[][])`, arguments BY REFERENCE), registered as CALL targets when the job selects normalized (`JobEngine.MODERN_PROGRAMS`, `JobDefinition.modernPrograms()`), and judged with `StepEquivalence.compareCall`. A normalized step can CALL through `ctx.call(...)`.
    - The published project carries the whole kit: requirements packets, design schema, AST outlines, examples, parity notes, graphs and `MODERNIZATION.md`.
21. **Gemma in the workbench** (`GemmaService`, `MlCpu.DesignModel design-all | implement-all`).
    - One model load serves every chosen program. With Gemma's MTP draft model present, llama-server decodes speculatively (about 60 instead of 22 tokens/s).
    - The AST outline goes into the design and implementation prompts only when the prompt and a full reply still fit the context.
    - The implementation contract follows the manifest: step or subprogram, and the platform package. The diff probe uses a test's `subprograms()`.
    - Gemma's best attempt per program replaces the capability's classes in `gemma/service/`, which is then tested like the published estate with `--mlcpu.implementation=normalized`.

## Answers-document defects

| Id | Status in this implementation |
|---|---|
| D1 SEARCH bodies unguarded | fixed: SEARCH is a whole-statement frontier |
| D2 ACCEPT DATE YYYYMMDD | fixed (`CobRt.accept`) |
| D3 PIC truncated at '.' | fixed: the PICTURE is taken as a whole word |
| D4 DIVIDE REMAINDER | fixed: remainder = dividend − stored quotient × divisor |
| D5 floating insertion | fixed (`Edit.format`, golden tests) |
| D6 JUSTIFIED / BLANK WHEN ZERO | implemented in MOVE and editing |
| D7 high-order truncation | fixed: numeric stores truncate to the PICTURE |
| D8 trailing sign when de-editing | fixed (`Edit.deEdit`) |

## Not done yet

- **Spec phases not implemented:**
  - ANTLR data-division cross-check: the COBOL-85 parse now runs for every program (AST, outline), but its data division is not yet compared item by item with the analyser's layouts;
  - `SyntheticDatasetGenerator` and `MainframeBaselineCapture` (synthetic inputs, lower-region capture/replay);
  - promotion evidence producers (coverage and mutation runs), so projections 5–7 apply only from certified catalogs or supplied evidence;
  - the spec's full suite list (§11.1). The gate covers the areas above; the C# transpiler, LIR and facts suites do not apply to this architecture.
- **Reviews pending (ASK-7):** the CardDemo `ProgramSemanticNameCatalog` entries and the domain taxonomy were written in the SME role and need human confirmation; the certified catalogs are empty. The normalized capabilities have no SME approvals (`capabilities/carddemo/approvals/`), and their `design-conformance.json` records await human confirmation.
- **Graph approximations:** control dependence is structural (per block), not post-dominator based; PERFORM returns in the dataflow are context-insensitive (conservative).
- **JobEngine:** COND codes are not evaluated, GDG generations are not versioned (they map to the base name), and VSAM AIX/PATH/BLDINDEX are no-ops because alternate keys are served from the base file.
- **Online CardDemo:** the CICS programs (`CO*`) are out of scope; their EXEC CICS statements become frontiers. Their COBOL-85 parse reports the IBM CICS copybooks (DFHAID, DFHBMSCA) as missing.
- **Generic oracle limits:** JCL COND is not evaluated (as in the job engine); a CALL to a program outside the estate other than CEE3ABD fails the build; data files are matched to DSNs by name qualifiers, and a dataset without a match starts empty (noted in `project.json`). Input files are treated as text lines unless they contain no line feeds.
- **Requirements packet gap:** `RequirementsPacketBuilder` emits no VALUE clauses and omits level-01 items without subordinate fields (e.g. `TRANSACTION-HEADER-2 PIC X(133) VALUE ALL '-'`). CBTRN03C's report literals are therefore supplied outside the packet, as copybook CVTRA07Y verbatim in its `examples.md`. Fixing the builder changes every program's `requirementsHash`, so designs, conformance records and evidence must then be regenerated. The AST outline (`normalization/<PID>/ast-outline.txt`) now carries every VALUE clause beside the packet without changing its hash.
- **Abend output (unverified):** the faithful runtime buffers 64 KB per output file and the platform's `SequentialDataset` writer 8 KB. Neither closes files on an abend, so an abend after more than 8 KB of output would probably leave different partial files. No scenario covers that yet.
