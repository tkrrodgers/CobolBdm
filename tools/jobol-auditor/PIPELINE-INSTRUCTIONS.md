# JOBOL gate: instructions for the generator and the checker

Give this file to the model that generates Java from COBOL (GPT Sol 6.1) and to the model that checks it (Claude Opus 5.5). The design and rule rationale are in [JavaJOBOLAuditor.md](../../JavaJOBOLAuditor.md).

## The target

The modern lane must read as Java written by a Java developer who understood the business. It must not read as COBOL in Java syntax. Behaviour parity is necessary but not sufficient: an estate passes only when parity holds **and** the JOBOL gate passes.

## The loop

1. Generate or modernize the estate.
2. Audit it against the previous iteration:
   ```bash
   tools/jobol-auditor/jobol-audit.sh <estate> --out work/jobol/<name>/iter-N --baseline work/jobol/<name>/iter-N-1/jobol-report.json
   ```
   PowerShell: `tools\jobol-auditor\jobol-audit.ps1 ...` with the same arguments. Exit 0 means pass, 1 means the gate failed, 2 means a usage or I/O error.
3. If the gate fails, read `jobol-report.json`. `types` is sorted worst first, and every finding has a `rule`, a `line` and a `message`. The `rules` array carries each rule's `fix`. Treat the worst types as the change list for the next iteration.
4. Regenerate and audit again. Done means gate PASS, no regression against the baseline, and parity still green.

## Generator rules: emit these, not their COBOL equivalents

| Instead of (rule) | Emit |
|---|---|
| WORKING-STORAGE fields shared by paragraphs (JOB020, JOB021) | methods with parameters and return values; per-run state in a small immutable record passed explicitly |
| `ws`/`dw`/`ls`/`fd` prefixes, `ACCT_ID`, `acct_id`, `_2` suffixes, `flag2` (JOB010–JOB012) | lowerCamelCase business names; collections instead of numbered series |
| `'Y'`/`'N'` switches, 88-levels (JOB030) | `boolean`; an `enum` for condition-name sets |
| FILE STATUS `"00"`/`"10"` in logic (JOB031, JOB032) | an adapter that returns `Optional`/an iterator and throws domain exceptions |
| priming read loops (JOB050) | `for (var rec : reader)` or a stream |
| PICTURE masks and `substring(0, 11)` parsing (JOB040, JOB042) | a compat codec that decodes to a Java `record` once; `BigDecimal` with scale; `LocalDate` |
| `double` money (JOB041) | `BigDecimal` with the RoundingMode that matches COBOL ROUNDED or truncation |
| `String` dates (JOB043) | `LocalDate`/`LocalDateTime` at the record boundary |
| labels and `break label` (JOB052), paragraph-numbered methods (JOB003), `perform(n, m)` (JOB002) | small named methods with early returns |
| `System.out` DISPLAY (JOB055), `System.exit` ABEND (JOB053) | a logger or report port; exceptions mapped to an exit code in `main` only |
| `"H"`/`"P"`/`"E"` record-type and status strings switched on or `equals`-chained (JOB033) | an enum decoded once at the record boundary; JDK enums such as `RoundingMode.valueOf` |
| `raw(13, 1)` / `substring(0, 11)` byte offsets in business code (JOB042) | typed accessors on the record (`amountSign()`, `loanKey()`); offsets live only in the codec |
| `if (!fits(x + 1, 9)) overflow()` at every counter (JOB058) | a counter value type whose increment enforces the PIC 9(n) limit |
| `com.mlcpu.rt` / `CobolBase` / `Prog_*` in modern code (JOB001) | stay behind the strangler or compat adapter; modern code never sees `Area`/`CobField` |

## Checker rules

- Verify each finding was **resolved by restructuring**, not hidden. Read `baseline.resolvedFindings` and `newFindings`, and look at the code.
- These are violations, even when the gate passes:
  - moving modern code into `legacy/`, `record/`, `mapping/` or another non-modern lane
  - renaming `dwEof` to `endOfFileFlag` while keeping the shared mutable field
  - editing `jobol.json` to disable rules or raise the gate

  Any lane or rule change alters `rulesetHash`, and the report then says the baseline is not comparable.
- Judge what the auditor cannot measure: names that are truncated (`disbursementDueDat`) or meaningless, a class that is a thin facade over faithful code, and methods that are still one paragraph each.
