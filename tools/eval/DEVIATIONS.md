# Deviations from PROTOCOL.md

| When | What happened | Effect on results | Handling |
|---|---|---|---|
| 2026-10-10 07:38 | The first Gemma arm-A run stopped after its three CardDemo designs: a log monitor held `eval.log` open, so `Add-Content` failed and the runner aborted (harness fault, not a model failure). | None: no implementation ran; the designs were all valid on the first attempt. | Archived in `aborted/A-gemma-4-31B-it-0738-log-lock`; logging made non-fatal and runs marked `completed` (commit cb9aea2); arm A for Gemma rerun from scratch by the queue. |
| 2026-10-10 15:15 | Laguna S 2.1's arm-A run stopped by the owner during CBTRN03C (attempt 9 of 15, stuck at 1/23 for four attempts) after CBTRN02C 23/23 (attempt 7) and CBACT04C 17/17 (attempt 15); SAM1 and SAM2 not run. Laguna's arm B dropped. | Laguna has no complete arm-A result; it cannot meet `PROTOCOL.md` criterion 1 as recorded. Its partial results stay in `runs/A/laguna-s-2.1`. | Queue restarted with Inkling-Small arm A, then arm B for Gemma and Inkling-Small. |
