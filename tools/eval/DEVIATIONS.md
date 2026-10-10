# Deviations from PROTOCOL.md

| When | What happened | Effect on results | Handling |
|---|---|---|---|
| 2026-10-10 07:38 | The first Gemma arm-A run stopped after its three CardDemo designs: a log monitor held `eval.log` open, so `Add-Content` failed and the runner aborted (harness fault, not a model failure). | None: no implementation ran; the designs were all valid on the first attempt. | Archived in `aborted/A-gemma-4-31B-it-0738-log-lock`; logging made non-fatal and runs marked `completed` (commit cb9aea2); arm A for Gemma rerun from scratch by the queue. |
