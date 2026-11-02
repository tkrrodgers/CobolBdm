"""Builds a short human review sheet from a thinker run (lead tool; no original-Java input).

usage: python dt_review_sheet.py <thinker-dir> <proposals.json> <out.md>
Lists the units held in review, by unit (not by symbol id), in priority order: types, then the business programs'
methods, record instances and data items, then everything else as counts only. Each row gives the COBOL id, the
generator's name, the thinker's name, its confidence, its rationale and one verbatim quote, plus Approve and Rename
columns to fill in. An approved row enters the ledger as approval "review:<who>".
"""
import collections
import json
import os
import sys

tdir, prop, out = sys.argv[1], sys.argv[2], sys.argv[3]
wl = json.load(open(os.path.join(tdir, 'worklist.json'), encoding='utf-8'))
units = {u['unit']: u for u in wl['units']}
d = json.load(open(prop, encoding='utf-8'))
held = collections.OrderedDict()
for e in d['review']:
    held.setdefault(e['unit'], e)
raw = {}
for f in sorted(os.listdir(os.path.join(tdir, 'proposals'))):
    for u in json.load(open(os.path.join(tdir, 'proposals', f), encoding='utf-8'))['units']:
        raw[u['unit']] = u
BUSINESS = {'FSEM0100', 'KZLE0100'}


def esc(s):
    return (s or '').replace('|', '\\|').replace('\n', ' ')


def row(uid):
    u, p, e = units[uid], raw.get(uid, {}), held[uid]
    q = (p.get('evidence') or [{}])[0]
    name = p.get('name') or ('(keep)' if p.get('keep') else '(constants)' if p.get('constants') else '(parameters)' if p.get('parameters') else '')
    why = e.get('reviewReason') or ''
    return '| %s | %s | `%s` | **`%s`** | %.2f | %s%s | `%s` %s:%s | | |' % (
        uid, esc(', '.join(u['legacy'])[:40]), u['currentStem'] or '-', name, p.get('confidence', 0), esc(p.get('rationale', ''))[:160],
        (' (' + why + ')') if why else '', esc(q.get('quote', ''))[:60], os.path.basename(q.get('file', '')), q.get('line', ''))


groups = [
    ('Types (class, record, enum names)', lambda u: u['unitKind'] == 'type'),
    ('Business methods and parameters (job driver and calculation service)',
     lambda u: u['unitKind'] == 'method' and set(u['programs']) & BUSINESS),
    ('Record instances and state', lambda u: u['unitKind'] in ('record-instance', 'instance', 'state', 'predicate')),
    ('Business data items (fields; their getters and setters follow)', lambda u: u['unitKind'] == 'data' and u['programs'][0] in BUSINESS),
]
lines = ['# DT1 review sheet: kontor-zins-legacy names held for a human', '',
         'The thinker (%s, prompt %s) proposed these below the 0.7 auto-approval threshold, or they were held by a check. '
         'Mark **Approve** with your initials to accept the thinker\'s name, or write a better one under **Rename**. '
         'Approved rows enter the naming ledger as `review:<initials>`; the rename engine applies them by id, and '
         'behaviour is proven by the same gates. Evidence quotes are verbatim from the COBOL.' % (d['model'], d['promptVersion']), '']
done = set()
for title, pred in groups:
    sel = [uid for uid in held if pred(units[uid]) and uid not in done]
    if not sel:
        continue
    lines += ['## %s (%d)' % (title, len(sel)), '',
              '| Unit | COBOL | Generator | Thinker | Conf | Why | Evidence | Approve | Rename |', '|---|---|---|---|---|---|---|---|---|']
    lines += [row(uid) for uid in sel]
    lines.append('')
    done.update(sel)
rest = [uid for uid in held if uid not in done]
by = collections.Counter('%s / %s' % (units[u]['programs'][0], units[u]['unitKind']) for u in rest)
lines += ['## Everything else (%d units, counts only)' % len(rest), '',
          'Mostly scratch storage of the decimal library (FSJBDEC), named plainly at low confidence on purpose (caution C001).', '']
lines += ['- %s: %d' % kv for kv in sorted(by.items())]
open(out, 'w', encoding='utf-8', newline='\n').write('\n'.join(lines) + '\n')
print('held units', len(held), 'listed', len(done), 'counted', len(rest))
