# Deep thinker: the lead's runner tools (loops/DT-deep-thinker.md)

The deterministic steps around one thinker run. The pipeline owns the packet exporter, `naming validate` and
`lift --names`. These scripts sit between them and the model.

| Step | Tool | Input | Output |
|---|---|---|---|
| 1. Isolate | `dt_worklist.py <packet> <thinker-dir>` | a naming packet | a folder holding only copies of source/, scenarios/ and notebook/, plus worklist.json (proposable symbols grouped into naming units) |
| 2. Think | an LLM agent, packet-only | thinker-dir + `knowledge/prompts/naming-1u.md` (as TASK.md) | proposals/<PROGRAM>.json (`mlcpu.naming-unit-proposals.v1`) |
| 3. Pre-check and expand | `dt_expand.py <thinker-dir> <packet> <out>` | the unit proposals | proposals.json (`mlcpu.naming-proposals.v1`, one entry per symbol id), precheck.json/.md |
| 4. Validate and ledger | pipeline: `naming validate <proposals.json> <packet>` | proposals.json | naming-ledger.json, review-queue.json/.md |
| 5. Apply | pipeline: `lift ... --names <ledger>` | the ledger | renamed Java; all gates |
| 6. Score (lead only) | `dt_eval.py <thinker-dir> <original-java> <out.md>` | the held-out original Java | vocabulary hit and concept recall vs the generator baseline |

`dt_expand` fails closed per unit on any of:
- a missing unit;
- a quote that is not verbatim on its cited line, or longer than 15 words;
- a broken naming convention, or a Java keyword;
- a type name that shadows a JDK type (e.g. `RoundingMode`);
- a merged non-data unit;
- a collision against the full inventory, locked symbols included.

Data units derive their accessor and setter names by suffix (stem + current[len(currentStem):]). Renames equal to the
current name are dropped as no-ops. Units below confidence 0.7 go to `review`.

`dt_eval` reads the original Java and is therefore lead-only. Its output never goes into a packet or the notebook for
the same estate (knowledge/notebook/README.md, the held-out rule). Note that concept recall was defined after the
first run, so it is exploratory and not a pre-registered acceptance measure.

Run python with `-I` on packet data (isolated mode).
