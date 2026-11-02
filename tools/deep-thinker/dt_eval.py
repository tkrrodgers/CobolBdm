"""Lead-only evaluation of the thinker's names against the held-out original Java (answer key; never shown to the thinker).

usage: python dt_eval.py <thinker-dir> <original-java-root> <out.md>
With no structural type pairs on this estate (DT-deep-thinker.md 2.6), names are scored by vocabulary:
- vocab hit: the share of a name's camel tokens that occur among the original's identifier tokens;
- per unit kind, for the thinker's stem against the generator's current stem (the baseline);
- the type names side by side with the original's type names, for a human read.
Tokens are lower-cased camel parts; digits and one-letter tokens are ignored.
"""
import collections
import glob
import json
import os
import re
import sys

tdir, orig, outmd = sys.argv[1], sys.argv[2], sys.argv[3]
CAMEL = re.compile(r'(?<=[a-z0-9])(?=[A-Z])|_|(?<=[A-Z])(?=[A-Z][a-z])')


def toks(n):
    return [t.lower() for t in CAMEL.split(n or '') if len(t) > 1 and not t.isdigit()]


ident = re.compile(r'\b(?:class|record|enum|interface)\s+([A-Z]\w*)|\b([a-z]\w*)\s*\(|\b(?:[A-Z]\w*(?:<[^>]*>)?|int|long|boolean|double)\s+([a-z]\w*)\s*[;=,)]')
types, vocab = set(), collections.Counter()
for f in glob.glob(os.path.join(orig, '**', '*.java'), recursive=True):
    src = open(f, encoding='utf-8', errors='replace').read()
    src = re.sub(r'//[^\n]*', ' ', re.sub(r'/\*.*?\*/', ' ', src, flags=re.S))
    for m in ident.finditer(src):
        n = m.group(1) or m.group(2) or m.group(3)
        if m.group(1):
            types.add(n)
        for t in toks(n):
            vocab[t] += 1
skip = {'get', 'set', 'is', 'to', 'of', 'with', 'new', 'string', 'equals', 'hash', 'code', 'value', 'values', 'this', 'return'}
V = {t for t in vocab if t not in skip}

wl = json.load(open(os.path.join(tdir, 'worklist.json'), encoding='utf-8'))
units = {u['unit']: u for u in wl['units']}
got = {}
for f in sorted(glob.glob(os.path.join(tdir, 'proposals', '*.json'))):
    for u in json.load(open(f, encoding='utf-8')).get('units', []):
        got[u['unit']] = u


def hit(n):
    ts = [t for t in toks(n) if t not in skip]
    return None if not ts else sum(t in V for t in ts) / len(ts)


by = collections.defaultdict(lambda: [0, 0.0, 0.0])
rows = []
for uid, u in units.items():
    p = got.get(uid)
    if not p or not p.get('name') or u['currentStem'] is None:
        continue
    a, b = hit(p['name']), hit(u['currentStem'])
    if a is None or b is None:
        continue
    k = by[u['unitKind']]
    k[0] += 1
    k[1] += a
    k[2] += b
    if u['unitKind'] == 'type':
        rows.append((u['currentStem'], p['name'], p.get('confidence'), round(a, 2)))
lines = ['# Thinker names vs the held-out original (lead only)', '',
         'Original: `%s`. %d original types, %d vocabulary tokens.' % (orig, len(types), len(V)), '',
         '| Unit kind | Units | Vocab hit, thinker | Vocab hit, generator (baseline) |', '|---|---|---|---|']
for k, (n, a, b) in sorted(by.items()):
    lines.append('| %s | %d | %.0f%% | %.0f%% |' % (k, n, 100 * a / n, 100 * b / n))
tn = sum(v[0] for v in by.values())
lines.append('| **all** | %d | %.0f%% | %.0f%% |' % (tn, 100 * sum(v[1] for v in by.values()) / max(tn, 1),
                                                    100 * sum(v[2] for v in by.values()) / max(tn, 1)))
# concept recall: an original identifier is recovered when some name's token set contains all of its tokens
concepts = set()
for f in glob.glob(os.path.join(orig, '**', '*.java'), recursive=True):
    src = re.sub(r'//[^\n]*', ' ', re.sub(r'/\*.*?\*/', ' ', open(f, encoding='utf-8', errors='replace').read(), flags=re.S))
    for m in re.finditer(r'\brecord\s+([A-Z]\w*)\s*\(([^)]*)\)', src):
        concepts.add(m.group(1))
        for comp in m.group(2).split(','):
            if comp.strip():
                concepts.add(comp.split()[-1])
    for m in re.finditer(r'\b(?:class|enum|interface)\s+([A-Z]\w*)', src):
        concepts.add(m.group(1))
    for m in re.finditer(r'\b(?:public|private|protected|static)[^=;{(]*?\b([a-z]\w*)\s*\(', src):
        concepts.add(m.group(1))
concepts -= {'toString', 'equals', 'hashCode', 'main'}
cskip = skip | {'is', 'of', 'after', 'require', 'in', 'same'}


def ctoks(n):
    return {t for t in toks(n) if t not in cskip}


thinker_names = set()
gen_names = set()
for uid, u in units.items():
    gen_names.update(m['current'] for m in u['members'])
    p = got.get(uid) or {}
    if p.get('keep'):
        thinker_names.update(m['current'] for m in u['members'])
    elif p.get('name') and u['currentStem'] is not None:
        thinker_names.update(p['name'] + m['current'][len(u['currentStem']):] for m in u['members']
                             if m['kind'] not in ('parameter', 'constant') and u['unitKind'] == 'data')
        thinker_names.add(p['name'])
    thinker_names.update((p.get('parameters') or {}).values())
    thinker_names.update((p.get('constants') or {}).values())


def recovered(names):
    sets = [ctoks(n) for n in names]
    hit = sorted(c for c in concepts if ctoks(c) and any(ctoks(c) <= s for s in sets))
    return hit


rt, rg = recovered(thinker_names), recovered(gen_names)
cc = [c for c in concepts if ctoks(c)]
lines += ['', '## Concept recall (original identifiers whose words all appear in one name)', '',
          '| Names | Recovered | Share |', '|---|---|---|',
          '| Thinker | %d/%d | %.0f%% |' % (len(rt), len(cc), 100 * len(rt) / max(len(cc), 1)),
          '| Generator (baseline) | %d/%d | %.0f%% |' % (len(rg), len(cc), 100 * len(rg) / max(len(cc), 1)), '',
          'Recovered by the thinker: ' + ', '.join(rt), '', 'Recovered by the generator: ' + ', '.join(rg), '',
          'Not recovered: ' + ', '.join(sorted(set(cc) - set(rt)))]
lines += ['', 'Original types: ' + ', '.join(sorted(types)), '', '| Generator type | Thinker type | Confidence | Vocab hit |', '|---|---|---|---|']
lines += ['| %s | %s | %s | %s |' % r for r in sorted(rows)]
open(outmd, 'w', encoding='utf-8', newline='\n').write('\n'.join(lines) + '\n')
print('\n'.join(lines))
