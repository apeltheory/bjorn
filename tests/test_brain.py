import json
import os
import unittest
from unittest.mock import patch
from brain.server import ACTIONS, Heard, Planner, offline, validate

class BrainTests(unittest.TestCase):
    def test_basic_orders(self):
        for message, action in [('follow me!', 'follow'), ('STOP', 'stay'), ('what are you carrying?', 'inventory')]:
            self.assertEqual(offline(message)['action'], action)

    def test_work_orders_survive_an_outage(self):
        for message, action in [('chop wood', 'chop'), ('mine', 'mine'), ('defend me', 'fight'),
                                ('take all', 'withdraw'), ('come here', 'come'), ('repair', 'repair')]:
            self.assertEqual(offline(message)['action'], action)

    def test_reports_leave_the_reply_to_the_game(self):
        for message in ['status', 'where are you', 'look around', 'inventory']:
            self.assertEqual(offline(message)['reply'], '')

    def test_unknown_offline_does_not_promise_execution(self):
        self.assertEqual(offline('build a hall')['action'], 'chat')

    def test_reject_unsupported_action(self):
        with self.assertRaises(ValueError): validate({'action': 'execute_shell', 'reply': 'yes'})

    def test_autonomy_orders_route(self):
        for message, action in [('guard the camp', 'guard'), ('patrol', 'guard'),
                                ('what places do you know', 'places')]:
            self.assertEqual(offline(message)['action'], action)

    def test_bring_is_in_the_contract(self):
        # The plugin chains fetch-then-deliver behind this one action, so the planner
        # must be allowed to choose it.
        self.assertIn('bring', ACTIONS)
        self.assertEqual(validate({'action': 'bring', 'reply': 'Aye.', 'item': 'wood'})['item'], 'wood')

    def test_natural_phrasings_reach_the_right_action(self):
        for message, action in [('repair your stuff', 'repair'), ('fix your gear', 'repair'),
                                ('eat up', 'eat'), ('eat something', 'eat'), ('dump it', 'pile')]:
            self.assertEqual(offline(message)['action'], action)

    def test_loose_phrasings_still_work_with_the_api_down(self):
        # These are answered locally by the plugin now, but the offline table is the
        # backstop when an order does reach a planner with no key or no budget left.
        for message, action in [('repair your axe', 'repair'), ('go chop down some trees', 'chop')]:
            self.assertEqual(offline(message)['action'], action)

    def test_offline_actions_are_all_in_the_contract(self):
        self.assertTrue({offline(m)['action'] for m in ['chop', 'mine', 'fight', 'craft a shield']} <= ACTIONS)

    def test_item_argument_is_kept_and_bounded(self):
        self.assertEqual(validate({'action': 'craft', 'reply': '', 'item': '20 wood arrows'})['item'], '20 wood arrows')
        self.assertEqual(len(validate({'action': 'deposit', 'reply': '', 'item': 'x'*300})['item']), 120)
        self.assertEqual(validate({'action': 'gather', 'reply': '', 'item': None})['item'], '')

    def test_sanitize_reply(self):
        self.assertEqual(validate({'action': 'chat', 'reply': '<b>Hello</b>\nfriend'})['reply'], 'Hello friend')
        self.assertEqual(len(validate({'action': 'chat', 'reply': 'x'*500})['reply']), 180)

    @patch.dict(os.environ, {'ANTHROPIC_API_KEY': 'test', 'MAX_API_CALLS': '1'})
    @patch('urllib.request.urlopen')
    def test_api_contract_and_cap(self, urlopen):
        urlopen.return_value.__enter__.return_value.read.return_value = json.dumps({'content': [{'type': 'text', 'text': '{"action":"follow","reply":"On my way."}'}]}).encode()
        planner = Planner()
        self.assertEqual(planner.decide('come along', {})['action'], 'follow')
        self.assertEqual(planner.decide('come along', {})['action'], 'chat')
        self.assertEqual(urlopen.call_count, 1)
        self.assertEqual(planner.decide('stop', {})['action'], 'stay')

    @patch.dict(os.environ, {'ANTHROPIC_API_KEY': 'test', 'MAX_API_CALLS': '1'})
    @patch('urllib.request.urlopen', side_effect=TimeoutError)
    def test_failed_call_counts(self, urlopen):
        planner = Planner()
        with self.assertRaises(TimeoutError): planner.decide('come along', {})
        self.assertEqual(planner.calls, 1)
        self.assertEqual(planner.decide('come along', {})['action'], 'chat')

class HeardTests(unittest.TestCase):
    """The queue that carries spoken orders to the plugin."""

    def test_lines_are_taken_once_and_in_order(self):
        heard = Heard()
        heard.add('Bjorn, chop wood')
        heard.add('Bjorn, guard the camp')
        self.assertEqual(heard.take(), ['Bjorn, chop wood', 'Bjorn, guard the camp'])
        self.assertEqual(heard.take(), [])

    def test_junk_is_refused(self):
        heard = Heard()
        for bad in ['', '   ', 'x' * 501]:
            with self.assertRaises(ValueError): heard.add(bad)

    def test_queue_is_bounded(self):
        # A transcriber left running while the game is closed must not grow it forever.
        heard = Heard(limit=3)
        for i in range(10): heard.add(f'Bjorn, line {i}')
        kept = heard.take()
        self.assertEqual(len(kept), 3)
        self.assertEqual(kept[-1], 'Bjorn, line 9')

if __name__ == '__main__': unittest.main()
