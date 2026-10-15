"""Host-lane helpers for ofbiz-host.sh (FE 9.1): make the mutant hole sets, and summarize OFBiz's JUnit XML.

  host_mutants.py make <package> <out dir>          one directory per mutant of host/mutants.json: the reference holes with
                                                   one literal edit; refuses a mutant whose 'find' is not exactly once in its file
  host_mutants.py junit <test-results dir> [out]    tests/failures/errors and each failing test's message, as JSON
  host_mutants.py score <package> <runs dir> <out>  killed / survived / invalid per mutant from each run's verdict and JUnit
  host_mutants.py gate <package> <reference junit.json> <mutants-result.json> <out>   the host gate (exit 1 when it fails)
  host_mutants.py regression <without dir> <with dir> <out>   G12: the same outcome for every test (exit 1 when not)
"""
import glob
import json
import os
import re
import shutil
import sys
import xml.etree.ElementTree as ET


def make(package, out):
    spec = json.load(open(os.path.join(package, 'host', 'mutants.json'), encoding='utf-8'))
    ref = os.path.join(package, spec['reference'])
    for m in spec['mutants']:
        d = os.path.join(out, m['id'])
        shutil.rmtree(d, ignore_errors=True)
        os.makedirs(d)
        for f in glob.glob(os.path.join(ref, '*.java')):
            shutil.copy(f, d)
        path = os.path.join(d, m['file'])
        text = open(path, encoding='utf-8').read()
        if text.count(m['find']) != 1:
            sys.exit(f"{m['id']}: 'find' occurs {text.count(m['find'])} times in {m['file']}")
        if m['replace'] == '':
            # a deletion takes its whole line (an indented empty line would be a checkstyle violation, not a mutant)
            text = re.sub(r'(?m)^[ \t]*' + re.escape(m['find']) + r'[ \t]*\n', '', text, count=1)
        else:
            text = text.replace(m['find'], m['replace'])
        open(path, 'w', encoding='utf-8', newline='\n').write(text)
        print(m['id'])


def junit(results):
    out = {'tests': 0, 'failures': 0, 'errors': 0, 'failed': []}
    for f in sorted(glob.glob(os.path.join(results, '*.xml'))):
        root = ET.parse(f).getroot()
        for ts in ([root] if root.tag == 'testsuite' else root.iter('testsuite')):
            for k in ('tests', 'failures', 'errors'):
                out[k] += int(ts.attrib.get(k, 0))
            for tc in ts.iter('testcase'):
                bad = tc.find('failure') if tc.find('failure') is not None else tc.find('error')
                if bad is not None:
                    msg = ((bad.attrib.get('message') or '') + '\n' + (bad.text or '')).strip()
                    out['failed'].append({'name': tc.attrib.get('name'), 'message': msg[:4000]})
    return out


def score(package, runs, out):
    spec = json.load(open(os.path.join(package, 'host', 'mutants.json'), encoding='utf-8'))
    rows = []
    for m in spec['mutants']:
        d = os.path.join(runs, m['id'])
        verdict = open(os.path.join(d, 'verdict.txt')).read().strip() if os.path.exists(os.path.join(d, 'verdict.txt')) else 'missing'
        if not verdict.startswith('tests'):
            rows.append({'id': m['id'], 'rule': m['rule'], 'status': 'invalid', 'verdict': verdict})
            continue
        j = junit(os.path.join(d, 'test-results'))
        killed = j['failures'] + j['errors'] > 0 or j['tests'] == 0
        rows.append({'id': m['id'], 'rule': m['rule'], 'status': 'killed' if killed else 'survived', 'tests': j['tests'],
                     'failed': [f['name'] for f in j['failed']]})
    result = {'schema': 'mlcpu.host-mutants-result.v1', 'mutants': len(rows),
              'killed': sum(r['status'] == 'killed' for r in rows),
              'survived': [r['id'] for r in rows if r['status'] == 'survived'],
              'invalid': [r['id'] for r in rows if r['status'] == 'invalid'], 'rows': rows}
    json.dump(result, open(out, 'w', encoding='utf-8', newline='\n'), indent=2)
    print(f"{result['killed']}/{result['mutants']} killed; survived {result['survived']}; invalid {result['invalid']}")


