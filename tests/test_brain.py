import importlib.util
import json
import os
import re
import unittest
from pathlib import Path
from unittest.mock import patch
from brain.server import (ACTIONS, ACTION_CRITERIA, ARGLESS, JEV_TIMEOUT, PLAN_TIMEOUT, TALK_TIMEOUT,
                          Heard, Planner, extract_item, offline, validate)

ROOT = Path(__file__).resolve().parents[1]


def jev_reply(action, confidence=0.95, named=0.9, tone='order'):
    """The shape api.typesafe.ai returns for the three questions the planner asks."""
    return json.dumps({'model': 'jev-latest', 'usage': {'input_tokens': 120, 'output_tokens': 12},
                       'answers': {'action': {'type': 'choice', 'choice': action, 'confidence': confidence,
                                              'probabilities': {action: confidence}},
                                   'named': {'type': 'noul', 'noul': named},
                                   'tone': {'type': 'choice', 'choice': tone, 'confidence': 0.9,
                                            'probabilities': {tone: 0.9}}}}).encode()


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

    def test_every_action_is_a_choice_jev_can_pick(self):
        # The choice criteria ARE the contract now; a new action with no description
        # would be unreachable, and a description with no action unpickable.
        self.assertEqual(set(ACTION_CRITERIA), set(ACTIONS))
        self.assertTrue(all(ACTION_CRITERIA.values()), 'every action needs a description')
        self.assertLessEqual(len(ACTION_CRITERIA), 255, 'Jev takes at most 255 choices')


class ItemTests(unittest.TestCase):
    """Jev answers with a choice and never with text, so the thing that was named
    is read off the sentence here rather than written by a model."""

    def test_the_named_thing_survives_the_verb(self):
        for message, item in [('craft 20 wood arrows', '20 wood arrows'), ('eat cooked meat', 'cooked meat'),
                              ('equip the iron sword', 'iron sword'), ('attack the greyling', 'greyling'),
                              ('pick raspberries', 'raspberries'), ('deposit the wood and stone', 'wood and stone')]:
            self.assertEqual(extract_item(message), item)

    def test_counts_are_kept(self):
        self.assertEqual(extract_item('give me 10 wood'), '10 wood')
        self.assertEqual(extract_item('Bjorn, drop 10 wood'), '10 wood')

    def test_people_and_places_keep_their_name_and_case(self):
        for message, item in [('go to Aregas', 'Aregas'), ('stick with Aregas', 'Aregas'),
                              ('where is Sven', 'Sven'), ('remember this as the mine', 'mine'),
                              ('go to the mine', 'mine')]:
            self.assertEqual(extract_item(message), item)

    def test_a_particle_is_never_mistaken_for_an_item(self):
        # "eat up" used to match turnip soup through the "up" substring.
        self.assertEqual(extract_item('eat up'), '')
        self.assertEqual(extract_item('chop down some trees'), 'trees')

    def test_everything_is_spelled_as_no_item(self):
        for message in ['unequip all', 'take all', 'repair your stuff', 'dump it']:
            self.assertEqual(extract_item(message), '')

    def test_a_question_keeps_only_the_thing_asked_about(self):
        self.assertEqual(extract_item('what does a chest cost'), 'chest')
        self.assertEqual(extract_item('how do I make a torch'), 'torch')
        self.assertEqual(extract_item('do we have any deer hide'), 'deer hide')

    def test_the_container_is_not_the_item(self):
        # The action already decides what he acts on, so "out of the chest" is not
        # a thing to go looking for in his pack.
        for message, item in [('take the wood out of the chest', 'wood'),
                              ('put the stone into the chest', 'stone'),
                              ('stash the wood away', 'wood'), ('drop it on the floor', '')]:
            self.assertEqual(extract_item(message), item)

    def test_a_clause_is_not_a_name(self):
        # Past a few words the reader has failed, and a junk filter is worse than
        # none: the plugin would answer "I see no <whole sentence> to fight".
        self.assertEqual(extract_item("there's a troll on us, deal with it"), '')
        self.assertEqual(extract_item('walk a ring round the mine and kill anything that comes'), '')
        # Naming several things at once is still a real answer, and stays whole.
        self.assertEqual(extract_item('deposit the wood, stone and flint'), 'wood, stone and flint')

    def test_bounded(self):
        self.assertLessEqual(len(extract_item('craft ' + 'x'*300)), 120)


