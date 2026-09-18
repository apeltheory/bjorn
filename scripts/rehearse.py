#!/usr/bin/env python3
"""Run the order corpus through the real planner, against a stand-in Jev.

This is the dress rehearsal for the Jev path: a real `Planner`, real HTTP, real
JSON on the wire, and the stand-in from `fake-jev.py` holding the request to the
published contract at the other end. What it proves is everything downstream of
the model — that the request is well formed, that the named thing is read off the
sentence correctly, that an unsure pick becomes talk, that the chest guard bites,
and that the reply table never claims work is finished. What it cannot prove is
Jev's judgement; the fixture supplies that.

    python3 scripts/rehearse.py           # one line per order, failures marked
    python3 scripts/rehearse.py --quiet   # only the failures and the tally

Exits non-zero if any order lands somewhere the corpus did not expect, so it can
sit in the build checks beside the unit tests.
"""
import argparse
import json
import os
import sys
import threading
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT / 'scripts'))

import importlib.util
spec = importlib.util.spec_from_file_location('fake_jev', ROOT / 'scripts/fake-jev.py')
fake_jev = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fake_jev)


def rehearse(quiet=False):
    corpus = fake_jev.load_fixtures()
    cases = [case for case in corpus if case.get('order') != '__fallback__']
    server = fake_jev.serve(0, corpus)
    port = server.server_address[1]
    threading.Thread(target=server.serve_forever, daemon=True).start()

    # A real planner, pointed at the stand-in. No Anthropic key: the talk path has
    # to stand on its canned lines, which is also how it behaves with the budget spent.
    environment = {'TYPESAFE_API_KEY': 'rehearsal', 'TYPESAFE_BASE_URL': f'http://127.0.0.1:{port}',
                   'ANTHROPIC_API_KEY': '', 'MAX_JEV_CALLS': str(len(cases) + 10)}
    previous = {name: os.environ.get(name) for name in environment}
    os.environ.update(environment)
    try:
        from brain.server import Planner
        planner = Planner()
        state = {'health': 87, 'maxHealth': 100, 'task': 'idle', 'biome': 'BlackForest',
                 'haveBed': True, 'places': ['home', 'the mine'], 'chestNearby': True,
                 'stationNearby': False, 'inventory': [{'name': 'Wood', 'count': 42}]}
        failures = []
        for case in cases:
            order, want = case['order'], case['expect']
            try:
                got = planner.decide(order, state)
            except Exception as error:                      # noqa: BLE001 - reported, not swallowed
                failures.append((order, f'{type(error).__name__}: {error}'))
                print(f'  FAIL  {order!r}\n        raised {type(error).__name__}: {error}')
                continue
            wrong = [f'{field}={got.get(field)!r} want {want[field]!r}'
                     for field in ('action', 'item', 'reply') if field in want and got.get(field) != want[field]]
            if wrong:
                failures.append((order, '; '.join(wrong)))
                print(f'  FAIL  {order!r}\n        ' + '\n        '.join(wrong))
            elif not quiet:
                shown = got['reply'][:48] + ('…' if len(got['reply']) > 48 else '')
                print(f'  ok    {order[:44]:<44}  {got["action"]:<14} {got["item"][:22]:<22} {shown}')
        return cases, failures
    finally:
        server.shutdown()
        server.server_close()
        for name, value in previous.items():
            if value is None: os.environ.pop(name, None)
            else: os.environ[name] = value


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--quiet', action='store_true', help='only print failures')
    options = parser.parse_args()
    cases, failures = rehearse(options.quiet)
    print(f'\n{len(cases)} orders rehearsed, {len(failures)} unexpected')
    if failures:
        print('\nThe planner and the corpus disagree. Either the planner is wrong, or the')
        print('corpus is: decide which before changing either.')
    sys.exit(1 if failures else 0)
