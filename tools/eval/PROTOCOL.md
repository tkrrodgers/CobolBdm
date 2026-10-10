# Model evaluation protocol: Laguna S 2.1 and Inkling-Small against Gemma 4

**Question.** Is either challenger worth integrating into the normalization pipeline in place of, or next to, Gemma 4 31B?

**Scope.** The pipeline step Gemma owns today: design a capability from its requirements packet, implement the design in
idiomatic Java on the platform layer, and pass the program's equivalence test (every scenario, every dataset byte for byte,
SYSOUT and return code), with compiler errors and every differing byte fed back between attempts.

This protocol was fixed before any challenger result existed. Changes after a run starts are recorded as deviations
in the report.

## 1. What is held constant

| Factor | Setting | Why |
|---|---|---|
| Inputs | Frozen copies of both published estates (`workspace/projects/`, copied 2026-10-10 before the first run) | Every model sees byte-identical packets, outlines, examples, tests and oracle outputs |
| Programs | CardDemo CBTRN02C, CBACT04C, CBTRN03C; zopeneditor SAM1 (step), SAM2 (subprogram) | Every program Gemma has been judged on: two estates, both program kinds |
| Harness | `MlCpu.DesignModel` from branch `model-eval` (commit recorded per run) | Same prompts, parser, feedback, diff probe and best-attempt rule |
| Runtime | One llama.cpp build for all three: Unsloth b11539-mix (upstream b11539 + Inkling PR #25731), CUDA 13.3 | Runtime differences cannot explain a result |
| Context, budget | 131,072 tokens; 49,152 output tokens per reply; seed 42; reasoning medium for design, high for implementation | As in the workbench |
| Conversation | Base prompt + the latest files + the feedback (no growing history) | Identical for all; Laguna's card prefers preserved thinking, so this is noted as a possible handicap, not changed |
| Fallback | `--no-fallback`: every reply comes from the model under test | |

## 2. What varies (each model at its best published setting)

| | Gemma 4 31B (baseline) | Laguna S 2.1 | Inkling-Small |
|---|---|---|---|
| Lab, license | Google, Apache-2.0 | Poolside, OpenMDW-1.1 + AUP | Thinking Machines, Apache-2.0 + AUP |
| Architecture | 31B dense | 118B MoE, 8B active | 276B MoE, 12B active |
| Precision on this machine | BF16 (full) | Q8_0 | UD-Q5_K_XL (Q8 does not fit) |
| Sampling (vendor's) | temp 1.0, top-p 0.95, top-k 64 | temp 1.0, top-p 1.0, top-k 20 | temp 1.0, top-p 1.0 (Unsloth; card gives none) |
| Speed aid | MTP draft model | none (DFlash needs Poolside's fork) | none |

Precision is the one asymmetry that favours the baseline; the challengers run at the highest precision this machine holds.
Gemma is **re-run** under this protocol rather than compared with its 2026-10-09 results, which used an older harness state
and default sampling.

## 3. Arms

* **A, production guidance:** AST outline in both prompts, worked examples and parity notes in the implementation prompt;
  3 design attempts, 15 implementation attempts. This is the configuration behind Gemma's 116/116 and 61/61.
* **B, lean guidance:** the same without worked examples and parity notes; 10 implementation attempts. Arm A is near its
  ceiling for Gemma, so arm B is where a better model can show it: deriving COBOL storage semantics from the packet and the
  outline instead of having them spelled out.

## 4. Measures

Per model, arm and program: design valid (and attempts), acceptance scenarios passed, output differences of the best attempt,
attempts to first full pass, minutes, prompt and generation tokens per second.
Per model and arm, per estate: the assembled service (the workbench's rule: a program's code goes in only if it passed every
scenario) verified by an isolated host: every test, then each job with `--mlcpu.implementation=normalized` against the
GnuCOBOL oracle (CardDemo POSTTRAN, INTCALC, TRANREPT: 11 outputs; zopeneditor ZDERUN over two scenarios: 6 outputs).
Operational: load time, GPU and RAM footprint, wall clock.
Code (SME review, blind to the model): size, use of the platform layer, readability, and a check that no scenario data is
hard-coded (the acceptance test is shown to the model, so this guards against fitting the test instead of the program).

## 5. Decision rule

A challenger is **worth integrating** only if all of these hold:

1. **Parity in arm A:** every program passes its acceptance test and the assembled services match the oracle on every job
   output (17/17), as Gemma's do.
2. **A material advantage over Gemma** in at least one of: more programs passed in arm B; at least 30% fewer total
   implementation attempts or minutes over the programs both pass; a clearly better blind code review.
3. **No disqualifier:** a runtime that still needs an unmerged llama.cpp change at decision time counts against integration
   (Inkling today), as do licence terms that block the intended use, or a wall clock that makes the workbench impractical
   (more than 3x Gemma's).

With one seed per cell, a difference of one program or under 30% in attempts is within run-to-run noise; such cells are
re-run with seeds 7 and 1234 before a conclusion is drawn.

## 6. Self-tests (harness credibility)

Before any model result counts: the verifier must reproduce the published estates' results (CardDemo 116/116 tests and 11/11
outputs; zopeneditor 61/61 and 6/6) and Gemma's 2026-10-09 service (same numbers) on the frozen workspace.