class JevTests(unittest.TestCase):
    @patch.dict(os.environ, {'TYPESAFE_API_KEY': 'test', 'ANTHROPIC_API_KEY': '', 'MAX_JEV_CALLS': '10'})
    @patch('urllib.request.urlopen')
    def test_jev_picks_the_action_and_the_item_is_read_locally(self, urlopen):
        urlopen.return_value.__enter__.return_value.read.return_value = jev_reply('craft')
        result = Planner().decide('craft 20 wood arrows', {})
        self.assertEqual(result['action'], 'craft')
        self.assertEqual(result['item'], '20 wood arrows')
        self.assertTrue(result['reply'], 'an acknowledgement, never a claim of completion')

    @patch.dict(os.environ, {'TYPESAFE_API_KEY': 'test', 'ANTHROPIC_API_KEY': ''})
    @patch('urllib.request.urlopen')
    def test_the_request_matches_the_system_one_contract(self, urlopen):
        urlopen.return_value.__enter__.return_value.read.return_value = jev_reply('follow')
        Planner().decide('walk along with me', {'health': 100})
        request = urlopen.call_args[0][0]
        self.assertEqual(request.full_url, 'https://api.typesafe.ai/v1/systemone')
        self.assertEqual(request.get_header('Authorization'), 'Bearer test')
        body = json.loads(request.data)
        self.assertEqual(body['model'], 'jev-latest')
        self.assertEqual(body['state'], {'order': 'walk along with me', 'state': {'health': 100}})
        self.assertEqual(body['questions']['action']['type'], 'choice')
        self.assertEqual(set(body['questions']['action']['criteria']), set(ACTIONS))
        self.assertEqual(body['questions']['named']['type'], 'noul')

    @patch.dict(os.environ, {'TYPESAFE_API_KEY': 'test', 'ANTHROPIC_API_KEY': ''})
    @patch('urllib.request.urlopen')
    def test_no_item_is_sent_where_the_plugin_reads_none(self, urlopen):
        urlopen.return_value.__enter__.return_value.read.return_value = jev_reply('escort')
        self.assertEqual(Planner().decide('come hunting with us', {})['item'], '')

    @patch.dict(os.environ, {'TYPESAFE_API_KEY': 'test', 'ANTHROPIC_API_KEY': ''})
    @patch('urllib.request.urlopen')
    def test_taking_everything_is_judged_on_the_pick_alone(self, urlopen):
        # drop_all and pile ignore the item and take the lot by definition, so
        # requiring a readable one there would only ever refuse a valid order.
        urlopen.return_value.__enter__.return_value.read.return_value = jev_reply('pile', named=0.99)
        self.assertEqual(Planner().decide('put all that timber on the floor', {})['action'], 'pile')

    @patch.dict(os.environ, {'TYPESAFE_API_KEY': 'test', 'ANTHROPIC_API_KEY': ''})
    @patch('urllib.request.urlopen')
    def test_an_unsure_pick_becomes_talk_rather_than_a_wrong_job(self, urlopen):
        urlopen.return_value.__enter__.return_value.read.return_value = jev_reply('mine', confidence=0.11)
        self.assertEqual(Planner().decide('do the thing', {})['action'], 'chat')

    @patch.dict(os.environ, {'TYPESAFE_API_KEY': 'test', 'ANTHROPIC_API_KEY': ''})
    @patch('urllib.request.urlopen')
    def test_he_will_not_empty_a_chest_on_a_guess(self, urlopen):
        # Something was named but nothing could be read off the sentence, and an
        # empty item means everything. He asks instead.
        urlopen.return_value.__enter__.return_value.read.return_value = jev_reply('deposit', named=0.95)
        self.assertEqual(Planner().decide('put that lot away', {})['action'], 'chat')
        # Named outright, it goes through.
        urlopen.return_value.__enter__.return_value.read.return_value = jev_reply('deposit', named=0.95)
        result = Planner().decide('deposit the wood and stone', {})
        self.assertEqual((result['action'], result['item']), ('deposit', 'wood and stone'))
        # "deposit everything" names nothing, so an empty item is what was meant.
        urlopen.return_value.__enter__.return_value.read.return_value = jev_reply('deposit', named=0.02)
        self.assertEqual(Planner().decide('hand over the haul', {})['action'], 'deposit')

    @patch.dict(os.environ, {'TYPESAFE_API_KEY': 'test', 'ANTHROPIC_API_KEY': ''})
    @patch('urllib.request.urlopen')
    def test_an_answer_that_makes_no_sense_is_talk_not_a_crash(self, urlopen):
        # A shape the API should never return still has to fail towards asking.
        for payload in [b'{}', b'{"answers": {}}',
                        json.dumps({'answers': {'action': {'type': 'choice', 'choice': 'launch_missiles',
                                                           'confidence': 0.99, 'probabilities': {}}}}).encode()]:
            urlopen.return_value.__enter__.return_value.read.return_value = payload
            self.assertEqual(Planner().decide('do the thing', {})['action'], 'chat')

    @patch.dict(os.environ, {'TYPESAFE_API_KEY': 'test', 'ANTHROPIC_API_KEY': '', 'MAX_JEV_CALLS': '1'})
    @patch('urllib.request.urlopen')
    def test_jev_budget_is_separate_and_spent_by_failures(self, urlopen):
        urlopen.return_value.__enter__.return_value.read.return_value = jev_reply('chop')
        planner = Planner()
        self.assertEqual(planner.decide('go and fell some timber', {})['action'], 'chop')
        self.assertEqual(planner.decide('go and fell some timber', {})['action'], 'chat')
        self.assertEqual(urlopen.call_count, 1)
        # Direct orders never touch a budget.
        self.assertEqual(planner.decide('stop', {})['action'], 'stay')

    @patch.dict(os.environ, {'TYPESAFE_API_KEY': 'test', 'ANTHROPIC_API_KEY': ''})
    @patch('urllib.request.urlopen', side_effect=TimeoutError)
    def test_a_failed_jev_call_counts_and_surfaces(self, urlopen):
        planner = Planner()
        with self.assertRaises(TimeoutError): planner.decide('go and fell some timber', {})
        self.assertEqual(planner.jev_calls, 1)

    @patch.dict(os.environ, {'TYPESAFE_API_KEY': 'test', 'ANTHROPIC_API_KEY': 'test', 'MAX_API_CALLS': '5'})
    @patch('urllib.request.urlopen')
    def test_only_talk_reaches_anthropic(self, urlopen):
        calls = []

        def answer(request, **_):
            calls.append(request.full_url)
            payload = (jev_reply('chat', tone='question') if 'typesafe' in request.full_url
                       else json.dumps({'content': [{'type': 'text', 'text': 'Black Forest. Look for mottled boulders.'}]}).encode())
            urlopen.return_value.__enter__.return_value.read.return_value = payload
            return urlopen.return_value
        urlopen.side_effect = answer
        planner = Planner()
        result = planner.decide('where do I find copper', {})
        self.assertEqual(result['action'], 'chat')
        self.assertEqual(result['reply'], 'Black Forest. Look for mottled boulders.')
        self.assertEqual(calls, ['https://api.typesafe.ai/v1/systemone', 'https://api.anthropic.com/v1/messages'])
        # An action Jev can answer never spends an Anthropic call.
        calls.clear()
        urlopen.side_effect = lambda request, **_: (calls.append(request.full_url),
            setattr(urlopen.return_value.__enter__.return_value.read, 'return_value', jev_reply('chop')),
            urlopen.return_value)[-1]
        self.assertEqual(planner.decide('go and fell some timber', {})['action'], 'chop')
        self.assertEqual(calls, ['https://api.typesafe.ai/v1/systemone'])
        self.assertEqual(planner.calls, 1, 'the Anthropic budget only moves when he speaks')

    @patch.dict(os.environ, {'TYPESAFE_API_KEY': 'test', 'ANTHROPIC_API_KEY': 'test'})
    @patch('urllib.request.urlopen')
    def test_losing_his_voice_does_not_lose_the_order(self, urlopen):
        def answer(request, **_):
            if 'typesafe' in request.full_url:
                urlopen.return_value.__enter__.return_value.read.return_value = jev_reply('chat', tone='abuse')
                return urlopen.return_value
            raise TimeoutError
        urlopen.side_effect = answer
        result = Planner().decide('you smell of goat', {})
        self.assertEqual(result['action'], 'chat')
        self.assertTrue(result['reply'], 'a canned line beats silence')


