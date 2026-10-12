# Knowledge base and grounded Gemma 4 — design

**Status (2026-10-11).**
- **Phase 1 is built and verified** on branch `knowledge-base` (worktree `C:\Users\Tomrodgers\Source\CobolBdm-kb`, uncommitted, for the owner's review):
  - EmbeddingGemma through ML.NET;
  - three project indexes;
  - ML.NET classifiers;
  - catalogs;
  - search;
  - grounding packets;
  - the workbench page.
- **Phase 2 is designed and waits for the owner's go.** That is the VSIX and Gemma 4 answering. The other session's Gemma 4 test holds the GPU.

Section 15 is the status log; keep it current.

## 1. Goal

The owner asked for a knowledge base over three of the Java estates CobolBdm published, so that a user can ask about a topic
and get a semantic search over the functionality and business rules of each estate. Later, Gemma 4 answers those questions,
grounded in the same knowledge base, through a VSIX that the Blazor UI posts the user's question to.

| Project | Estate | Domain | Programs |
|---|---|---|---|
| `carddemo` | AWS CardDemo | credit cards: posting, interest, statements, accounts, cards, users | 31 (11 batch, 2 subprogram, 17 online, 1 unreferenced) |
| `zopeneditor-sample` | IBM Z Open Editor sample (SAM1, SAM2) | customer master-file maintenance | 2 |
| `fineract-legacy` | Apache Fineract progressive loan schedules, lowered to legacy-style COBOL (J8) | lending: loan terms, EMI, rate factors, schedules, disbursement, money arithmetic | 21 |

`fineract-cobol` (the non-legacy Tier 1) is out of scope by the owner's choice; the harvesters work for it unchanged.

**Phase 1.**
- Harvest the pipeline outputs that Gemma 4 already uses (requirements packets, AST, call graph, designs, published Java) into labeled chunks.
- Embed them with **EmbeddingGemma 300M (ONNX, from Hugging Face) through ML.NET**.
- Classify functionality and business rules with ML.NET trainers on those embeddings, using the pipeline's labels.
- Serve semantic search from the Blazor workbench.
- Author a **financial glossary**. It labels chunks and expands queries, and in Phase 2 it teaches Gemma 4 the terminology it needs to document the financial programs.

**Phase 2 (after the owner's go).**
- The knowledge base builds a grounding packet for each question.
- A VS Code extension (VSIX) listens on a loopback port and queues the Blazor UI's asks.
- Gemma 4 (llama-server) takes each ask from the queue and answers from the grounding with citations, or documents a program.

Non-goals:
- fine-tuning Gemma 4 or EmbeddingGemma;
- GPU embedding;
- changing any pipeline output;
- running Gemma 4 before the owner says so.

## 2. Constraints that shaped the design

1. **The machine is shared.** During this work llama-server held 74 GB of VRAM for the other session's Gemma 4 test, and the J3S pools kept the CPU near 99%.
   - Embedding runs on the CPU, with 8 worker threads at below-normal priority.
   - It is incremental: a content-hash cache re-embeds only the chunks that changed. A rebuild with no changes takes 0.2 min of embedding.
   - Nothing in Phase 1 touches the GPU or llama-server.
2. **The main CobolBdm tree is busy.** It has another stream's uncommitted edits, including `MlCpu.Host/Program.cs` and `MainLayout.razor`, and host :5100 serves the Gemma run.
   - This work lives in the worktree, on a branch from `42cdc6c`.
   - A second host runs on **:5110** and reads the main tree's `projects\`.
   - The only writes are to:
     - `projects\<id>\knowledge\`, which is git-ignored by `projects/*/*`;
     - `work\kb\`, which is git-ignored.
   - Host :5100 was never restarted.
3. **Reproducible.**
   - The model is pinned to Hugging Face revision `5090578d9565bb06545b4552f76e6bc2c93e4a66`, and every file is checked against its LFS SHA-256.
   - Every index records the model, variant, revision, chunker version and the SHA-256 of each input.
   - Inputs are processed in sorted order.
   - ML.NET runs with seed 1 and single-threaded trainers.
4. **Pipeline outputs are inputs.**
   - The knowledge base never edits them.
   - Labels the pipeline computed (paragraph roles, rule ids, quirks) are ground truth.
   - Every other label carries its source (`rule`, `glossary`, `model`) and a confidence.

## 3. Inputs: what the pipeline already produces

All paths are under `projects\<id>\`, where `<estate>` is `<id>-normalized-spring`.

| Source | Path | Chunks | Labels it carries |
|---|---|---|---|
| Project manifest | `project.json` | program | program kind, jobs, callers |
| COBOL header comments | `source\…\<PID>.cbl` (before the DATA DIVISION) | program | business *Function*, *Type*, *Interface*, Fineract `SRC` citations (Java class:lines method), maintenance log |
| AST outline | `graphs\ast\<PID>.outline.txt` | paragraph | paragraph boundaries and program lines, statement verbs → `ruleKind` |
| Call graph | `graphs\callgraph.json` | program (calls, runtime services, job steps) | — |
| Requirements packet | `publish\<estate>\normalization\<PID>\requirements.json` | rule, quirk, data, io | paragraph **role** (orchestration, business-logic, io-adapter, error-handling, mixed), segment kinds, rule ids, conditions, reads/writes, quirk kinds, DD bindings, console and return-code outputs |
| Gemma 4 design | `gemma\<PID>\design.json` | design; rule and quirk descriptions joined by id | capability, summary, domain model, services, ports, open questions |
| Published Java (faithful) | `publish\<estate>\src\main\java\…\legacy\Prog_<PID>.java` | java (one per paragraph, by `// PARAGRAPH <name> (L<n>)`) | joined to its paragraph's role and rule kind |
| Published Java (normalized) | `…\capabilities\<pkg>\*.java` (placeholders skipped) | java (one per member) | — (classified by the model) |
| Transpiled Java | `transpile\java\Prog_<PID>.java` | java for programs the estate did not publish (CardDemo online) | joined by paragraph |
| Gemma 4's Java | `gemma\<PID>\java\**\*.java` | java (one per member) | — (classified by the model) |
| Worked examples | `publish\<estate>\normalization\<PID>\examples.md` | example (at most 8 per program, raw bytes elided) | — |

Measured on the three projects:
- 26 requirements packets (CardDemo 3, SAM 2, Fineract-legacy 21), with 655 rules, 696 paragraphs with roles, and 46 quirks.
- Gemma 4 designs for 5 programs.
- AST outlines for all 54 programs.

The labels the owner pointed at come from two sources:
- **Requirements**: roles, rules, quirks, data contracts.
- **AST**: paragraph structure and statement verbs.

The Java join is exact, because every faithful Java method is one COBOL paragraph. A Java method therefore inherits its paragraph's labels, and the classifiers learn what those labels look like in Java. That lets them classify Java with no COBOL twin, such as Gemma 4's classes and the normalized classes.

## 4. Architecture

```
 projects\<id>\  (pipeline outputs, read-only)
     │  Harvester (deterministic, sorted)          knowledge\glossary\financial-terms.json  (149 terms)
     ▼                                             knowledge\taxonomy.json                   (16 functions)
 KbChunk[]  ──► labels: role (pipeline) · ruleKind (AST verbs) · function (glossary, weak)
     │  EmbeddingGemma 300M fp32 ─ Microsoft.ML.Tokenizers (SentencePiece) ─ ML.NET ApplyOnnxModel (ONNX Runtime 1.31, CPU)
     ▼
 projects\<id>\knowledge\  manifest.json · chunks.jsonl (with labels) · vectors.f32 · embed-cache.fp32.bin · catalog.json/.md
 work\kb\                  classifiers\{role,ruleKind,function}.zip · classification.json/-report.md · eval\ · set.json
     │
     ├─► KbSearch: EmbeddingGemma cosine + BM25 (identifier-aware, stopwords) + glossary expansion
     │            → weighted RRF (BM25 full weight only for queries that name code) → filters → ≤ 2 hits per anchor
     │       ▲  GET /api/kb/search            ◄── Blazor /knowledge (Search · Catalog · Glossary · Classifiers · Build · Ask Gemma)
     │       ▲  CLI  kb search | eval | terms
     └─► GroundingBuilder → mlcpu.kb-grounding.v1 (cited contexts, glossary definitions, chat messages)
             ▲  POST /api/kb/ground  (the Phase 1 → Phase 2 seam; implemented, calls no model)
 Phase 2:
   Blazor "Ask Gemma" (server side) ──POST /asks──► VS Code extension "CobolBdm Ask" (127.0.0.1:5199)
                                         durable queue  pending → running → done | failed
                                         worker: /api/kb/ground ──► llama-server :8090 (Gemma 4) ──► answer + citations
   Blazor ◄──GET /asks/{id} (or SSE)──────┘          VS Code: Asks view, answers as Markdown, citations open file:line
```

| Project | Kind | Contents |
|---|---|---|
| `MlCpu.SemanticKb` | library | `KbChunk`, `KbProject`, `Text/` (COBOL header, AST outline, Java splitter, identifier terms), `Harvest/`, `Labels/` (glossary, taxonomy, rule kind, function labeler, structure features), `Embedding/` (model store, tokenizer, ML.NET embedder, cache), `Index/` (index, BM25, builder), `Search/`, `Classify/`, `Catalog/`, `Grounding/`, `Eval/`, `KbRuntime` |
| `MlCpu.SemanticKb.Cli` | console `kb` | `model`, `verify-model`, `harvest`, `build`, `search`, `eval`, `ground`, `terms`, `bench` |
| `MlCpu.Host` | Blazor (existing) | `Services/Knowledge/KnowledgeService.cs` and `KnowledgeEndpoints.cs`; page `Components/Pages/Knowledge.razor`. One nav section, two lines in `Program.cs`, one project reference |
| `tools\vscode\cobolbdm-ask` | VS Code extension (Phase 2) | listener, queue, worker, views; packaged by `New-Vsix.ps1` |

Authored assets live in `knowledge\` at the repository root and are tracked:
- `taxonomy.json`
- `glossary\financial-terms.json`
- `gold\questions.json` (42 retrieval questions)
- `gold\function-labels.tsv` (40-chunk SME sample)
- `fixtures\embeddinggemma-reference.json`, made by `tools\kb\make-embedding-fixture.py`
- `prompts\answer.md`
- `prompts\document-program.md`

## 5. Chunk model

```
KbChunk {
  id          "<project>:<program>:<kind>:<key>"          stable across rebuilds (the SME gold sample keys on it)
  project, program, kind     program | paragraph | java | rule | quirk | data | io | design | example
  anchor      "<project>/<program>/<paragraph or member>"  joins the views of one paragraph (COBOL, Java, rules)
  title       e.g. "FNEM0300 · paragraph 2000-RATE-FACTOR · Rate factors of interest periods"
  text        what is embedded, indexed and shown
  path, startLine, endLine                              citation (path relative to the project dir; COBOL program lines)
  facts       role, segments, verbs, reads, writes, ruleId, capability, origin …
  labels      { family → { value, source: pipeline|rule|glossary|model, confidence } }
  tokens, sha
}
```

| Kind | One per | Text |
|---|---|---|
| program | program | header entries, purpose, Fineract SRC citations, Gemma capability, callers, calls, runtime services, jobs, files (a subprogram says which step and caller it runs in, not the step's files), paragraph list, maintenance log |
| paragraph | non-trivial COBOL paragraph (AST outline; `-EXIT` paragraphs skipped) | its statement lines; split into parts at line boundaries over the token cap |
| java | faithful method, or normalized / Gemma member (methods one by one, nested types whole, fields together) | the code with its `// [step] COBOL` comments |
| rule | requirements rule | id, paragraph, conditions, statements with lines, reads, writes, and Gemma's description of the rule |
| quirk | legacy quirk | kind, description, evidence, and Gemma's disposition |
| data | data contract record | record, copybook, file, length, fields (name, PIC, usage, type, offset, length) |
| io | program | job step and DD bindings, ports, console and return-code outputs, output layouts and clock-derived fields |
| design | Gemma design | summary, domain model, services, ports, rules, outputs, open questions |
| example | worked example section (at most 8 per program) | the section, with raw record bytes («…») elided |

- **Size.** The cap is 1,024 tokens per chunk, counted with the embedding tokenizer. EmbeddingGemma takes up to 2,048.
- **Text clean-up.** COBOL sequence numbers (columns 1–6), columns 73+ and license boilerplate are stripped.
- **Volume (built).** 3,739 chunks and 1.48M tokens:

  | Project | Chunks | Tokens |
  |---|---|---|
  | carddemo | 1,682 | 828k |
  | zopeneditor-sample | 200 | 56k |
  | fineract-legacy | 1,857 | 602k |

## 6. Embedding: EmbeddingGemma 300M through ML.NET

| Item | Choice |
|---|---|
| Model | `onnx-community/embeddinggemma-300m-ONNX` @ `5090578d…` in `C:\models\embeddinggemma-300m-ONNX`; `kb model` downloads and verifies |
| Variant | **`fp32`** (`onnx/model.onnx`, 1.23 GB) is the default. `q8` (`model_quantized.onnx`, 309 MB) is an option. fp16 and q4f16 are excluded: the model card says EmbeddingGemma's activations do not support fp16 |
| Tokenizer | `Microsoft.ML.Tokenizers` 2.0.0 `SentencePieceTokenizer` over `tokenizer.model`, with `<bos>`/`<eos>` added as the HF post-processor does |
| Inference | ML.NET 5.0.0 `ApplyOnnxModel` (`Microsoft.ML.OnnxTransformer`) on ONNX Runtime 1.31.0, CPU. Inputs `input_ids` / `attention_mask` are variable-length Int64 rows (batch 1, no padding). Output `sentence_embedding` is 768-d and L2-normalized |
| Prompts (model card) | document `title: {title} \| text: {text}`; query `task: search result \| query: {q}` |
| Workers | 8 dedicated below-normal threads, each with its own ML.NET `PredictionEngine` over the one transformer and session, one intra-op thread each |
| Cache | `embed-cache.<variant>.bin` per project, keyed by SHA-256 of variant + prompted text; unused entries are dropped on save |

**Why fp32.** It is faster on this Threadripper and is also the full-precision model. Measured on 98 real chunks (30,802 tokens), 8 workers, CPU near 99% busy, back to back:

| Variant | Tokens/s, run 1 | Tokens/s, run 2 |
|---|---|---|
| fp32 | 6,253 | 6,346 |
| q8 | 4,385 | 4,388 |

AVX-512 fp32 kernels beat dynamic int8 quantization here. The full build embedded 1.48M tokens in 3.8 min, at 6,400–6,800 tokens/s.

**Verified.** `kb verify-model` passes for fp32 and for q8:
- token ids are identical to the Hugging Face `tokenizers` ids;
- cosine similarity with Python `onnxruntime` is ≥ 0.9999999;
- the 10 fixture texts cover English, COBOL, Java, Unicode and a 900-token input.

**How ML.NET is used, end to end:**
- `Microsoft.ML.Tokenizers` tokenizes.
- `ApplyOnnxModel` embeds.
- `SdcaMaximumEntropy` and `LightGbm` classify, with `MapValueToKey` / `MapKeyToValue`.
- `CrossValidate`, with the program as the sampling key, evaluates.
- `mlContext.Model.Save` persists the classifiers to `work\kb\classifiers\*.zip`.

## 7. Index and search

- **Dense.** Exact dot product with `TensorPrimitives` over every row. Warm search takes 27 ms; the first query takes about 0.9 s while indexes and the model load.
- **Lexical.** BM25 (k1 1.2, b 0.75) over identifier-aware terms:
  - COBOL names are kept whole and split at `-`;
  - camelCase is split;
  - English stopwords are dropped from the query.
- **Glossary expansion.** Terms recognized in a query add their aliases and code terms to the BM25 query at weight 0.4.
- **Fusion.** Weighted reciprocal rank fusion (k = 60). BM25 gets full weight only when the query names code: a program id, a COBOL name, a rule id, a camelCase or underscore identifier. For plain questions it gets **0.1**.
- **Why that weighting.** In the first evaluation, equal-weight fusion (R@5 0.750) was worse than dense alone (0.861), because BM25 matches common words in prose. The 0.1 weight comes from a sweep over 0, 0.1, 0.25 and 0.5 on the gold set, so the hybrid number in §12 is tuned on that set.
- **Filters.** By projects, program, kind and label (`role:`, `function:`, `ruleKind:`), also typed inline, e.g. `rounding program:FNMY0100 kind:rule`.
- **Grouping.** At most two hits per anchor. Each hit lists its siblings, the other views of its paragraph.

## 8. Classification: functionality and business rules (ML.NET)

| Family | Classes | Training labels | Features |
|---|---|---|---|
| `role` | orchestration, business-logic, io-adapter, error-handling, mixed | the requirements packet's paragraph role, shared by the paragraph, its Java method and its rules (pipeline, ground truth) | embedding + 16 statement-composition features (verb-group shares, RETURN-CODE and ABEND mentions, statement count) |
| `ruleKind` | rounding, date-time, calculation, aggregation, validation, lookup, formatting, io, error-handling, decision, transformation, control | derived from each paragraph's or rule's COBOL statements (`Labels/RuleKind.cs`: verbs weighted, heaviest wins) | embedding only, so the model transfers to Java without COBOL verbs |
| `function` | the 16 taxonomy classes (§9) | weak labels from the glossary: a chunk's own terms, and only where one class scores ≥ 2 and ≥ 1.5× the runner-up | embedding only |

- **Model selection.** Candidates are `SdcaMaximumEntropy` and `LightGbm`, both multiclass with class-balancing example weights.
  - The one with the better mean of pooled micro and macro accuracy is deployed.
  - LightGBM is tried for `role` only, where it won. When both were tried on the other families, SDCA won on the micro/macro mean (ruleKind SDCA 71.9/49.0 against LightGBM 72.1/45.7; function SDCA 71.8/50.7 against LightGBM 68.0/46.1), so they now train SDCA alone and the build is four minutes shorter.
- **Cross-validation.** 5-fold, grouped by **program**.
  - Micro and macro accuracy are pooled over all held-out rows; macro is the mean per-class recall.
  - ML.NET's own fold averages, which are higher, are reported alongside.
  - Baseline: zero-shot nearest class description.
- **Precedence.** Pipeline and rule labels win where they exist. Model predictions fill the rest (Gemma's and the normalized Java, CardDemo programs without packets) with their probability as confidence.
- **Program summaries.** A summary's `function` comes from its header *Function* line (weighted 2) plus its purpose and design.
- **The prior.** Adding that program prior to every chunk was measured and turned off (§12): it did not change the SME accuracy and made the labels less consistent.
- **Catalog.** `catalog.json` / `catalog.md` per project (and the *Catalog* tab) is the deliverable. Per program it gives:
  - its business functions, by share;
  - its paragraph roles and rule kinds;
  - its business rules: id, paragraph, function, kind, role, citation and Gemma's description;
  - its quirks.

  Programs without a packet list their business-rule paragraphs instead.

## 9. Financial terminology

Both files are written in the SME role and marked `reviewed: false`.

- **`knowledge\taxonomy.json`** has 16 business functions:
  - Lending: `loan-terms`, `repayment-schedule`, `emi-installment`, `interest-calculation`, `outstanding-balance`, `disbursement`, `money-rounding`, `date-calendar`.
  - Cards: `transaction-posting` (bill payment included), `account-card-maintenance`, `statement-reporting`.
  - Customer and security: `customer-maintenance`, `user-security`.
  - Cross-cutting: `validation-rejects`, `batch-control-io`, `data-exchange`.

  The draft's `authorization` and `bill-payment` were dropped: the onboarded CardDemo has no authorization programs, and bill payment is one program.
- **`knowledge\glossary\financial-terms.json`** has 149 terms. Each has aliases, code terms, a definition, domains, functions, a weight and a source.
  - Fineract: EMI, rate factor, period fraction, days in year/month, declining balance vs flat, repayment and interest periods, disbursement, tranche, down payment, moratorium, evening-out, interest recognition, and the header abbreviations (PEC, RP, IP, PLSG, LSM, PLISM, MONEY, MU).
  - CardDemo: credit and cash limits, cycle credit/debit, category balance, disclosure group, monthly interest (÷ 1200), reject codes 100–103, posting, statements.
  - SAM: master update, add/update/delete, report totals.
  - Accounting.
  - Numerics: HALF_EVEN and the other rounding modes, scale, precision, MathContext, packed and implied decimals, truncation.
  - Batch.

  The CardDemo rules were checked against the COBOL (CBTRN02C L385–417, L549–551; CBACT04C L465).
- **Matching.** Aliases and code terms match by sub-words, with a light plural stem, so `LT-ANNUAL-RATE` and `annualRate` both match `ANNUAL-RATE`, and "Credit Cards" matches "credit card".
- **Tuning tool.** `kb terms "<text>"` shows the terms a text uses, its function scores and the label it would get.
- **Uses:**
  - weak `function` labels;
  - lexical expansion;
  - the Glossary tab, with how many chunks use each term;
  - in Phase 2, the definitions of terms in the question and the contexts go into every grounding packet (up to 30), with the documentation template. This is how Gemma 4 gets the financial terminology it needs to document the Java programs.

## 10. Workbench integration (Phase 1)

- **`KnowledgeService`** (singleton) loads indexes and the embedder lazily.
- **Settings** (all optional):

  | Setting | Default |
  |---|---|
  | `MlCpu:EmbeddingModelDir` | `C:\models\embeddinggemma-300m-ONNX` |
  | `MlCpu:KbSetDir` | `<workspace>\..\work\kb` |
  | `MlCpu:KbWorkers` | 8 |
  | `MlCpu:AskBridgeUrl` | `http://127.0.0.1:5199` |

- **Endpoints:**

  | Method and path | What it does |
  |---|---|
  | `GET /api/kb/status` | indexes, models present, VSIX listener health |
  | `POST /api/kb/build?projects=…&variant=fp32&rebuild=false` | background operation (`OperationService`, project `knowledge`, kind `kb-build`) |
  | `GET /api/kb/search?q=…&projects=…&k=10&mode=hybrid&kind=…&program=…&label=function:interest-calculation` | search |
  | `GET /api/kb/chunk/{id}` | one chunk |
  | `GET /api/kb/catalog/{project}` | a project's catalog |
  | `GET /api/kb/classification` | the classifier reports |
  | `GET /api/kb/glossary?term=…` | glossary terms with usage counts |
  | `POST /api/kb/ground` with body `{ question, projects, mode: answer\|document, program?, k?, contextTokens? }` | the Phase 2 seam |

- **Page `/knowledge`**, tabs:
  - *Search*: query, projects, mode, kind and function filters; results with labels colored by source, citation and text.
  - *Catalog*: per project; click a program for its rules.
  - *Glossary*.
  - *Classifiers*: metrics, candidates, per class.
  - *Build*: operation panel and index table.
  - *Ask Gemma*: the listener's state, and a **grounding preview** that shows exactly what Gemma 4 would receive.
- **Host edits.** Only two existing files change, by a few lines each: `Program.cs` (register the service, map the API) and `MainLayout.razor` (the nav section). The main tree has uncommitted edits in both, so expect two small merge hunks.

## 11. Phase 2: grounded Gemma 4 through a VSIX (after the owner's go)

### 11.1 Flow

1. The user types an ask on the *Ask Gemma* tab.
   - Blazor Server posts `{ question, projects, mode, program? }` from the server to `http://127.0.0.1:5199/asks` and gets `{ id, position }`.
   - No browser-to-VSIX call is made, so no CORS is needed.
2. The extension writes the ask to the durable queue (`pending\<id>.json`).
3. The single worker moves it to `running\`.
4. The worker calls `POST <kb>/api/kb/ground` and gets the contexts, glossary definitions and chat messages.
5. It streams `POST <model>/v1/chat/completions` (Gemma 4 on llama-server :8090).
6. It checks that every citation `[C<n>]` refers to a context in the packet. Unknown citations are flagged and dropped.
7. It writes `done\<id>.json`: answer Markdown, citations, model, timings and the grounding id. On error it writes `failed\<id>.json`.
8. Blazor polls `GET /asks/{id}` (or uses SSE `GET /asks/{id}/events`) and renders the answer, with citations linked to sources.
9. VS Code shows the same ask in its *CobolBdm Asks* view. Opening it shows the answer as Markdown; citations open the file at the line.

### 11.2 The extension (`tools\vscode\cobolbdm-ask`)

- **Language and packaging.**
  - Plain JavaScript (CommonJS) with **no npm dependencies**. Node.js is not installed on this machine. VS Code's extension host provides Node 20+ (`http`, `fs`, `fetch`).
  - Packaged as a `.vsix` by `tools\vscode\New-Vsix.ps1`, which writes the OPC zip itself: `[Content_Types].xml`, `extension.vsixmanifest`, `extension\…`.
  - Installed with `code --install-extension`.
  - The core modules (server, queue, worker) do not import `vscode`. They are tested headless with VS Code's bundled runtime: `ELECTRON_RUN_AS_NODE=1 Code.exe test\run.js`.
- **Settings**: `cobolbdm.ask.port` (5199), `kbUrl` (`http://localhost:5100`), `modelUrl` (`http://127.0.0.1:8090`), `token` (optional bearer), `maxTokens`, `reasoning` (`low`), `autoStartModel` (false).
- **HTTP**: `GET /health`, `POST /asks`, `GET /asks?limit=`, `GET /asks/{id}`, `GET /asks/{id}/events`, `DELETE /asks/{id}`.
- **Queue**: `%LOCALAPPDATA%\CobolBdm\asks\{pending,running,done,failed}`.
  - Moves are atomic renames.
  - On activation, `running` returns to `pending`.
  - There is one worker, matching llama-server's `-np 1`.
- **Gemma 4 lifecycle.**
  - When the model is unhealthy, asks wait and the status bar says so.
  - With `autoStartModel`, the extension starts llama-server with the workbench's command line (`-c 131072`, the MTP draft model), and only when no server answers on the port.
- **Security**:
  - loopback bind only;
  - bodies capped at 64 KB;
  - optional bearer token;
  - no request can run a command or read a file outside the queue.
- **VS Code UX**:
  - an activity-bar view;
  - a status bar item;
  - commands: *Ask the knowledge base* (also from a selection), *Start/Stop listener*, *Retry*, *Open queue folder*.

### 11.3 Grounding packet and prompts (`mlcpu.kb-grounding.v1`) — implemented in Phase 1

```
{ schema, id, createdAt, question, mode: answer|document, projects, program?, filters,
  contexts: [ { cite: "C1", chunkId, project, program, kind, title, path, absolutePath, startLine, endLine, labels, text, tokens } ],
  glossary: [ { term, definition } ], programFacts?, contextTokens, limit,
  messages: [ { role: system, content }, { role: user, content } ] }
```

- **Answer mode.** Hybrid search (k 12, at most 2 per anchor), plus the rule, COBOL and Java siblings of the three best anchors, within the token budget.
- **Document mode.** Every chunk of the program, in this order: program, design, io, rules, paragraphs, quirks, data, normalized and Gemma Java, examples. Faithful Java goes in last, because it repeats the paragraphs.
- **Budget.** 24,000 context tokens by default, of Gemma 4's 131k window.
- **System prompt** (`knowledge\prompts\answer.md`). Gemma 4 acts as a financial systems analyst. It must:
  - answer only from the contexts and cite `[C<n>]` after each claim;
  - say *"The knowledge base does not cover this."* when the contexts do not support an answer;
  - use the glossary terms exactly as defined;
  - give the formula, scale and rounding or truncation of every calculation;
  - name the paragraph, lines and Java method;
  - flag legacy quirks;
  - treat `model` labels as hints.
- **Document template** (`knowledge\prompts\document-program.md`):
  - Purpose;
  - Business context;
  - Inputs and outputs;
  - Processing steps;
  - Business rules (by function);
  - Calculations;
  - Error handling and return codes;
  - Legacy quirks;
  - Glossary of terms used;
  - Open questions.
- **Measured packets:**

  | Request | Contexts | Tokens | Glossary terms |
  |---|---|---|---|
  | "Why would a daily card transaction be rejected?" (answer) | 8: worked examples for rejects 100 and 102, Gemma's validator, `1500-A-LOOKUP-XREF`, `2500-WRITE-REJECT-REC`, the reject enums | 2,633 | 30 |
  | FNEM0300 (document) | 152 (109 rules, 41 paragraphs, program, io) | 23,980 | 30 |

### 11.4 Phase 2 evaluation (run only after the go)

- **Questions.** The 42 gold questions plus 6 out-of-scope questions.
- **Measures:**
  - citation validity (100% required);
  - groundedness and correctness (blind SME rubric 1–5);
  - correct refusals;
  - latency.
- **Document mode.** CBACT04C, SAM1 and FNEM0300, reviewed by the SME.

## 12. Verification and results (Phase 1)

| Check | Result | Target |
|---|---|---|
| Model files | all 10 files match the HF LFS SHA-256 at the pinned revision | match |
| Tokenizer + embedding parity (`kb verify-model`, fp32 and q8) | ids identical, worst cosine 0.9999999 | ids identical, ≥ 0.99999 |
| Incremental rebuild | 0 of 3,739 chunks re-embedded; embedding 0.2 min; full rebuild with classifiers 1.3 min | 0 re-embedded |
| Retrieval, 42 gold questions (36 prose, 6 that name code), searched across all projects | **hybrid R@5 0.905, MRR@10 0.755**, R@1 0.667, R@10 0.952 | R@5 ≥ 0.80, MRR ≥ 0.60 — met |
| … dense only | R@5 0.881, MRR 0.742 | |
| … lexical only | R@5 0.643, MRR 0.509 | |
| … by project (hybrid R@5) | carddemo 0.93, fineract-legacy 0.93, zopeneditor-sample 0.86 | |
| … questions that name code | hybrid MRR 1.00, dense 0.79 (why BM25 stays in) | |
| `role` classifier (1,725 examples, 26 programs) | LightGBM: pooled micro **63.7%**, macro 47.9% (SDCA 61.5/43.8; zero-shot 22.7/36.1). `mixed` (43) is never predicted; io-adapter recall 0.40 | ≥ 70% — **not met** |
| `ruleKind` classifier (2,893 examples, 54 programs) | SDCA: micro **71.9%**, macro 49.0% (zero-shot 25.3/37.3) | reported |
| `function` classifier (2,119 weak labels) | SDCA: micro **74.0%**, macro 55.4% agreement with the glossary (zero-shot 47.8/55.5) | reported |
| `function` vs SME sample (40 chunks, seed 20261011) | **25/40 = 62.5%** (model 13/18, glossary 12/22). Program prior 1.5: 26/40 with CV 69.2/49.8; prior 0 kept for consistency | reported |
| Host :5110 over the main tree's projects | `/knowledge` renders; search, catalog, glossary, classification and ground endpoints answer; host :5100 untouched | works |

How to read these numbers:
- **The role target was missed** under the strict test: whole programs are held out, and the training set is mostly one estate's (Fineract J8) paragraph style.
- **Role predictions affect few chunks.** Every chunk of a program with a requirements packet carries the pipeline's own role. The model only fills the 28 CardDemo programs without packets and the idiomatic Java, and it marks those labels `model`.
- **The main function error is policy.** Plumbing paragraphs (file open and close, CICS browse) inside a business program are `batch-control-io` by the SME policy, but the glossary sees the program's file names. The owner should settle that policy before the sample grows.

## 13. Runbook

```powershell
cd C:\Users\Tomrodgers\Source\CobolBdm-kb
dotnet build MlCpu\src\MlCpu.SemanticKb.Cli
$kb = "dotnet MlCpu\src\MlCpu.SemanticKb.Cli\bin\Debug\net10.0\kb.dll"
$ws = "--workspace C:\Users\Tomrodgers\Source\CobolBdm\projects"
iex "$kb model --variant all"                 # download if missing; verify SHA-256 at the pinned revision
iex "$kb verify-model --variant fp32"         # parity with the HF reference fixture
iex "$kb build $ws"                           # all three projects; --threads 8 --max-tokens 1024 --rebuild --no-classify
iex "$kb search `"how is monthly interest calculated`" $ws"
iex "$kb eval $ws"                            # retrieval (gold questions) + function labels (SME sample) → work\kb\eval
iex "$kb ground `"`" --mode document --program FNEM0300 $ws --out FNEM0300.grounding.json"
iex "$kb terms `"EMI fix-up of the last unpaid repayment period`""   # glossary diagnostics
dotnet run --project MlCpu\src\MlCpu.Host --urls http://localhost:5110 --MlCpu:WorkspaceRoot=C:/Users/Tomrodgers/Source/CobolBdm/projects
# reference fixture (once, in a throwaway venv with tokenizers, onnxruntime, numpy):
python -I tools\kb\make-embedding-fixture.py C:\models\embeddinggemma-300m-ONNX knowledge\fixtures\embeddinggemma-reference.json
```

Pass `--MlCpu:WorkspaceRoot` with forward slashes from Git Bash; bash strips backslashes.

## 14. Risks and decisions

- **Role accuracy below target (§12).** Mitigation: pipeline labels win wherever they exist, and predictions are marked `model`.
  - Next steps if role matters for the owner:
    - add CardDemo online programs' requirements packets;
    - or derive role for paragraphs without packets the way the pipeline does, from segment kinds.
- **Weak `function` labels.**
  - Cross-validation measures agreement with the glossary, not accuracy. The SME sample (n = 40) is the accuracy number.
  - Taxonomy, glossary and sample are unreviewed. Grow the sample to about 150 once the owner settles the plumbing policy.
- **Tuning on the test set.** The BM25 prose weight and the function prior were chosen on the gold sets; a held-out set would give unbiased numbers.
- **Fineract-legacy's semantic names are unreviewed fallbacks** (`ProgramFNEM0300`). The catalog names functions from the header *Function* line and the taxonomy instead.
- **Merge.** `Program.cs` and `MainLayout.razor` conflict with the main tree's uncommitted edits in two small hunks. `MlCpu.slnx` gains two projects.
- **The VSIX listener exists only while VS Code is open.** Blazor shows its state. A Windows service was considered; the owner asked for a VSIX.
- **Gemma 4 latency and the single slot.** Asks are queued and the queue is visible. Document packets are capped at 24k tokens.
- **Alternatives rejected:**
  - Python sentence-transformers: the owner asked for ML.NET.
  - A vector database: not needed at 10⁴ rows.
  - Padding buckets: ML.NET's variable-length rows work.
  - GPU embedding: it would compete with Gemma 4.
  - q8 as default: slower here.
  - Equal-weight RRF: worse than dense alone on prose.

## 15. Status log

- **2026-10-11 — Design.**
  - Worktree `CobolBdm-kb` created (branch `knowledge-base`).
  - EmbeddingGemma fp32 and q8 downloaded to `C:\models\embeddinggemma-300m-ONNX`; the SHA-256 of every file matches.
  - Spike: ML.NET `ApplyOnnxModel` accepts variable-length rows, and its vectors equal Python `onnxruntime`.
- **2026-10-11 — Phase 1 built.**
  - Built: `MlCpu.SemanticKb` and the `kb` CLI; the taxonomy (16 classes), glossary (149 terms), prompts, gold questions (42) and SME label sample (40).
  - Indexes for carddemo, zopeneditor-sample and fineract-legacy: 3,739 chunks, 1.48M tokens, embedded in 3.8 min.
  - Classifiers, catalogs and the workbench page with its API; verified on :5110. All results are in §12.
  - Nothing is committed. The files are in the worktree, and the indexes are in the main tree's git-ignored `projects\<id>\knowledge` and `work\kb`.
- **Next (owner):**
  - review the taxonomy, glossary, gold questions and SME sample (all `reviewed: false`), and the plumbing policy;
  - say when the Gemma 4 test is done, so Phase 2 can start: the VSIX, a live grounded Ask, and the §11.4 evaluation;
  - decide on committing and merging `knowledge-base` (two small hunks in the Host), and a README row for the Knowledge Base page.
