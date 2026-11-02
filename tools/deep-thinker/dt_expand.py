"""Deterministic pre-check and expansion of the thinker's unit proposals (lead tool, DT-deep-thinker.md).

usage: python dt_expand.py <thinker-dir> <packet-dir> <out-dir> [<unit-packet-dir>]
Reads <thinker-dir>/worklist.json and proposals/*.json. Checks completeness, verbatim evidence, identifier validity
and conventions, and collisions (against the full packet inventory, locked symbols included). Writes:
  proposals.json  mlcpu.naming-proposals.v1, one entry per symbol id, derived from its unit
  precheck.json   every finding, per unit
  precheck.md     summary
A unit fails closed: any finding keeps all of its members out of proposals.json (they appear in precheck as rejected).
"""
import collections
import glob
import json
import os
import re
import sys

tdir, pdir, odir = sys.argv[1], sys.argv[2], sys.argv[3]
os.makedirs(odir, exist_ok=True)
wl = json.load(open(os.path.join(tdir, 'worklist.json'), encoding='utf-8'))
packet = json.load(open(os.path.join(pdir, 'packet.json'), encoding='utf-8'))
units = {u['unit']: u for u in wl['units']}
THRESHOLD = 0.7
JAVA_KW = set('''abstract assert boolean break byte case catch char class const continue default do double else enum extends
final finally float for goto if implements import instanceof int interface long native new package private protected public
return short static strictfp super switch synchronized this throw throws transient try void volatile while true false null
var record yield sealed permits'''.split())
# simple names of JDK types the generated estates use or import: a domain type with one of these names shadows it
JDK_TYPES = set('''String Integer Long Short Byte Double Float Boolean Character Number Object Record Enum Math StrictMath System
Runtime Thread Process Class Iterable Comparable CharSequence StringBuilder Exception RuntimeException Error Throwable Override
Objects Arrays Collections List Map Set Optional Iterator Collection Deque Queue Stream Path Paths Files BigDecimal BigInteger
MathContext RoundingMode LocalDate LocalDateTime LocalTime Instant Duration Period Year Month YearMonth DayOfWeek Clock
ChronoUnit Pattern Matcher Charset StandardCharsets ByteBuffer IOException UncheckedIOException ArithmeticException'''.split())
UPPER_CAMEL = re.compile(r'^[A-Z][A-Za-z0-9]*$')
LOWER_CAMEL = re.compile(r'^[a-z][A-Za-z0-9]*$')
UPPER_SNAKE = re.compile(r'^[A-Z][A-Z0-9]*(_[A-Z0-9]+)*$')

files_cache = {}
# optional 4th argument: a packet with generator-emitted canonical units (unitKey per symbol, same ids)
canon = None
completion = {}
if len(sys.argv) > 4:
    up = json.load(open(os.path.join(sys.argv[4], 'packet.json'), encoding='utf-8'))
    canon = {x['id']: x['unitKey'] for x in up['symbols'] if x.get('proposable')}
    assert set(canon) == {x['id'] for x in packet['symbols'] if x.get('proposable')}, 'unit packet proposable ids differ'


def line_text(rel, n):
    path = os.path.normpath(os.path.join(tdir, rel))
    if not path.startswith(os.path.normpath(tdir)) or not os.path.isfile(path):
        return None
    if path not in files_cache:
        files_cache[path] = open(path, encoding='utf-8', errors='replace').read().splitlines()
    ls = files_cache[path]
    return ls[n - 1] if isinstance(n, int) and 1 <= n <= len(ls) else None


def check_evidence(ev):
    bad = []
    if not ev:
        return ['no evidence']
    ok = 0
    for e in ev:
        t = line_text(e.get('file', ''), e.get('line'))
        q = e.get('quote', '')
        if t is None:
            bad.append('evidence %s:%s does not exist' % (e.get('file'), e.get('line')))
        elif not q or q not in t:
            bad.append('quote not verbatim at %s:%s: %r' % (e.get('file'), e.get('line'), q[:60]))
        elif len(q.split()) > 15:
            bad.append('quote longer than 15 words')
        else:
            ok += 1
    return [] if ok and not bad else bad


def conv_ok(kind, name):
    if name in JAVA_KW:
        return False
    if kind == 'type':
        return bool(UPPER_CAMEL.match(name))
    if kind == 'constant':
        return bool(UPPER_SNAKE.match(name))
    return bool(LOWER_CAMEL.match(name))


got = {}
dups = []
model = set()
notes = []
for f in sorted(glob.glob(os.path.join(tdir, 'proposals', '*.json'))):
    d = json.load(open(f, encoding='utf-8'))
    model.add(d.get('model'))
    if d.get('packetSha256') != wl['packetSha256']:
        dups.append('%s: packetSha256 mismatch' % os.path.basename(f))
    notes.extend(dict(n, file=os.path.basename(f)) for n in d.get('notes', []))
    for u in d.get('units', []):
        if u['unit'] in got:
            dups.append('%s twice' % u['unit'])
        got[u['unit']] = u

