# Customer Account Service: the forward-engineering SME package

This is Phase 0 of `tools/eval/PROTOCOL-forward.md`, built by the SME before any model runs. The service is specified,
tested and implemented once by the SME. Then models design and implement it again, and the suites judge them.

| Path | What | Shown to models |
|---|---|---|
| `SPEC.md` | Purpose, bounded contexts, glossary, use cases, idempotency, the customer statement (STMT), number rules, decisions D-1 to D-11 | Yes |
| `RULES.md` | Design rules A-1 to A-11 and the test that enforces each | Yes |
| `digests/*.md` | One rule digest per legacy program (POST, INT, RPT, CUST, UPD, AVG), traced to the requirements packets | Yes |
| `service/` | The judge's Gradle build. It contains `cas-api` (the fixed API), `cas-architecture` (ArchUnit, RULES.md) and `cas-acceptance` (scenario runner and in-memory ports). The five implementation modules are empty | `cas-api`, the in-memory ports, public scenarios |
| `service/cas-acceptance/src/test/resources/scenarios/{public,hidden}/<slice>/` | 285 acceptance scenarios: 58 public, 227 hidden | Public only |
| `scenarios.json` | The scenarios' manifest: slice, visibility, rules, primary rule, origin | Public entries |
| `cross-check.md` | The reference implementation against the faithful programs: 200 agree, 44 differ only by the listed decisions | No |
| `reference/` | The SME's reference implementation of the five modules | No |
| `rubric.md` | The design rubric with its anchors, fixed before any design | No |
| `schemas/` | JSON Schemas of the architecture (1a), the slice designs (1b) and the peer reviews | Yes |
| `gate.json` | The Phase 0 gate result: the reference passes 285/285 and 7/7, mutation score 98.2%, every rule traced | No |
| `sme/` | The SME's scenario tooling: the legacy export tests and ScenarioBuild (tags, cross-check, authored scenarios, split) | No |
| `tools/Build-Scenarios.ps1` | Rebuilds every scenario from the published estates (`workspace-v2`) | - |
| `tools/Test-ForwardGate.ps1` | The quality gate: reference against all suites, PIT mutation score, traceability, public share | - |

**Where expected values come from:**
* Legacy-derived scenarios: the equivalence-test inputs of CBTRN02C, CBACT04C, CBTRN03C and SAM1/SAM2 are run by the
  faithful programs (byte-identical to GnuCOBOL) and decoded into the service's terms.
* Every scenario: the expected values are the reference implementation's results. On every legacy-derived scenario these
  agree with the faithful program, except where a decision D-n of `SPEC.md` changes the rule (`cross-check.md`).
* The customer statement has no legacy program. Its scenarios are SME-authored and rest on `SPEC.md` section 6.

To rebuild and re-check:

```powershell
powershell -ExecutionPolicy Bypass -File forward\cas\tools\Build-Scenarios.ps1
powershell -ExecutionPolicy Bypass -File forward\cas\tools\Test-ForwardGate.ps1
```

The model runs use `tools/eval/Invoke-ForwardEval.ps1` with `tools/eval/eval-config-forward.json`.
