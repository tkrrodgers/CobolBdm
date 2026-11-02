# The deep thinker's notebook

This is the memory the deep thinker (loops/DT-deep-thinker.md) builds across estates. Each estate it names should make
the next one faster and better. The notebook is versioned in the repo and reviewed like code.

| File | Holds | Example |
|---|---|---|
| `../glossary/financial-terms.json` | business terms and their COBOL code terms (existing; 149 terms) | PRIN -> principal |
| `glossary.json` | estate-confirmed abbreviations that the base glossary lacks | `AMORT` = amortization, confirmed in two estates |
| `patterns.json` | recurring COBOL idioms and what they mean in modern Java | a function-code-dispatched library program is a kernel |
| `cautions.json` | where the thinker was wrong before, and why | a `-SAVE` area is not a domain entity |

Schema `mlcpu.kb-notebook.v1`. Every entry has:
- `id`, `kind` (`glossary|pattern|caution`), `text`, and `confidence` (0 to 1);
- `evidence`: COBOL-side citations `{estate, file, line, quote}`, with verbatim quotes of 15 words or fewer that `naming validate` checks. The lead's seed entries use `summary` with `line: null`, which describes the evidence rather than quoting it;
- `origin`: `{source: "lead|thinker|review|evaluation:<estate>", run, ledger}`;
- `reviewed`: `true` only when Tomrodgers or an SME accepted it; `estates`: where the entry was confirmed.

Rules:
1. **COBOL-side evidence only.** An entry never names an identifier from an original Java tree. The original names are
   the answer key, so a note that quotes them leaks the answer.
2. **Held-out discipline.** An entry whose `origin.source` is `evaluation:<estate>` was learned by scoring against that
   estate's original Java. It is never put into a packet for that same estate. It may help other estates.
3. **Weight.** Reviewed entries are trusted. Unreviewed entries are hints and are labelled as such in the packet.
4. **Growth.** After each run, the thinker proposes notes. `naming validate` checks their citations, and the review queue
   promotes them. The lead may seed entries from verified loop evidence as `reviewed: false`, `source: lead`.
5. **Matching.** The packet exporter includes the base-glossary terms whose `codeTerms` occur in the program's data
   names, plus every pattern and caution. They are small enough to include in full.
