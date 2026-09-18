#!/usr/bin/env python3
"""Prompt privately for an API key and update the local .env.

    python3 scripts/set-key.py            # the Jev (TypeSafe) planner key
    python3 scripts/set-key.py anthropic  # the Anthropic key he speaks with
"""
import getpass
import os
import re
import sys
from pathlib import Path

KEYS = {
    'typesafe': ('TYPESAFE_API_KEY', 'TypeSafe (Jev)', None),
    'anthropic': ('ANTHROPIC_API_KEY', 'Anthropic', re.compile(r'sk-ant-[A-Za-z0-9_-]+')),
}
which = (sys.argv[1] if len(sys.argv) > 1 else 'typesafe').lower()
if which not in KEYS:
    raise SystemExit(f'Say which key: {" or ".join(KEYS)}. Nothing was changed.')
name, label, shape = KEYS[which]

path = Path(__file__).resolve().parents[1] / '.env'
key = getpass.getpass(f'Paste your {label} API key, then press Enter (it stays invisible): ').strip()
# TypeSafe has not published a key prefix, so that one is only checked for the
# shape of a credential rather than against a pattern that might reject a real key.
if not key or re.search(r'\s', key) or len(key) < 16:
    raise SystemExit(f'That does not look like an API key. Nothing was changed.')
if shape and not shape.fullmatch(key):
    raise SystemExit(f'That does not look like an {label} API key. Nothing was changed.')
lines = path.read_text().splitlines() if path.exists() else []
lines = [line for line in lines if not line.startswith(name + '=')]
lines.insert(0, name + '=' + key)
fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
os.fchmod(fd, 0o600)
with os.fdopen(fd, 'w') as file:
    file.write('\n'.join(lines) + '\n')
print(f'{label} key saved privately. You can tell me: saved.')