findings = collections.OrderedDict()
renames = {}  # symbol id -> (new name, unit)
for uid, u in units.items():
    p = got.get(uid)
    f = []
    if p is None:
        findings[uid] = ['missing']
        continue
    if p['unit'] not in units:
        f.append('unknown unit')
    f += check_evidence(p.get('evidence'))
    conf = p.get('confidence')
    if not isinstance(conf, (int, float)) or not 0 <= conf <= 1:
        f.append('confidence missing or out of range')
    keep = p.get('keep') is True
    name = p.get('name')
    kind = u['unitKind']
    if not keep and kind not in ('code-enum',) and u['currentStem'] is not None and not name:
        f.append('no name and not keep')
    planned = {}
    stemmed = [m for m in u['members'] if m['kind'] not in ('parameter', 'constant')]
    if kind != 'data' and len(stemmed) > 1:
        # suffix derivation is proven only for data units (frozen unit model); a merged derived-role unit goes to review
        f.append('heterogeneous non-data unit (%d named members): review' % len(stemmed))
    if name and u['currentStem'] is not None and not keep:
        if not conv_ok('type' if kind == 'type' else 'member', name):
            f.append('name %r breaks the %s convention' % (name, kind))
        if kind == 'type' and name in JDK_TYPES:
            f.append('type name %r shadows a JDK type' % name)
        for m in stemmed:
            planned[m['id']] = name + m['current'][len(u['currentStem']):]
    for mid, nm in (p.get('parameters') or {}).items():
        mm = next((m for m in u['members'] if m['id'] == mid and m['kind'] == 'parameter'), None)
        if mm is None:
            f.append('parameters: %s is not a parameter of this unit' % mid[:12])
        elif not conv_ok('member', nm):
            f.append('parameter name %r breaks convention' % nm)
        else:
            planned[mid] = nm
    for mid, nm in (p.get('constants') or {}).items():
        mm = next((m for m in u['members'] if m['id'] == mid and m['kind'] == 'constant'), None)
        if mm is None:
            f.append('constants: %s is not a constant of this unit' % mid[:12])
        elif not conv_ok('constant', nm):
            f.append('constant name %r breaks convention' % nm)
        else:
            planned[mid] = nm
    findings[uid] = f
    if not f:
        cur = {m['id']: m['current'] for m in u['members']}
        for mid, nm in planned.items():
            if nm != cur[mid]:  # a proposal equal to the current name is a no-op, not a rename
                renames[mid] = (nm, uid)

# collisions against the full inventory (locked included), after renames
sym = {s['id']: s for s in packet['symbols']}


def role(s):
    k = s['originKey']
    return k.split('/role/')[-1].split('/')[0] if '/role/' in k else s['kind']


def ns(s):
    if s['kind'] in ('class', 'enum', 'record', 'interface'):
        return ('type', '*')
    if s['kind'] == 'parameter':
        return ('param', s['originKey'].rsplit('/parameter', 1)[0])
    if s['kind'] == 'method':
        r = role(s)
        return ('method:' + ('setter' if r == 'setter' else 'other'), s['owner'] + '|' + ','.join(s.get('parameters') or []))
    if s['kind'] == 'constant':
        return ('constant', s['owner'])
    return ('field', s['owner'])


space = collections.defaultdict(list)
for s in packet['symbols']:
    if s['kind'] == 'local':
        continue
    final = renames.get(s['id'], (s['current'], None))[0]
    space[(ns(s), final)].append(s['id'])
collided_units = set()
collisions = []
for (key, nm), ids in space.items():
    if len(ids) > 1 and any(i in renames for i in ids):
        us = sorted({renames[i][1] for i in ids if i in renames})
        was_dup_before = len({sym[i]['current'] for i in ids}) == 1 and not any(i in renames for i in ids)
        if was_dup_before:
            continue
        # overloads that already shared a name before renaming are not new collisions
        if len({sym[i]['current'] for i in ids}) == 1 and len(us) == 1:
            continue
        collisions.append({'namespace': key[0], 'name': nm, 'units': us, 'symbols': len(ids)})
        collided_units.update(us)
for uid in collided_units:
    findings[uid].append('collision after rename')
    for mid in [m for m, (n, u) in renames.items() if u == uid]:
        del renames[mid]

out = []
review = []


def entry(mid, nm, uid, **extra):
    p = got[uid]
    s = sym[mid]
    e = {'id': mid, 'kind': s['kind'], 'legacy': s['legacy'], 'current': s['current'], 'proposed': nm,
         'confidence': p['confidence'], 'rationale': p.get('rationale', ''), 'evidence': p.get('evidence', []),
         'notebook': p.get('notebook', []), 'unit': uid}
    e.update(extra)
    return e


