import json
import os
import sys
from pathlib import Path
from urllib.error import HTTPError, URLError
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from brain.server import Planner
for line in (Path(__file__).resolve().parents[1] / '.env').read_text().splitlines():
    if line.startswith(('ANTHROPIC_API_KEY=', 'ANTHROPIC_MODEL=', 'MAX_API_CALLS=')):
        name, value = line.split('=', 1)
        os.environ[name] = value.strip()
try:
    result = Planner().decide('Greet your travelling companion briefly.', {'health': 100, 'task': 'stay', 'inventory': []})
    print('Anthropic connection successful. Action:', result['action'])
except HTTPError as error:
    print('Anthropic request failed. HTTP status:', error.code)
    try:
        detail=json.load(error).get('error', {})
        print('Error type:', detail.get('type', 'unknown'))
        message=detail.get('message', '')
        key=os.environ.get('ANTHROPIC_API_KEY', '')
        print('Message:', message.replace(key, '[redacted]') if key else message)
    except Exception:
        pass
    sys.exit(1)
except Exception as error:
    print('Connection check failed:', type(error).__name__)
    sys.exit(1)
