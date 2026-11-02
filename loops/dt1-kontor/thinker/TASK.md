# Deep thinker task: naming-1u (unit-level naming)

You are a senior COBOL business analyst and Java designer. The original authors of this batch system have retired. A
generator has turned its COBOL into Java, but the Java names are mechanical: program ids, record prefixes and paragraph
names. Your job is to give each naming unit the business name an SME would give it, with evidence.

## What you may read: ONLY this folder
- `source/app/cbl/*.cbl`: the programs. `source/app/cpy/*.cpy`: the copybooks. `source/app/jcl/*.jcl`: the job.
- `scenarios/` and `source/app/data/`: sample input records, with letters and digits masked (layout only).
- `notebook/entries.json`: glossary terms, patterns and cautions from earlier estates. Entries with `reviewed: false` are
  hints, not rules.
- `worklist.json`: the 390 naming units. Each unit has a `unitKind`, its COBOL `legacy` ids, the `programs` it appears
  in, the Java `owners`, a `currentStem`, and its `members` (the Java symbols, with ids and current names).
Read nothing outside this folder: no other directories, no web, no repositories. If something you need is missing,
write that in `notes`.

## The unit rule
Give each unit ONE name, its stem. The pipeline renames the members deterministically: it replaces `currentStem` with
your stem and keeps each member's suffix. For example, `function`, `functionCode` and `functionText` become
`calculationFunction`, `calculationFunctionCode` and `calculationFunctionText`.

How each unit kind is named:
- `data`: a COBOL data item; its accessors and setters follow from the stem. Use a lowerCamel noun.
- `type`: a Java class, record or enum. Use an UpperCamel noun.
- `method`: a paragraph or section. Use a lowerCamel verb phrase as the stem. Name its parameter members individually
  in `parameters` (member id -> lowerCamel noun). If `currentStem` is null, the method itself is locked: give
  `parameters` only.
- `code-enum`: the 88-level or value constants of a code field. Set `name` to null and name each constant member in
  `constants` (member id -> UPPER_SNAKE). Expand the abbreviated value meanings when the code shows them.
- `record-instance` and `instance`: a field holding a record or state object. Use a lowerCamel noun.
- `predicate`, `increment`, `state` and `switch-validator`: the stem as the role suggests: isX/hasX, incrementX, a noun,
  validateX.

## What you write
Write one file per program, in this order: `proposals/CEE3ABD.json`, `FNDKRND.json`, `FSEM0100.json`, `FSJBDEC.json`,
`KZLE0100.json`. Each file covers the units whose `programs[0]` is that program. Every unit appears in exactly one file.
Each file uses this format:
```json
{ "schema": "mlcpu.naming-unit-proposals.v1", "estate": "<from worklist>", "packetSha256": "<from worklist>",
  "model": "<your exact model id>", "promptVersion": "naming-1u",
  "units": [ { "unit": "U003", "name": "calculationFunction", "confidence": 0.8,
      "rationale": "<one or two sentences>",
      "evidence": [ { "file": "source/app/cpy/FNDKPRM.cpy", "line": 3, "quote": "<VERBATIM text from that line, 15 words or fewer>" } ],
      "notebook": ["P006-prefix-is-not-meaning"],
      "parameters": { "<member id>": "<name>" }, "constants": { "<member id>": "<NAME>" } } ],
  "notes": [ { "kind": "glossary|pattern|caution", "text": "<generic lesson for the NEXT estate>",
      "evidence": [ { "file": "...", "line": 0, "quote": "..." } ] } ] }
```
Include every unit of the program. If the current name is already the right business name, set `"keep": true`, give
evidence, and omit `name`.

## Rules
1. **Evidence.** Every unit needs at least one quote that appears VERBATIM, case and spacing included, on the cited
   line of the cited file. Paths are relative to this folder. A machine checks every quote, and a unit whose quote does
   not match is rejected. Quote the data declaration, the comment, or the statement that shows the meaning.
2. **Confidence** is the probability that an SME would accept the name unchanged:
   - 0.9 or more: a comment or header states the business meaning;
   - about 0.7: the data names and how the code uses them make it clear;
   - 0.5 or less: you are inferring from structure only. These go to human review, not into the code. A confident
     wrong name is worse than a plain one.
3. **Consistency.** The same business concept gets the same word everywhere: a principal is `principal` in every record
   and method. Signed and unsigned twins (-S/-U) and text/code variants keep the suffix logic from the unit rule; do not
   invent different words for them.
4. **Conventions.**
   - Expand abbreviations, using the glossary.
   - Drop shop and program prefixes (WS-, LK-, KZ, FS, DK, ...) and paragraph numbers.
   - Avoid empty suffixes (Data, Info, Manager, Util).
   - I/O record layouts are boundary types (`...Record`, or `...Line` for a report line). Business values used as one
     unit are domain nouns.
   - A library program called with an operation code is a capability (`DecimalKernel`-style).
   - Scratch areas (-SAVE, -WORK, -REGS, -AREA) are not business entities: name them plainly after the algorithm's
     role, at modest confidence.
5. **No collisions.** Two units in the same owner must not end up with the same name, and two types must not share a
   name. Check before writing.
6. **Notes.** Give 0 to 5 generic lessons that would help name the NEXT estate, each with evidence from this folder.

Work in this order: the job (JCL) and the driver program first, then its records and the business program, then the
library programs. When all five files are written, reply with a short summary:
- units named, kept and low-confidence;
- the 10 most important type names;
- anything you could not decide.
