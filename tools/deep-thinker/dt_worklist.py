"""Builds the isolated thinker folder from a naming packet (deterministic view; adds no information).

usage: python dt_worklist.py <packet-dir> <thinker-dir>
Copies source/, scenarios/ and notebook/ verbatim, and writes worklist.json: the proposable symbols grouped into
naming units (one data item, paragraph, type, code enum, ...). A unit gets ONE proposed name; its members' names
are derived from it by keeping each member's suffix after the unit's base stem (dt_expand.py).
"""
import collections
import hashlib
import json
import os
import shutil
import sys

src, dst = sys.argv[1], sys.argv[2]
if os.path.exists(dst):
    shutil.rmtree(dst)
os.makedirs(dst)
for d in ('source', 'scenarios', 'notebook'):
    shutil.copytree(os.path.join(src, d), os.path.join(dst, d))
p = json.load(open(os.path.join(src, 'packet.json'), encoding='utf-8'))


def role(s):
    k = s['originKey']
    return k.split('/role/')[-1].split('/')[0] if '/role/' in k else s['kind']


def unit_of(s):
    r = role(s)
    base = s['originKey'].split('/role/')[0]
    if r in ('accessor', 'setter', 'field', 'component'):
        return base, 'data'
    if s['kind'] in ('class', 'enum', 'record'):
        return s['originKey'], 'type'
    return base, r


units = collections.OrderedDict()
for s in sorted((s for s in p['symbols'] if s.get('proposable')), key=lambda s: (s['originKey'], s['id'])):
    key, kind = unit_of(s)
    u = units.setdefault((key, kind), {'unit': None, 'unitKind': kind, 'originKey': key, 'programs': set(), 'legacy': set(),
                                       'owners': set(), 'members': []})
    u['programs'].update(s.get('programs') or [])
    u['legacy'].add(s['legacy'])
    u['owners'].add(s['owner'])
    u['members'].append({'id': s['id'], 'kind': s['kind'], 'role': role(s), 'current': s['current'], 'type': s.get('type')})

out = []
for i, ((key, kind), u) in enumerate(units.items(), 1):
    curr = sorted({m['current'] for m in u['members'] if m['kind'] not in ('parameter', 'constant')}, key=lambda c: (len(c), c))
    stem = curr[0] if curr else None
    if kind == 'data':
        bad = [m['current'] for m in u['members'] if not m['current'].startswith(stem)]
        assert not bad, (key, stem, bad)
    out.append({'unit': 'U%03d' % i, 'unitKind': kind, 'legacy': sorted(u['legacy']), 'programs': sorted(u['programs']),
                'owners': sorted(u['owners']), 'currentStem': stem, 'members': u['members']})

wl = {'schema': 'mlcpu.naming-worklist.v1', 'estate': p['estate'], 'packetSha256': p['packetSha256'],
      'unitRule': 'Give each unit ONE name (its stem). Members are renamed by replacing currentStem with your stem and keeping '
                  'their suffix (functionText -> calculationFunctionText). Method units: the stem names the method; name its '
                  'parameter members individually in "parameters". code-enum units: name each constant member in "constants".',
      'units': out}
text = json.dumps(wl, indent=1, ensure_ascii=False)
open(os.path.join(dst, 'worklist.json'), 'w', encoding='utf-8', newline='\n').write(text + '\n')
print('units', len(out), collections.Counter(u['unitKind'] for u in out), 'members', sum(len(u['members']) for u in out),
      'sha', hashlib.sha256(text.encode()).hexdigest()[:12])