class ClaudePlannerTests(unittest.TestCase):
    """With no Jev key the planner is what it was: Claude decides and speaks."""

    @patch.dict(os.environ, {'TYPESAFE_API_KEY': '', 'ANTHROPIC_API_KEY': 'test', 'MAX_API_CALLS': '1'})
    @patch('urllib.request.urlopen')
    def test_api_contract_and_cap(self, urlopen):
        urlopen.return_value.__enter__.return_value.read.return_value = json.dumps({'content': [{'type': 'text', 'text': '{"action":"follow","reply":"On my way."}'}]}).encode()
        planner = Planner()
        self.assertEqual(planner.decide('come along', {})['action'], 'follow')
        self.assertEqual(planner.decide('come along', {})['action'], 'chat')
        self.assertEqual(urlopen.call_count, 1)
        self.assertEqual(planner.decide('stop', {})['action'], 'stay')

    @patch.dict(os.environ, {'TYPESAFE_API_KEY': '', 'ANTHROPIC_API_KEY': 'test', 'MAX_API_CALLS': '1'})
    @patch('urllib.request.urlopen', side_effect=TimeoutError)
    def test_failed_call_counts(self, urlopen):
        planner = Planner()
        with self.assertRaises(TimeoutError): planner.decide('come along', {})
        self.assertEqual(planner.calls, 1)
        self.assertEqual(planner.decide('come along', {})['action'], 'chat')

    @patch.dict(os.environ, {'TYPESAFE_API_KEY': '', 'ANTHROPIC_API_KEY': ''})
    def test_no_keys_at_all_still_heeds_the_basics(self):
        planner = Planner()
        self.assertEqual(planner.decide('stop', {})['action'], 'stay')
        self.assertEqual(planner.decide('build me a longhouse', {})['action'], 'chat')
        self.assertEqual(planner.mode, 'offline')


