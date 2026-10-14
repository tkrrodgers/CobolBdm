"""The leak checker's dictionary (ForwardEngineeringFineract.md section 4.3): every identifier of the source system's classes in
scope, and whether a prompt may carry it.

The classes are those stage J1 parses (fineract-cobol-gen config/sources.yaml, with their nested classes); their class, method
and field names come from the compiled classes in the embeddable jar (javap -p, JDK 25: the classes are Java 25). A name is
allowed when a real estate could have it too:
  * cobol: it is the camel case of a run of words of a paragraph or data name of the clean-room estate
    (4000-ADJUST-REPAYMENT-DATE gives adjustRepaymentDate, repaymentDate, ...);
  * short: shorter than 8 characters;
  * jdk:   a member name of a JDK type the engine uses (compareTo, isBefore, stripTrailingZeros);
  * word:  one word (no inner capital): common English and Java words.
Every other name is a leak: a prompt that carries it is blocked. The words of the source system's name are matched as
substrings by the checker itself, not listed here.

Usage: python -I build_leak_dictionary.py <fineract-cobol-gen dir> <clean-room package source/app dir> <out .tsv>
Output: one line per name: name <TAB> status (leak, cobol, short, word) <TAB> where (abbreviation.member kind).
"""
import os
import re
import subprocess
import sys
import zipfile


def classes(gen):
    vendor = os.path.join(gen, "..", "vendor")
    jar = os.path.join(vendor, "fineract", "fineract-progressive-loan-embeddable-schedule-generator", "build", "libs",
                       "fineract-progressive-loan-embeddable-schedule-generator-all.jar")
    out = {}
    for line in open(os.path.join(gen, "config", "sources.yaml"), encoding="utf-8"):
        m = re.match(r"^\s+([A-Z]+):\s+(\S+\.java)\s*$", line)
        if m:
            path = m.group(2).split("src/main/java/", 1)[1]
            out[m.group(1)] = path[:-len(".java")].replace("/", ".")
    return jar, out, os.path.join(vendor, "jdk-25.0.4.1+1", "bin", "javap")


MEMBER = re.compile(r"^\s+(?:[\w.$<>\[\],? ]+\s)?([\w$]+)\s*(\(|;)")


def members(javap, jar, names):
    """(simple name, kind) for the classes and their members (jar "": the JDK's own classes)."""
    out = []
    text = subprocess.run([javap, "-p"] + (["-cp", jar] if jar else []) + names, capture_output=True, text=True, check=True).stdout
    for line in text.splitlines():
        h = re.match(r"^(?:.*\s)?(?:class|interface|enum|record)\s+([\w.$]+)", line)
        if h and not line.startswith(" "):
            for part in h.group(1).split(".")[-1].split("$"):
                if part and not part.isdigit():
                    out.append((part, "class"))
            continue
        m = MEMBER.match(line)
        if m:
            out.append((m.group(1), "method" if m.group(2) == "(" else "field"))
    return out


def cobol_runs(src):
    """Lower-case concatenations of every run of words of every paragraph and data name."""
    names = set()
    for root, _, files in os.walk(src):
        for f in files:
            if not f.lower().endswith((".cbl", ".cpy")):
                continue
            for line in open(os.path.join(root, f), encoding="latin-1"):
                if len(line) > 6 and line[6] == "*":
                    continue
                m = re.match(r"^ {7}([0-9A-Z][A-Z0-9-]*)\.\s*$", line) or re.match(r"^\s+\d\d\s+([A-Z:][A-Z0-9:-]*)", line)
                if m:
                    names.add(m.group(1).replace(":M:", "LM"))
    runs = set()
    for n in names:
        words = [w for w in n.split("-") if w and not w.isdigit()]
        for i in range(len(words)):
            for j in range(i + 1, len(words) + 1):
                runs.add("".join(words[i:j]).lower())
    return runs


JDK = ["java.lang.Object", "java.lang.Comparable", "java.lang.String", "java.lang.Enum", "java.lang.Record", "java.lang.Iterable",
       "java.math.BigDecimal", "java.math.MathContext", "java.math.RoundingMode", "java.time.LocalDate", "java.time.YearMonth",
       "java.time.temporal.ChronoUnit", "java.time.temporal.TemporalAdjusters", "java.util.List", "java.util.Map", "java.util.Set",
       "java.util.Collection", "java.util.Optional", "java.util.Objects", "java.util.stream.Stream", "java.util.stream.Collectors",
       "java.util.function.Function", "java.util.function.Supplier"]


def jdk_names(javap):
    """Member names of the JDK types the engine uses: a name the JDK gives is no leak (compareTo, isBefore, stripTrailingZeros)."""
    return {n for n, _ in members(javap, "", JDK) if "$" not in n}


def main(gen, src, out):
    jar, cls, javap = classes(gen)
    jdk = jdk_names(javap)
    with zipfile.ZipFile(jar) as z:
        entries = [n[:-6].replace("/", ".") for n in z.namelist() if n.endswith(".class")]
    runs = cobol_runs(src)
    found = {}
    for abbr, fq in sorted(cls.items()):
        names = [e for e in entries if e == fq or e.startswith(fq + "$")]
        for name, kind in members(javap, jar, names):
            found.setdefault(name, set()).add(f"{abbr}.{kind}")
    rows = []
    for name in sorted(found):
        if "$" in name or name.startswith("lambda") or name in ("<init>", "<clinit>"):
            continue
        if len(name) < 8:
            status = "short"
        elif name in jdk:
            status = "jdk"
        elif re.fullmatch(r"[A-Z]?[a-z0-9]+|[A-Z0-9_]+", name):
            status = "word"
        elif name.lower() in runs:
            status = "cobol"
        else:
            status = "leak"
        rows.append((name, status, ",".join(sorted(found[name]))))
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        f.write("# Leak checker dictionary (tools/build_leak_dictionary.py). SME only: never part of a prompt.\n")
        for r in rows:
            f.write("\t".join(r) + "\n")
    counts = {}
    for _, s, _ in rows:
        counts[s] = counts.get(s, 0) + 1
    print(f"{len(cls)} classes, {len(rows)} names: {counts}")


if __name__ == "__main__":
    main(*sys.argv[1:4])
