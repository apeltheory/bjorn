"""Check a running local bridge without printing its token."""
import json
from pathlib import Path
from urllib.request import Request, urlopen
from urllib.error import HTTPError
root = Path(__file__).resolve().parents[1]
token = (root / 'runtime/bridge.token').read_text().strip()
body = json.dumps({'message': 'follow me', 'state': {}}).encode()
for auth, expected in [('invalid', 401), (token, 200)]:
    request = Request('http://127.0.0.1:8765/decide', data=body,
                      headers={'Authorization': 'Bearer ' + auth, 'Content-Type': 'application/json'})
    try:
        with urlopen(request, timeout=5) as response:
            assert response.status == expected
            assert json.load(response)['action'] == 'follow'
    except HTTPError as error:
        assert error.code == expected, error.code
print('PASS: unauthorized request rejected; authenticated follow request succeeded.')
