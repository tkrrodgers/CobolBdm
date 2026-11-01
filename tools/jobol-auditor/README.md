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

## Build and test

```bash
cd tools/jobol-auditor && gradle test installDist
```

## SonarQube

Add the report to the analysis so the findings appear as external issues next to Sonar's own:

```properties
sonar.sarifReportPaths=work/jobol/<estate>/jobol-report.sarif
```
