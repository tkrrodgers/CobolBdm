# Phase M, rule specs batch 1 (from the gap catalogue, commit 94708e7)

Source: tools/jobol-auditor/COMPARE-GAPS.md. The finding that frames the batch: every method J3S reached is present in
the modern Java (METHOD.MISSING = 0, OVERLOAD.COLLAPSED = 0 in all four estates). The gaps are in FORM: names,
visibility, types and idioms. Behaviour is not lost.

## Evidence rule for every spec (binding)
- Allowed evidence: the COBOL program text and its data layouts, the generator IR, data and control flow, the estate's
  own identifier set, the call graph and reference graph of the generated estate, and the job/replay entry points.
- FORBIDDEN in the generator, even when it is present in the estate: J3S provenance comments that quote original Java
  names or types (CLASS and FRAME comments, "legacy PEC:182" citations used as name sources), and any original source.
  Such comments exist only in J3S estates, so a rule that reads them cannot generalize to CardDemo or real legacy COBOL.
  The compare tool may read them, because measuring is allowed. The generator may not.
- Every rule fails closed: when the evidence is ambiguous, keep the current output and log the decision in a
  per-rule ledger (modernize/<rule>.json).

## Measurement for every milestone
- `jobol-audit compare` for each of the 4 estates (--original, --modern <new generation>, --cobol <project>/source).
  Report the API, name and signature fidelity and the targeted gap counts. Show TRAIN (fineract-xl, kontor-zins) and
  TEST (joda-money, loanamorti) separately.
- Plus: strict parity under the approved policies only, the JOBOL gate, all seven anchors with no regression, and the
  build clean.
- Acceptance: the targeted gap falls on TEST as well as TRAIN, and no other gap code rises. A rule that improves only
  TRAIN is rejected.
- Precision: for a rename or completion rule, the share of changed identifiers that now equal the original name must be
  at least 98% on TRAIN, and is reported on TEST.

## Order (lowest risk and broadest first). One rule per milestone.

### R1. API-surface visibility (closes G21 widened 97, G22 narrowed 60)
- Evidence: the generated reference graph, plus the external entry surface. The external entry surface is the members
  reached from the job/replay entry dispatch (the API the spec drives from outside), the step entry points, the
  strangler ports, and anything reached by reflection or resource registration (Joda's registry and providers).
- Transformation:
  - an entry-surface member is public;
  - a member referenced only inside its type is private;
  - a member referenced only inside its package is package-private;
  - a member used from another package is public.
  Constants and statics follow the same rule.
- Fail closed: a member reached by reflection, by a string lookup or from an unknown caller keeps its current visibility.
- Expected effect: G21 and G22 fall together. TRAIN 72 + 58, TEST 25 + 2.

### R3. Sealed hierarchy facts: final classes and value records (closes G23 27; G19 4)
- final: a class with no subclass in the estate and not on the entry surface as an extension point is final.
- record: a class whose fields are all final, assigned only in its canonical constructor, exposed only through
  accessors, with equals/hashCode that are value-based or absent, is a record. Never turn a class into a record when
  identity is used (==, an identity map, mutation after construction).
- G20 ("classes promoted to records", 68) is class (c): records are acceptable modern Java. Do not undo them.
- Expected effect: TRAIN G23 20, TEST 7; G19 kontor-zins 4 (TRAIN only: report it as unvalidated).

### R5. Program to interfaces (closes G11 16; part of G13)
- Evidence: the uses of each local, field and parameter.
- Transformation: one declared as ArrayList, HashMap, LinkedHashMap, HashSet, TreeMap and so on, whose uses are only
  interface methods, is declared as List, Map, Set or SortedMap. Keep the concrete type at the `new`.
- Fail closed: any use of an implementation-only method (ensureCapacity, trimToSize, clone, descending views) keeps the
  concrete type.
- Expected effect: TRAIN 13, TEST 3.

### R2. Type recovery for Object-typed slots (closes G07 27, G09 48, G10 37)
- Evidence: flow-insensitive type sets over the IR. Every value stored into the slot and every value read from it are
  considered, including collection element writes for type arguments. Use J3S class-id dispatch tables only as
  structural data (the class ids and their layouts), never their Java names.
- Transformation:
  - a slot whose set is one estate type gets that type;
  - a collection whose element writes are one type gets that type argument;
  - a slot whose set is several types gets the nearest common estate supertype or interface, but only if the estate
    declares one; otherwise it stays Object.
- Fail closed: an empty or unknown set (values from outside the closure: G09) stays Object.
- Validation caveat: TEST has 1 instance (joda-money) and loanamorti has 0. Report it as TRAIN-validated only. It needs
  CardDemo or a leave-one-module-out check inside fineract-xl (derive from one half of its packages, test on the other)
  before it counts as generalized.

### R4. Evidence-based completion of truncated identifiers (closes G01 208; must NOT touch G02 570)
- Context: naive unique-prefix completion from the estate's identifiers would be wrong 98 + 21 times in fineract-xl.
  The catalogue rejects it.
- Evidence: a completion candidate C for truncated name T is accepted only when all of these hold:
  1. T has the shape of the 30-character cut, or the J3S cut length, measured from the COBOL item name;
  2. C extends T;
  3. C is the only such identifier in the estate;
  4. a direct data-flow link joins T and C (T is assigned from, passed to, or compared with a member named C, or C is a
     field and T its accessor or parameter), with type agreement.
  With no flow link, keep T. Never guess from frequency.
- Expected effect: G01 TRAIN 202, TEST 6, with G02 unchanged. Report the precision against the original names.

## Later batches (after batch 1 is measured)
- G15 domain interfaces/superclasses (12; mostly TEST: derive from fineract-xl's 3 only, or hold for CardDemo).
- G17 switch expressions and pattern matching (9).
- G03 abbreviation expansion through an estate-internal dictionary (19).
- G04 accessor style (20).
- G16 streams (19, class b; style only, so the lowest priority).
- Not pursued: G20, G24, G25, G26, G27, G28 (class c).

## Outcomes
- **R1 REJECTED (15:30).** G21 TRAIN 72 -> 10, TEST 25 -> 5; but G22 TRAIN 58 -> 108, TEST 2 -> 25, and reached-API
  fidelity fell. Least access cannot recover the authors' API intent, so G21 and G22 are reclassified to (b): they need an
  external API manifest. R1 stays opt-in only; the default is byte-identical.
- **Finding (disclosed):** for J3S estates, the modernizer's class, package and method names come from the J3S CLASS/FRAME
  provenance headers (pre-existing), which quote the original Java names. Name and structure fidelity on J3S estates are
  therefore inflated relative to real legacy COBOL. The `legacy` session's estates (no such headers) will measure it.
- **R3 narrowed** to `final` on classes only (G23). The record conversion is dropped (G19 is TRAIN-only, 4 instances, and
  it conflicts with G20). The acceptance is pre-registered in the hub message of 15:30.
- **R3 ACCEPTED (16:20), behind the explicit estate profile `closedWorld: true`; the default is off.** G23 TRAIN 20 -> 3,
  TEST 7 -> 0; no catalogued gap rises; 48/48 self-tests. TYPE.FINAL_ADDED (22, 52% precision against the originals) is
  stricter than the originals. It is classed (c) under closedWorld only, because Spring/JPA proxies, mocks and library
  extension need open classes, and the closed estate cannot see them. The profile is a per-estate deployment decision,
  never inferred.
