"""Make one real planner request and say which model answered it."""
import json
import os
import sys
from pathlib import Path
from urllib.error import HTTPError
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from brain.server import Planner

WANTED = ('TYPESAFE_API_KEY=', 'TYPESAFE_MODEL=', 'TYPESAFE_BASE_URL=', 'MAX_JEV_CALLS=',
          'JEV_MIN_CONFIDENCE=', 'ANTHROPIC_API_KEY=', 'ANTHROPIC_MODEL=', 'MAX_API_CALLS=')
for line in (Path(__file__).resolve().parents[1] / '.env').read_text().splitlines():
    if line.startswith(WANTED):
        name, value = line.split('=', 1)
        os.environ[name] = value.strip()

planner = Planner()
print('Planner mode:', planner.mode)
if planner.mode == 'offline':
    print('No key is set, so nothing was called. Direct orders still work.')
    print('Set one with: python3 scripts/set-key.py')
    sys.exit(0)
try:
    result = planner.decide('Greet your travelling companion briefly.',
                            {'health': 100, 'task': 'stay', 'inventory': []})
    print('Connection successful. Action:', result['action'])
except HTTPError as error:
    endpoint = 'TypeSafe' if 'typesafe' in getattr(error, 'url', '') else 'Anthropic'
    print(f'{endpoint} request failed. HTTP status:', error.code)
    if error.code == 401:
        print('That key was refused. Check it with: python3 scripts/set-key.py')
    try:
        body = json.load(error)
        detail = body.get('error', body.get('detail', {}))
        if isinstance(detail, dict):
            print('Error type:', detail.get('type', 'unknown'))
        message = detail.get('message', '') if isinstance(detail, dict) else str(detail)
        for key in (os.environ.get('TYPESAFE_API_KEY', ''), os.environ.get('ANTHROPIC_API_KEY', '')):
            if key:
                message = message.replace(key, '[redacted]')
        print('Message:', message)
    except Exception:
        pass
    sys.exit(1)
except Exception as error:
    print('Connection check failed:', type(error).__name__)
    sys.exit(1)