if canon is None:
    for mid, (nm, uid) in sorted(renames.items(), key=lambda kv: (kv[1][1], kv[0])):
        (out if got[uid]['confidence'] >= THRESHOLD else review).append(entry(mid, nm, uid))
else:
    # canonical completion: every member of a canonical unit that has a rename is emitted, no-ops explicitly
    myunit = {m['id']: uid for uid, u in units.items() for m in u['members']}
    rejected = {uid for uid, f in findings.items() if f}
    members_of = collections.defaultdict(list)
    for i, k in canon.items():
        members_of[k].append(i)
    for k in sorted({canon[i] for i in renames}):
        ids = sorted(members_of[k])
        bad = sorted({myunit[i] for i in ids if myunit[i] in rejected})
        es = []
        for i in ids:
            if i in renames:
                es.append(entry(i, renames[i][0], renames[i][1], canonicalUnit=k))
            else:
                es.append(entry(i, sym[i]['current'], myunit[i], canonicalUnit=k, noOp=True))
        if bad:
            for e in es:
                e.update(reviewOnly=True, reviewReason='canonical unit contains a pre-check rejected member (%s)' % ','.join(bad))
            review.extend(es)
        elif min(e['confidence'] for e in es) >= THRESHOLD:
            out.extend(es)
        else:
            review.extend(es)
    completion['touchedCanonicalUnits'] = len({canon[i] for i in renames})
    completion['noOpEntries'] = sum(1 for e in out + review if e.get('noOp'))
# merged derived-role units: per-id reviewOnly entries with the thinker's raw answer; never applied automatically
already = {e['id'] for e in review}
for uid, f in findings.items():
    if not (f and all(x.startswith('heterogeneous') for x in f) and uid in got):
        continue
    if any(m['id'] in already for m in units[uid]['members']):
        continue
    p = got[uid]
    for m in units[uid]['members']:
        s = sym[m['id']]
        review.append({'id': m['id'], 'kind': s['kind'], 'legacy': s['legacy'], 'current': s['current'],
                       'proposed': None, 'unitProposal': p.get('name'), 'confidence': p.get('confidence'),
                       'rationale': p.get('rationale', ''), 'evidence': p.get('evidence', []), 'notebook': p.get('notebook', []),
                       'unit': uid, 'reviewOnly': True, 'reviewReason': 'merged derived-role unit; per-member names needed'})
import datetime
stamps = [os.path.getmtime(f) for f in glob.glob(os.path.join(tdir, 'proposals', '*.json'))]
created = datetime.datetime.fromtimestamp(max(stamps), datetime.timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ') if stamps else None
models = sorted(m for m in model if m)
doc = {'schema': 'mlcpu.naming-proposals.v1', 'estate': wl['estate'], 'packetSha256': wl['packetSha256'],
       'model': models[0] if len(models) == 1 else ','.join(models), 'promptVersion': 'naming-1u', 'createdAt': created,
       'threshold': THRESHOLD, 'proposals': out, 'review': review, 'notes': notes}
json.dump(doc, open(os.path.join(odir, 'proposals.json'), 'w', encoding='utf-8', newline='\n'), indent=1, ensure_ascii=False)
json.dump({'findings': findings, 'collisions': collisions, 'fileErrors': dups},
          open(os.path.join(odir, 'precheck.json'), 'w', encoding='utf-8', newline='\n'), indent=1, ensure_ascii=False)

rej = {u: f for u, f in findings.items() if f}
kept = [u for u, p in got.items() if p.get('keep') is True and not findings.get(u)]
lines = ['# Thinker pre-check (lead, deterministic)', '',
         '| Measure | Value |', '|---|---|',
         '| Units | %d (answered %d) |' % (len(units), len(got)),
         '| Units accepted by pre-check | %d (kept as is: %d) |' % (len(units) - len(rej), len(kept)),
         '| Units rejected (fail closed) | %d |' % len(rej),
         '| Entries (renames + explicit no-ops), confidence >= %.1f (ledger candidates) | %d |' % (THRESHOLD, len(out)),
         '| Entries below threshold or held (review queue) | %d |' % len(review),
         '| Collisions | %d |' % len(collisions),
         '| Notes proposed | %d |' % len(notes),
         '| Canonical units touched / explicit no-op entries | %s / %s |' % (completion.get('touchedCanonicalUnits', '-'), completion.get('noOpEntries', '-')),
         '| File errors | %d |' % len(dups), '']
reasons = collections.Counter(re.sub(r"[:(].*", '', x).strip() for f in rej.values() for x in f)
lines += ['Rejection reasons: ' + ', '.join('%s %d' % kv for kv in reasons.most_common()), '']
open(os.path.join(odir, 'precheck.md'), 'w', encoding='utf-8', newline='\n').write('\n'.join(lines) + '\n')
print('\n'.join(lines))