def gate(package, reference_junit, mutants_result, out):
    """The host gate: the reference holes pass every host scenario and every mutant fails one (none survives, none is invalid)."""
    import datetime
    ref = json.load(open(reference_junit, encoding='utf-8'))
    mut = json.load(open(mutants_result, encoding='utf-8'))
    per_rule = {}
    for r in mut['rows']:
        s = per_rule.setdefault(r['rule'], {'mutants': 0, 'killed': 0})
        s['mutants'] += 1
        s['killed'] += r['status'] == 'killed'
    ok = ref['tests'] > 0 and ref['failures'] + ref['errors'] == 0 and not mut['survived'] and not mut['invalid'] and mut['mutants'] > 0
    result = {'schema': 'mlcpu.host-gate.v1', 'checked': datetime.datetime.now().astimezone().isoformat(timespec='seconds'), 'pass': ok,
              'reference': {'tests': ref['tests'], 'failed': ref['failures'] + ref['errors'], 'failedTests': [f['name'] for f in ref['failed']]},
              'mutants': {'total': mut['mutants'], 'killed': mut['killed'], 'survived': mut['survived'], 'invalid': mut['invalid'], 'perRule': per_rule}}
    json.dump(result, open(out, 'w', encoding='utf-8', newline='\n'), indent=2)
    print(f"host gate {'PASSED' if ok else 'NOT PASSED'}: reference {ref['tests'] - ref['failures'] - ref['errors']}/{ref['tests']}; "
          f"mutants {mut['killed']}/{mut['mutants']} killed")
    return 0 if ok else 1


def outcomes(results):
    """(suite, class, test) -> pass / fail / error / skipped, from a directory of JUnit XML."""
    out = {}
    for f in sorted(glob.glob(os.path.join(results, '*.xml'))):
        root = ET.parse(f).getroot()
        for ts in ([root] if root.tag == 'testsuite' else root.iter('testsuite')):
            for tc in ts.iter('testcase'):
                key = (ts.attrib.get('name', ''), tc.attrib.get('classname', ''), tc.attrib.get('name', ''))
                out[key] = ('fail' if tc.find('failure') is not None else 'error' if tc.find('error') is not None
                            else 'skipped' if tc.find('skipped') is not None else 'pass')
    return out


def regression(without, with_, out):
    """G12: every test of the run without the plugin has the same outcome with it (the plugin's own suite is not run)."""
    import datetime
    a, b = outcomes(without), outcomes(with_)
    changed = [{'test': ' / '.join(k), 'without': a[k], 'with': b.get(k, 'missing')} for k in sorted(a) if b.get(k) != a[k]]
    added = [' / '.join(k) for k in sorted(b) if k not in a]
    ok = len(a) > 0 and not changed
    result = {'schema': 'mlcpu.host-regression.v1', 'checked': datetime.datetime.now().astimezone().isoformat(timespec='seconds'), 'pass': ok,
              'tests': len(a), 'passedWithout': sum(v == 'pass' for v in a.values()), 'passedWith': sum(v == 'pass' for v in b.values()),
              'changed': changed, 'onlyWith': added}
    json.dump(result, open(out, 'w', encoding='utf-8', newline='\n'), indent=2)
    print(f"G12 {'PASSED' if ok else 'NOT PASSED'}: {len(a)} tests; {len(changed)} changed outcome(s); "
          f"passed {result['passedWithout']} without the plugin, {result['passedWith']} with it")
    return 0 if ok else 1


if __name__ == '__main__':
    verb = sys.argv[1]
    if verb == 'make':
        make(sys.argv[2], sys.argv[3])
    elif verb == 'junit':
        r = junit(sys.argv[2])
        text = json.dumps(r, indent=2)
        if len(sys.argv) > 3:
            open(sys.argv[3], 'w', encoding='utf-8', newline='\n').write(text)
        else:
            print(text)
    elif verb == 'score':
        score(sys.argv[2], sys.argv[3], sys.argv[4])
    elif verb == 'gate':
        sys.exit(gate(sys.argv[2], sys.argv[3], sys.argv[4], sys.argv[5]))
    elif verb == 'regression':
        sys.exit(regression(sys.argv[2], sys.argv[3], sys.argv[4]))
    else:
        sys.exit(__doc__)
