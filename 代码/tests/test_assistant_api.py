"""Assistant SocketIO API checks using fake bot state and temporary storage."""
from concurrent.futures import ThreadPoolExecutor
import copy
import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import types
import unittest
from unittest.mock import Mock, patch


ROOT = Path(__file__).resolve().parents[1]


def state(room='Lobby', version=0):
    return {
        'room': room, 'version': version, 'auto_detect': True,
        'task': None, 'proposals': [], 'trace': [], 'last_error': '',
    }


class AssistantApiTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.bot = types.ModuleType('bot')
        for name in (
            'get_bot_decision', 'set_reply_callback', 'set_status_callback',
            'cancel_pending', 'set_assistant_callback', 'get_assistant_state',
            'restore_assistant_state', 'handle_assistant_action',
        ):
            setattr(cls.bot, name, Mock())
        cls.module_name = '_isolated_six_feature_app_tests'
        spec = importlib.util.spec_from_file_location(cls.module_name, ROOT / 'app.py')
        cls.mod = importlib.util.module_from_spec(spec)
        sys.modules[cls.module_name] = cls.mod
        with patch.dict(os.environ, {'CHAPTER2_SECRET_KEY': 'isolated-assistant-test-secret-not-for-production'}):
            with patch.dict(sys.modules, {'bot': cls.bot}):
                spec.loader.exec_module(cls.mod)

    @classmethod
    def tearDownClass(cls):
        sys.modules.pop(cls.module_name, None)

    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.mod.DATA_USER_DIR = str(Path(directory.name) / 'database')
        self.mod.app.config.update(TESTING=True)
        self.mod.online_users.clear()
        self.mod.socket_rooms.clear()
        self.mod.db_op('users', {
            'alice': {'nick': 'Alice', 'avatar': ''},
            'bob': {'nick': 'Bob', 'avatar': ''},
        })
        self.states = {room: state(room) for room in self.mod.ALLOWED_ROOMS}
        self.restored = set()
        for name in ('get_bot_decision', 'get_assistant_state', 'restore_assistant_state', 'handle_assistant_action'):
            getattr(self.bot, name).reset_mock(return_value=True, side_effect=True)
        self.bot.get_assistant_state.side_effect = lambda room: copy.deepcopy(self.states[room])
        self.bot.restore_assistant_state.side_effect = self._restore
        self.bot.handle_assistant_action.return_value = {'ok': True}
        self.clients = []
        self.addCleanup(self._disconnect)

    def _restore(self, room, snapshot):
        self.assertFalse(self.mod._db_lock._is_owned(), 'DB lock held while calling bot restore')
        if room not in self.restored:
            if snapshot:
                self.states[room] = copy.deepcopy(snapshot)
            self.restored.add(room)

    def _disconnect(self):
        for client in self.clients:
            if client.is_connected():
                client.disconnect()

    def client(self, user='alice', room='Lobby'):
        client = self.mod.socketio.test_client(self.mod.app)
        self.clients.append(client)
        token = self.mod._sign_token(user)
        if room is not None:
            result = client.emit('join', {'token': token, 'room': room}, callback=True)
            self.assertEqual(result, {'ok': True, 'room': room})
        client.get_received()
        return client, token

    def action(self, client, token, **values):
        payload = {'token': token, 'room': 'Lobby', 'action': 'new', 'version': 0}
        payload.update(values)
        return client.emit('assistant_action', payload, callback=True)

    def test_assistant_callback_is_registered_and_uses_separate_database(self):
        self.bot.set_assistant_callback.assert_called_once_with(self.mod.bot_assistant_callback)
        self.mod.bot_assistant_callback('Lobby', state(version=1))
        self.assertEqual(self.mod.db_op('assistant_state')['Lobby'], state(version=1))
        self.assertEqual(self.mod.db_op('chat_history'), {})
        self.assertEqual(set(self.mod.db_op('users')), {'alice', 'bob'})

    def test_join_restores_saved_state_after_history_without_holding_database_lock(self):
        saved = state(version=8)
        saved['auto_detect'] = False
        saved['task'] = {'id': 'task-8', 'status': 'ready', 'fields': {'budget': '200元'}}
        self.mod.db_op('assistant_state', {'Lobby': saved})
        client, token = self.client(room=None)
        client.emit('join', {'token': token, 'room': 'Lobby'})
        received = client.get_received()
        names = [event['name'] for event in received]
        self.assertLess(names.index('history'), names.index('assistant_state'))
        actual = next(event['args'][0] for event in received if event['name'] == 'assistant_state')
        self.assertEqual(actual, saved)
        self.bot.restore_assistant_state.assert_called_once_with('Lobby', saved)
        self.bot.get_assistant_state.assert_called_once_with('Lobby')

    def test_rejoining_does_not_override_active_bot_state_with_disk_snapshot(self):
        client, token = self.client()
        self.states['Lobby']['version'] = 6
        self.mod.db_op('assistant_state', {'Lobby': state(version=2)})
        client.emit('join', {'token': token, 'room': 'Lobby'})
        current = next(event['args'][0] for event in client.get_received() if event['name'] == 'assistant_state')
        self.assertEqual(current['version'], 6)

    def test_saved_snapshots_are_detached_and_broadcast_only_to_their_room(self):
        lobby, _ = self.client()
        other, _ = self.client(user='bob', room='ZenRoom')
        lobby.get_received()
        value = state(version=1)
        value['trace'] = [{'summary': '原始信息'}]
        self.mod.bot_assistant_callback('Lobby', value)
        value['trace'][0]['summary'] = '之后被修改'
        saved = self.mod.db_op('assistant_state')['Lobby']
        self.assertEqual(saved['trace'][0]['summary'], '原始信息')
        received = next(event['args'][0] for event in lobby.get_received() if event['name'] == 'assistant_state')
        self.assertEqual(received, saved)
        self.assertFalse(any(event['name'] == 'assistant_state' for event in other.get_received()))
        self.assertNotIn('ZenRoom', self.mod.db_op('assistant_state'))

    def test_old_or_duplicate_state_callback_cannot_overwrite_or_rebroadcast(self):
        client, _ = self.client()
        self.mod.bot_assistant_callback('Lobby', state(version=4))
        client.get_received()
        for version in (3, 4, 1):
            stale = state(version=version)
            stale['auto_detect'] = False
            self.mod.bot_assistant_callback('Lobby', stale)
        self.assertEqual(self.mod.db_op('assistant_state')['Lobby'], state(version=4))
        self.assertFalse(any(event['name'] == 'assistant_state' for event in client.get_received()))

    def test_parallel_state_writes_preserve_highest_revision_per_room(self):
        values = [(room, state(room, version)) for version in range(1, 21) for room in ('Lobby', 'ZenRoom')]
        with ThreadPoolExecutor(max_workers=8) as pool:
            futures = [pool.submit(self.mod.bot_assistant_callback, room, value) for room, value in reversed(values)]
            for future in futures:
                future.result(timeout=10)
        saved = self.mod.db_op('assistant_state')
        self.assertEqual(saved['Lobby']['version'], 20)
        self.assertEqual(saved['ZenRoom']['version'], 20)
        path = Path(self.mod.DATA_USER_DIR) / 'assistant_state.json'
        self.assertEqual(json.loads(path.read_text(encoding='utf-8')), saved)
        self.assertFalse(list(path.parent.glob('*.tmp')))

    def test_invalid_callback_snapshots_are_not_written(self):
        for room, snapshot in (
            ('unknown', state('unknown')), ('Lobby', None),
            ('Lobby', state('ZenRoom')), ('Lobby', state(version=True)),
            ('Lobby', state(version=-1)), ('Lobby', {**state(), 'trace': [object()]}),
            ('Lobby', {**state(), 'trace': [float('nan')]}),
        ):
            self.mod.bot_assistant_callback(room, snapshot)
        self.assertEqual(self.mod.db_op('assistant_state'), {})

    def test_assistant_actions_require_valid_token_and_current_room(self):
        client, token = self.client(room=None)
        for supplied in ('', token + 'tamper'):
            self.assertFalse(self.action(client, supplied)['ok'])
        self.assertFalse(self.action(client, token)['ok'])
        client.emit('join', {'token': token, 'room': 'ZenRoom'})
        self.assertFalse(self.action(client, token, room='Lobby')['ok'])
        self.assertFalse(self.action(client, token, room='ZenRoom', u='bob')['ok'])
        self.bot.handle_assistant_action.assert_not_called()

    def test_action_payload_uses_authenticated_identity_and_only_supported_fields(self):
        client, token = self.client()
        result = self.action(
            client, token, action='edit', version=7, task_id='active-task',
            fields={'budget': '', 'time': '明天上午'}, user_id='bob',
            user_nick='伪造昵称', unknown='ignored',
        )
        self.assertEqual(result, {'ok': True})
        self.bot.handle_assistant_action.assert_called_once_with(
            'Lobby', 'alice', 'Alice',
            {'action': 'edit', 'version': 7, 'task_id': 'active-task', 'fields': {'budget': '', 'time': '明天上午'}},
        )

    def test_all_actions_preserve_revision_for_atomic_bot_validation(self):
        client, token = self.client()
        payloads = [
            {'action': 'edit', 'fields': {'destination': '杭州'}},
            {'action': 'confirm'}, {'action': 'cancel'}, {'action': 'complete'},
            {'action': 'new'}, {'action': 'set_auto', 'auto_detect': False},
        ]
        for payload in payloads:
            self.assertTrue(self.action(client, token, version=11, task_id='task-1', **payload)['ok'])
            routed = self.bot.handle_assistant_action.call_args.args[3]
            self.assertEqual(routed['version'], 11)
            self.assertEqual(routed['task_id'], 'task-1')

    def test_malformed_actions_do_not_reach_bot(self):
        client, token = self.client()
        invalid = [
            {'action': []}, {'action': 'delete_all'}, {'version': None},
            {'version': '0'}, {'version': True}, {'version': -1},
            {'task_id': ''}, {'task_id': ['invalid']},
            {'action': 'edit', 'fields': {}},
            {'action': 'edit', 'fields': {'unknown': 'value'}},
            {'action': 'edit', 'fields': {'time': 'x' * 201}},
            {'action': 'edit', 'fields': {'time': None}},
            {'action': 'edit', 'fields': {'time': 'bad\x00value'}},
            {'action': 'set_auto'}, {'action': 'set_auto', 'auto_detect': 0},
            {'action': 'confirm', 'fields': {'time': '今晚'}},
            {'action': 'confirm', 'auto_detect': False},
        ]
        for payload in invalid:
            with self.subTest(payload=payload):
                self.assertFalse(self.action(client, token, **payload)['ok'])
        self.bot.handle_assistant_action.assert_not_called()

    def test_stale_revision_returns_latest_state_only_to_requesting_client(self):
        client, token = self.client()
        spectator, _ = self.client(user='bob')
        client.get_received()
        self.states['Lobby']['version'] = 12
        self.bot.handle_assistant_action.return_value = {'ok': False, 'error': '需求已更新，请查看最新内容后重试'}
        result = self.action(client, token, version=3, action='confirm', task_id='outdated-task')
        self.assertFalse(result['ok'])
        received = client.get_received()
        latest = next(event['args'][0] for event in received if event['name'] == 'assistant_state')
        self.assertEqual(latest['version'], 12)
        self.assertTrue(any(event['name'] == 'err' for event in received))
        self.assertFalse(any(event['name'] == 'assistant_state' for event in spectator.get_received()))

    def test_bot_action_exception_and_malformed_ack_have_safe_error(self):
        client, token = self.client()
        self.bot.handle_assistant_action.side_effect = RuntimeError('upstream private details')
        result = self.action(client, token)
        self.assertFalse(result['ok'])
        self.assertNotIn('private', result['error'])
        self.bot.handle_assistant_action.side_effect = None
        for invalid in (None, {'ok': 'yes'}, {'ok': False, 'error': {'raw': 'body'}}):
            self.bot.handle_assistant_action.return_value = invalid
            result = self.action(client, token)
            self.assertFalse(result['ok'])
            self.assertIsInstance(result['error'], str)

    def test_explicit_ai_flag_is_stored_and_forwarded_with_human_message(self):
        client, token = self.client()
        for ask_ai in (True, False):
            result = client.emit('send_msg', {
                'token': token, 'room': 'Lobby', 'm': '帮我比较这两种方案',
                't': 'text', 'ask_ai': ask_ai,
            }, callback=True)
            self.assertTrue(result['ok'])
            saved = self.mod.db_op('chat_history')['Lobby'][-1]
            self.assertIs(saved['ask_ai'], ask_ai)
            self.assertIs(self.bot.get_bot_decision.call_args.args[0]['ask_ai'], ask_ai)
        result = client.emit('send_msg', {'token': token, 'room': 'Lobby', 'm': '普通消息'}, callback=True)
        self.assertTrue(result['ok'])
        self.assertIs(self.mod.db_op('chat_history')['Lobby'][-1]['ask_ai'], False)

    def test_non_boolean_ai_flag_does_not_save_or_invoke_bot(self):
        client, token = self.client()
        for value in (1, 'true', None, {}, []):
            result = client.emit('send_msg', {
                'token': token, 'room': 'Lobby', 'm': 'test', 'ask_ai': value,
            }, callback=True)
            self.assertFalse(result['ok'])
        self.assertEqual(self.mod.db_op('chat_history'), {})
        self.bot.get_bot_decision.assert_not_called()


if __name__ == '__main__':
    unittest.main()
