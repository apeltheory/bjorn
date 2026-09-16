#!/usr/bin/env python3
"""Push a line to Bjorn as if it had been spoken.

The transcriber is whatever you like; this is the seam. Anything posted here
reaches the plugin and takes the same path as typed chat, so it still has to
start with his name and still obeys every rule typing does.

    python3 scripts/say-to-bjorn.py "Bjorn, chop wood"
    python3 scripts/say-to-bjorn.py            # read lines from stdin
"""
import json
import sys
import urllib.error
import urllib.request
from pathlib import Path

BRIDGE = 'http://127.0.0.1:8765/listen'
TOKEN = Path(__file__).resolve().parents[1] / 'runtime' / 'bridge.token'


def send(text, token):
    request = urllib.request.Request(
        BRIDGE,
        data=json.dumps({'text': text}).encode(),
        headers={'content-type': 'application/json', 'Authorization': 'Bearer ' + token})
    with urllib.request.urlopen(request, timeout=5) as response:
        return json.load(response)


def main():
    try:
        token = TOKEN.read_text().strip()
    except OSError:
        print(f'No bridge token at {TOKEN}. Start the planner first.', file=sys.stderr)
        return 1
    lines = [' '.join(sys.argv[1:])] if len(sys.argv) > 1 else sys.stdin
    for line in lines:
        line = line.strip()
        if not line:
            continue
        try:
            print(f'queued ({send(line, token)["queued"]} waiting): {line}')
        except urllib.error.HTTPError as error:
            print(f'refused ({error.code}): {line}', file=sys.stderr)
        except OSError as error:
            print(f'bridge unreachable: {error}', file=sys.stderr)
            return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
