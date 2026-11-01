# jobol-auditor

This is a deterministic check of whether generated Java is modern Java or *JOBOL* (COBOL in Java syntax). It uses no model. For each estate it writes `jobol-report.json` (for the pipeline and LLMs), `jobol-report.md` (for people) and `jobol-report.sarif` (for SonarQube, VS Code and GitHub). The design, rule rationale and calibration are in [JavaJOBOLAuditor.md](../../JavaJOBOLAuditor.md). The prompt-ready loop for the generator and checker is in [PIPELINE-INSTRUCTIONS.md](PIPELINE-INSTRUCTIONS.md).

## Run

```bash
tools/jobol-auditor/jobol-audit.sh C:/Users/Tomrodgers/Source/ModernizedJava/loanamorti --out work/jobol/loanamorti
```

```powershell
tools\jobol-auditor\jobol-audit.ps1 C:\Users\Tomrodgers\Source\ModernizedJava\loanamorti --out work\jobol\loanamorti
```

Options:

| Option | Effect |
|---|---|
| `--baseline <prev>/jobol-report.json` | report the change; fail on regression |
| `--config jobol.json` | lane globs, rule overrides, gate. Start from `--print-config` |
| `--max-index N` | override the gate's maximum estate index |
| `--top N` | number of types in the Markdown report |
| `--format json,md,sarif` | which reports to write |
| `--no-gate` | always exit 0 |
| `--quiet` | no console summary |
| `--rules` | print the catalog |

Exit codes: 0 means the gate passed, 1 means it failed, 2 means a usage or I/O error.

The scripts run the Gradle distribution if `gradle installDist` has been run here. Otherwise they compile with `javac` against javaparser-core 3.28.2 and gson 2.11.0 from the Gradle cache into `build/direct`. That needs no network and no Gradle daemon. Set `JOBOL_DIRECT=1` to force the fallback.

## Distance to original (compare)

`compare` measures how far the modern Java is from the original Java that J3S translated to COBOL. The original Java is the answer key the generator never sees. Types align by fully qualified name (nested types too), then by unique simple name (moved types). Members align by name and arity, then by signature. The report covers API, name and signature fidelity, truncated, abbreviated and numbered names, idioms (Optional, streams and lambdas, collections, BigDecimal, java.time, records and enums), structure, constructors, factories, builders, equals/hashCode/toString, the top 20 types by distance, and every gap instance with a mechanical code (`NAME.TRUNCATED.NONE`, `TYPEREF.GENERIC_TO_OBJECT_KNOWN`, ...). Modern-only types in the compat and faithful lanes, or matching `compareInfrastructureGlobs` (default `**/batch/**`), are pipeline infrastructure, not gaps.

```bash
tools/jobol-auditor/jobol-audit.sh compare --original <orig>/src/main/java --modern <estate>/src/main/java \
  --cobol <CobolBdm>/projects/<estate>/source --out compare/<estate>
tools/jobol-auditor/jobol-audit.sh compare-gaps --catalogue tools/jobol-auditor/compare-catalogue.json \
  --report compare/a/compare-report.json --report compare/b/compare-report.json --out compare --md COMPARE-GAPS.md
```

| Option | Effect |
|---|---|
| `--original DIR` | repeatable; a `*` segment expands (`vendor/fineract/*/src/main/java`) |
| `--cobol DIR` | J3S COBOL: CLASS headers give the reached classes, FRAME comments the reached methods. It sets `--scope reached` (original types J3S compiled, or that modern names) and splits missing methods into `METHOD.UNREACHED_*` (never compiled) and `METHOD.MISSING_*` (lost by the generator) |
| `--scope all\|reached` | override the scope |
| `--name`, `--out`, `--config`, `--examples N`, `--quiet` | estate name, output directory, lanes, examples per gap in the Markdown |

Both outputs are deterministic: there is no clock and all lists are sorted. `compare-estates.sh` reruns the four loop-4 estates and regenerates [COMPARE-GAPS.md](COMPARE-GAPS.md), the cross-estate gap catalogue. The patterns, their closability classes and the generator hypotheses live in `compare-catalogue.json`; the counts and examples come from the reports under `compare/`.

## Build and test

```bash
cd tools/jobol-auditor && gradle test installDist
```

Without Gradle, `tools/jobol-auditor/jobol-test.sh` compiles the main and test sources with `javac` against the Gradle cache and runs them through the JUnit Platform launcher (`test-runner/JUnitMain.java`).

## SonarQube

Add the report to the analysis so the findings appear as external issues next to Sonar's own:

```properties
sonar.sarifReportPaths=work/jobol/<estate>/jobol-report.sarif
```
