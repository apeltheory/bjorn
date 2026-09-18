#!/usr/bin/env python3
"""A stand-in for api.typesafe.ai, so the planner can be exercised without a key.

It is deliberately strict. The point is not to imitate Jev's judgement — it cannot
— but to hold the planner to the published contract, so that a request this serves
happily is one the real API should accept too. Anything off-contract comes back 422
with the same `detail: [{loc, msg, type}]` shape the real one uses.

The answers come from a fixture rather than from any cleverness here: each order in
`tests/fixtures/jev_orders.json` carries the answer Jev is imagined to give, and the
decision the planner should reach from it. That keeps the harness honest about what
it tests — everything downstream of Jev — and the fixture doubles as the thing to
diff a real response against when a key arrives.

    python3 scripts/fake-jev.py --port 8799        # serve until interrupted

`scripts/rehearse.py` starts one of these by itself; run it by hand only to point a
real planner at it:

    TYPESAFE_API_KEY=fake TYPESAFE_BASE_URL=http://127.0.0.1:8799 ./scripts/brain.sh
"""
import argparse
import json
import re
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
FIXTURES = ROOT / 'tests/fixtures/jev_orders.json'
MAX_CRITERIA = 255  # Jev picks between at most this many choices.


def fault(where, message, kind='value_error'):
    return {'loc': ['body'] + list(where), 'msg': message, 'type': kind}


def check(body):
    """The request rules taken from the published schema. Returns a list of faults."""
    faults = []
    if not isinstance(body, dict):
        return [fault([], 'Input should be a valid dictionary', 'model_attributes_type')]
    if not isinstance(body.get('model'), str) or not body.get('model'):
        faults.append(fault(['model'], 'Field required', 'missing'))
    if not isinstance(body.get('state'), (str, dict, list)):
        faults.append(fault(['state'], 'Field required', 'missing'))
    questions = body.get('questions')
    if not isinstance(questions, dict) or not questions:
        faults.append(fault(['questions'], 'Dictionary should have at least 1 item', 'too_short'))
        return faults
    for name, question in questions.items():
        at = ['questions', name]
        if not isinstance(question, dict):
            faults.append(fault(at, 'Input should be a valid dictionary', 'model_attributes_type')); continue
        kind = question.get('type')
        if kind not in ('noul', 'choice', 'score'):
            faults.append(fault(at + ['type'], "Input should be 'noul', 'choice' or 'score'", 'union_tag_invalid')); continue
        criteria = question.get('criteria')
        if kind == 'choice':
            if not isinstance(criteria, dict) or not criteria:
                faults.append(fault(at + [kind, 'criteria'], 'Field required', 'missing'))
            elif len(criteria) > MAX_CRITERIA:
                faults.append(fault(at + [kind, 'criteria'], f'Dictionary should have at most {MAX_CRITERIA} items', 'too_long'))
        elif kind == 'score':
            if not isinstance(criteria, list) or not criteria:
                faults.append(fault(at + [kind, 'criteria'], 'List should have at least 1 item', 'too_short'))
        elif kind == 'noul' and criteria is not None:
            if not isinstance(criteria, dict) or set(criteria) - {'true', 'false'}:
                faults.append(fault(at + [kind, 'criteria'], 'Unexpected keyword argument', 'unexpected_keyword_argument'))
        for field in ('instructions', 'criteria'):
            if field in question and question[field] is not None and not isinstance(question[field], (str, dict, list)):
                faults.append(fault(at + [kind, field], 'Input should be a valid string, dictionary or list', 'union'))
    return faults


def answer_for(order, fixtures):
    """The canned answer for this order, matched the way the fixture states it."""
    text = order.strip().lower()
    for case in fixtures:
        if case['order'].strip().lower() == text:
            return case['jev']
    for case in fixtures:
        pattern = case.get('matches')
        if pattern and re.search(pattern, text):
            return case['jev']
    return None


def envelope(given, questions):
    """Dress a fixture's answer in the response shape the API documents."""
    answers = {}
    for name, question in questions.items():
        kind = question.get('type')
        if name not in given:
            continue
        value = given[name]
        if kind == 'choice':
            criteria = list(question.get('criteria') or {})
            choice = value if isinstance(value, str) else value.get('choice')
            confidence = 0.95 if isinstance(value, str) else float(value.get('confidence', 0.95))
            spread = round((1.0 - confidence) / max(len(criteria) - 1, 1), 6)
            answers[name] = {'type': 'choice', 'choice': choice, 'confidence': confidence,
                             'probabilities': {c: (confidence if c == choice else spread) for c in criteria}}
        elif kind == 'noul':
            answers[name] = {'type': 'noul', 'noul': float(value)}
        elif kind == 'score':
            legend = {str(i): c for i, c in enumerate(question.get('criteria') or [])}
            answers[name] = {'type': 'score', 'score': float(value), 'confidence': 0.9, 'legend': legend,
                             'probabilities': {k: 1.0 / max(len(legend), 1) for k in legend}}
    return {'model': 'jev-latest', 'answers': answers,
            'usage': {'input_tokens': 120, 'output_tokens': len(answers) * 4}}


def serve(port, fixtures, quiet=True):
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_):
            if not quiet:
                BaseHTTPRequestHandler.log_message(self, *_)

        def send_json(self, status, payload):
            data = json.dumps(payload).encode()
            self.send_response(status)
            self.send_header('Content-Type', 'application/json')
            self.send_header('Content-Length', str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def do_GET(self):
            if self.path != '/v1/models':
                self.send_json(404, {'detail': 'Not Found'}); return
            if not self.headers.get('Authorization', '').startswith('Bearer '):
                self.send_json(401, {'detail': 'Missing bearer token'}); return
            self.send_json(200, {'models': [{'name': 'jev-latest', 'description': 'Stand-in for the System One model.',
                                             'release_date': '2026-09-15'}]})

        def do_POST(self):
            if self.path != '/v1/systemone':
                self.send_json(404, {'detail': 'Not Found'}); return
            # The real API takes a bearer token and nothing else.
            if not self.headers.get('Authorization', '').startswith('Bearer '):
                self.send_json(401, {'detail': 'Missing bearer token'}); return
            if 'application/json' not in (self.headers.get('Content-Type') or ''):
                self.send_json(415, {'detail': 'Unsupported Media Type'}); return
            try:
                body = json.loads(self.rfile.read(int(self.headers.get('Content-Length', '0')) or 0))
            except ValueError:
                self.send_json(400, {'detail': 'Malformed JSON'}); return
            faults = check(body)
            if faults:
                self.send_json(422, {'detail': faults}); return
            state = body['state']
            # The action request carries `order`; the smaller gate request carries
            # `message`. Either way it is the line someone spoke.
            order = (state.get('order') or state.get('message')) if isinstance(state, dict) else state
            given = answer_for(order if isinstance(order, str) else json.dumps(state), fixtures)
            if given is None:
                # Not a refusal the real API would make; it means the corpus has a
                # hole, and rehearse.py reports it as one rather than inventing a pick.
                self.send_json(404, {'detail': f'No fixture for {order!r}'}); return
            self.send_json(200, envelope(given, body['questions']))

    return ThreadingHTTPServer(('127.0.0.1', port), Handler)


def load_fixtures(path=FIXTURES):
    return json.loads(Path(path).read_text())['orders']


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port', type=int, default=8799)
    parser.add_argument('--loud', action='store_true', help='log each request')
    options = parser.parse_args()
    server = serve(options.port, load_fixtures(), quiet=not options.loud)
    print(f'fake Jev on http://127.0.0.1:{options.port} (POST /v1/systemone, GET /v1/models)', flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
