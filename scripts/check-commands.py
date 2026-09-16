#!/usr/bin/env python3
"""Static checks on the plugin's chat dispatch, which nothing else can test offline.

Receive() is an ordered if-chain, so an earlier branch can make a later one
unreachable. This finds those, plus chat lines that would be cut at 180
characters and question-shaped phrases matched against the wrong variable.
"""
import re
import sys
from pathlib import Path

SOURCE = Path(__file__).resolve().parents[1] / 'plugin' / 'Companion.cs'
CAP = 180  # Say() truncates here.


def receive_body(text):
    start = text.index('internal void Receive(')
    end = text.index('\n    void Follow(', start)
    return text[start:end]


def branches(body):
    """Every dispatch test, in source order: (kind, variable, phrase, line)."""
    found = []
    base = body[:body.index('\n')].count('\n')
    for offset, line in enumerate(body.split('\n')):
        for match in re.finditer(r'Any\((simple|plain),([^)]*)\)', line):
            variable = match.group(1)
            for phrase in re.findall(r'"((?:[^"\\]|\\.)*)"', match.group(2)):
                found.append(('exact', variable, phrase, offset))
        for match in re.finditer(r'Prefixed\(simple, order, "((?:[^"\\]|\\.)*)"', line):
            found.append(('prefix', 'simple', match.group(1), offset))
    return found


def check_shadowing(items):
    problems = []
    for index, (kind, _, phrase, line) in enumerate(items):
        for earlier_kind, _, earlier, earlier_line in items[:index]:
            if earlier_kind != 'prefix':
                continue
            if phrase.startswith(earlier):
                problems.append(
                    f'line ~{line}: {kind} "{phrase}" is unreachable - '
                    f'prefix "{earlier}" at line ~{earlier_line} claims it first')
                break
    return problems


def check_duplicates(items):
    seen = {}
    problems = []
    for kind, variable, phrase, line in items:
        key = (kind, phrase)
        if key in seen:
            problems.append(f'line ~{line}: {kind} "{phrase}" repeats line ~{seen[key]}; the later one never runs')
        else:
            seen[key] = line
    return problems


def check_question_shape(items):
    """`simple` keeps a trailing '?'; only `plain` strips it."""
    openers = ('what', 'where', 'how', 'can you', 'are you', 'do you', 'who')
    return [f'line ~{line}: "{phrase}" reads as a question but is matched against `simple`, '
            f'so a trailing "?" defeats it - use `plain`'
            for kind, variable, phrase, line in items
            if kind == 'exact' and variable == 'simple' and phrase.startswith(openers)]


def check_say_length(text):
    problems = []
    for index, line in enumerate(text.split('\n'), 1):
        if 'Say(' not in line:
            continue
        literal = ''.join(re.findall(r'"((?:[^"\\]|\\.)*)"', line))
        literal = literal.replace('\\u2014', '-').replace('\\"', '"')
        if len(literal) > CAP:
            problems.append(f'line {index}: Say() literal is {len(literal)} chars, cut at {CAP}')
        elif len(literal) > CAP - 30 and '+' in line:
            problems.append(f'line {index}: Say() literal is {len(literal)} chars plus values - likely cut at {CAP}')
    return problems


def main():
    text = SOURCE.read_text()
    items = branches(receive_body(text))
    groups = [
        ('unreachable commands', check_shadowing(items)),
        ('duplicate phrases', check_duplicates(items)),
        ('question-shaped phrases', check_question_shape(items)),
        ('chat lines over the cap', check_say_length(text)),
    ]
    total = 0
    for name, problems in groups:
        print(f'{name}: {len(problems)}')
        for problem in problems:
            print(f'  {problem}')
        total += len(problems)
    print(f'\n{len(items)} dispatch branches checked, {total} problems')
    return 1 if total else 0


if __name__ == '__main__':
    sys.exit(main())
