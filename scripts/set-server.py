#!/usr/bin/env python3
"""Save the server he should join, keeping the password out of the terminal.

    python3 scripts/set-server.py                 # prompts for all three
    python3 scripts/set-server.py 10.0.0.5:2456   # address given, still prompts for the password
"""
import getpass
import os
import re
import sys
from pathlib import Path

PATH = Path(__file__).resolve().parents[1] / '.env'
ADDRESS = re.compile(r'^[A-Za-z0-9.\-]+:\d{1,5}$')


def put(lines, key, value):
    """Replace or append KEY=value, keeping every other line untouched."""
    lines = [line for line in lines if not line.startswith(key + '=')]
    lines.append(f'{key}={value}')
    return lines


def main():
    server = (sys.argv[1] if len(sys.argv) > 1 else input('Server address as ip:port: ')).strip()
    if not ADDRESS.match(server):
        raise SystemExit(f'{server!r} is not an ip:port address. Nothing was changed.\n'
                         'A world hosted from a game client has no address and must be joined by hand.')
    character = input('Character name [Bjorn]: ').strip() or 'Bjorn'
    password = getpass.getpass('Server password, or blank for none (it stays invisible): ').strip()
    # Valheim refuses a password shorter than five characters, so catch it here rather
    # than after a failed connection.
    if password and len(password) < 5:
        raise SystemExit('Valheim requires at least five characters. Nothing was changed.')

    lines = PATH.read_text().splitlines() if PATH.exists() else []
    for key, value in (('VALHEIM_SERVER', server), ('VALHEIM_CHARACTER', character),
                       ('VALHEIM_PASSWORD', password)):
        lines = put(lines, key, value)
    fd = os.open(PATH, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    os.fchmod(fd, 0o600)
    with os.fdopen(fd, 'w') as file:
        file.write('\n'.join(lines) + '\n')
    print(f'Saved. He will join {server} as {character}'
          f'{" with a password" if password else " with no password"}.')
    print('Start him with: ./scripts/bjorn.sh start')


if __name__ == '__main__':
    main()
