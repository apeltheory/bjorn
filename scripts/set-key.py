#!/usr/bin/env python3
"""Prompt privately for the Anthropic key and update the local .env."""
import getpass
import os
import re
from pathlib import Path

path = Path(__file__).resolve().parents[1] / '.env'
key = getpass.getpass('Paste your Anthropic API key, then press Enter (it stays invisible): ').strip()
if not re.fullmatch(r'sk-ant-[A-Za-z0-9_-]+', key):
    raise SystemExit('That does not look like an Anthropic API key. Nothing was changed.')
lines = path.read_text().splitlines() if path.exists() else []
lines = [line for line in lines if not line.startswith('ANTHROPIC_API_KEY=')]
lines.insert(0, 'ANTHROPIC_API_KEY=' + key)
fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
os.fchmod(fd, 0o600)
with os.fdopen(fd, 'w') as file:
    file.write('\n'.join(lines) + '\n')
print('Key saved privately. You can tell me: saved.')