class PluginContractTests(unittest.TestCase):
    """The planner writes a decision the C# dispatch has to be able to act on, and
    nothing but these tests notices when the two drift apart."""

    def dispatch(self):
        """Every `case` in the decision switch, paired with its body."""
        source = (ROOT / 'plugin/Companion.cs').read_text()
        block = source[source.index('string item = Normalize(decision.item);'):]
        block = block[:block.index('\n            }\n')]
        chunks = re.split(r'case\s+"([a-z_]+)"\s*:', block)
        return dict(zip(chunks[1::2], chunks[2::2]))

    def test_every_action_has_somewhere_to_land(self):
        missing = ACTIONS - set(self.dispatch())
        self.assertFalse(missing, f'the planner can choose {missing}, which the plugin cannot act on')

    def test_argless_mirrors_the_plugin(self):
        # Sending an item to an action that ignores it is noise; failing to send one
        # to an action that reads it is a job that does nothing. Neither is visible
        # from the Python side alone, so the C# is the source of truth.
        ignores = {name for name, body in self.dispatch().items() if 'item' not in body}
        self.assertEqual(ignores & ACTIONS, ARGLESS)

    def test_the_two_call_path_fits_inside_the_plugins_patience(self):
        # The plugin abandons /decide after 15s. Jev then Anthropic runs back to
        # back, so their budgets have to fit inside that or he gives up on an
        # answer that was on its way.
        source = (ROOT / 'plugin/Companion.cs').read_text()
        waits = [int(m) for m in re.findall(r'"http://127\.0\.0\.1:8765/decide".*?request\.timeout = (\d+)',
                                            source, re.S)]
        self.assertTrue(waits, 'could not find the plugin timeout for /decide')
        self.assertLess(JEV_TIMEOUT + TALK_TIMEOUT, waits[0])
        self.assertLess(PLAN_TIMEOUT, waits[0])


class RehearsalTests(unittest.TestCase):
    """The order corpus, run end to end through a real planner over real HTTP."""

    def test_the_corpus_lands_where_it_says(self):
        spec = importlib.util.spec_from_file_location('rehearse', ROOT / 'scripts/rehearse.py')
        rehearse = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(rehearse)
        cases, failures = rehearse.rehearse(quiet=True)
        self.assertGreater(len(cases), 20, 'the corpus should cover more than a handful of orders')
        self.assertEqual(failures, [])


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
