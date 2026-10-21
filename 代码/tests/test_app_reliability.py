"""Real Flask/SocketIO handlers, isolated storage, and no model/network calls."""
from concurrent.futures import ThreadPoolExecutor
import importlib.util
import io
import json
import os
from pathlib import Path
import sys
import tempfile
import time
import types
import unittest
from unittest.mock import Mock, patch

from werkzeug.security import check_password_hash


ROOT = Path(__file__).resolve().parents[1]


class AppReliabilityTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        stub = types.ModuleType('bot')
        stub.get_bot_decision = Mock(return_value=(False, ''))
        stub.set_reply_callback = Mock()
        stub.set_status_callback = Mock()
        stub.cancel_pending = Mock()
        stub.set_assistant_callback = Mock()
        stub.restore_assistant_state = Mock()
        stub.get_assistant_state = Mock(side_effect=lambda room: {
            'room': room, 'version': 0, 'auto_detect': True, 'task': None,
            'proposals': [], 'trace': [], 'last_error': '',
        })
        stub.handle_assistant_action = Mock(return_value={'ok': True})
        name = '_isolated_chat_app_security_tests'
        spec = importlib.util.spec_from_file_location(name, ROOT / 'app.py')
        module = importlib.util.module_from_spec(spec)
        sys.modules[name] = module
        with patch.dict(os.environ, {'CHAPTER2_SECRET_KEY': 'isolated-test-secret-do-not-use-in-production'}):
            with patch.dict(sys.modules, {'bot': stub}):
                spec.loader.exec_module(module)
        cls.module = module
        cls.bot = stub
        cls.module_name = name

    @classmethod
    def tearDownClass(cls):
        sys.modules.pop(cls.module_name, None)

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.mod = self.module
        self.mod.DATA_USER_DIR = str(Path(self.directory.name) / 'database')
        self.mod.app.config.update(
            TESTING=True, UPLOAD_FOLDER=str(Path(self.directory.name) / 'uploads'),
            AUTH_TOKEN_MAX_AGE=7 * 24 * 60 * 60,
        )
        self.mod.online_users.clear()
        self.mod.socket_rooms.clear()
        self.bot.get_bot_decision.reset_mock()
        self.bot.cancel_pending.reset_mock()
        self.clients = []
        self.addCleanup(self._disconnect)

    def _disconnect(self):
        for client in self.clients:
            if client.is_connected():
                client.disconnect()

    def client(self):
        client = self.mod.socketio.test_client(self.mod.app)
        self.clients.append(client)
        return client

    def login(self, username='alice', password='safe-test-password', register=True):
        client = self.client()
        if register:
            client.emit('register_action', {'u': username, 'p': password})
            self.assertTrue(any(event['name'] == 'ok' for event in client.get_received()))
        client.emit('login_action', {'u': username, 'p': password})
        events = client.get_received()
        login = next(event['args'][0] for event in events if event['name'] == 'login_ok')
        return client, login['token']

    def join(self, client, token, room='Lobby'):
        result = client.emit('join', {'token': token, 'room': room}, callback=True)
        self.assertTrue(result['ok'])
        client.get_received()

    def send(self, client, token, message='今晚吃什么', room='Lobby', **extra):
        payload = {'token': token, 'room': room, 'm': message, 't': 'text'}
        payload.update(extra)
        return client.emit('send_msg', payload, callback=True)

    def test_registration_hashes_password_and_profile_never_returns_it(self):
        client, token = self.login()
        user = self.mod.db_op('users')['alice']
        self.assertNotIn('p', user)
        self.assertTrue(check_password_hash(user['password_hash'], 'safe-test-password'))
        client.emit('update_profile', {'token': token, 'nick': '新昵称', 'avatar': user['avatar']})
        public = next(event['args'][0]['user'] for event in client.get_received() if event['name'] == 'update_res')
        self.assertEqual(set(public), {'u', 'nick', 'avatar'})
        self.assertEqual(public['nick'], '新昵称')

    def test_existing_plaintext_account_migrates_only_after_successful_login(self):
        original = {'p': '旧密码', 'nick': '原有昵称', 'avatar': 'https://example.test/avatar.png'}
        self.mod.db_op('users', {'legacy': original})
        client = self.client()
        client.emit('login_action', {'u': 'legacy', 'p': '错误密码'})
        self.assertTrue(any(event['name'] == 'err' for event in client.get_received()))
        self.assertEqual(self.mod.db_op('users')['legacy'], original)
        _, token = self.login('legacy', '旧密码', register=False)
        migrated = self.mod.db_op('users')['legacy']
        self.assertNotIn('p', migrated)
        self.assertTrue(check_password_hash(migrated['password_hash'], '旧密码'))
        self.assertEqual(migrated['nick'], original['nick'])
        self.assertEqual(migrated['avatar'], original['avatar'])
        self.assertEqual(self.mod._token_user(token), 'legacy')

    def test_unauthenticated_requests_cannot_read_send_edit_cancel_or_upload(self):
        client = self.client()
        for event, payload in (
            ('join', {'room': 'Lobby', 'u': 'alice'}),
            ('send_msg', {'room': 'Lobby', 'u': 'alice', 'm': 'forged'}),
            ('update_profile', {'u': 'alice', 'nick': 'forged'}),
            ('cancel_ai', {'room': 'Lobby', 'u': 'alice'}),
        ):
            client.emit(event, payload)
            received = client.get_received()
            self.assertTrue(any(item['name'] == 'auth_error' for item in received), event)
            self.assertFalse(any(item['name'] in {'history', 'new_msg', 'update_res'} for item in received))
        response = self.mod.app.test_client().post('/upload', data={'file': (io.BytesIO(b'x'), 'image.png')})
        self.assertEqual(response.status_code, 401)
        self.assertFalse(Path(self.mod.app.config['UPLOAD_FOLDER']).exists())
        self.assertEqual(self.mod.db_op('chat_history'), {})
        self.bot.get_bot_decision.assert_not_called()
        self.bot.cancel_pending.assert_not_called()

    def test_signed_in_user_cannot_impersonate_another_account(self):
        client, token = self.login()
        self.login('bob')
        client.emit('join', {'room': 'Lobby', 'token': token, 'u': 'bob'})
        self.assertTrue(any(event['name'] == 'err' for event in client.get_received()))
        self.assertEqual(self.mod.socket_rooms, {})
        self.join(client, token)
        result = self.send(client, token, u='bob')
        self.assertFalse(result['ok'])
        client.emit('update_profile', {'token': token, 'u': 'bob', 'nick': 'forged'})
        self.assertNotEqual(self.mod.db_op('users')['bob']['nick'], 'forged')
        self.assertEqual(self.mod.db_op('chat_history'), {})

    def test_invalid_or_expired_tokens_are_rejected(self):
        client, token = self.login()
        invalid_tokens = [token + 'tampered', '', {'u': 'alice'}]
        for invalid in invalid_tokens:
            client.emit('join', {'token': invalid, 'room': 'Lobby'})
            self.assertTrue(any(event['name'] == 'auth_error' for event in client.get_received()))
        self.mod.app.config['AUTH_TOKEN_MAX_AGE'] = -1
        self.assertIsNone(self.mod._token_user(token))

    def test_room_switch_removes_previous_subscription_and_presence(self):
        alice, alice_token = self.login()
        bob, bob_token = self.login('bob')
        self.join(alice, alice_token)
        self.join(bob, bob_token)
        alice.get_received()
        self.join(alice, alice_token, 'ZenRoom')
        bob.get_received()
        result = self.send(bob, bob_token, '只发公共大厅')
        self.assertTrue(result['ok'])
        self.assertFalse(any(event['name'] == 'new_msg' for event in alice.get_received()))
        self.assertEqual(len(self.mod.online_users['Lobby']), 1)
        self.assertEqual(len(self.mod.online_users['ZenRoom']), 1)
        self.assertFalse(self.send(alice, alice_token, room='Lobby')['ok'])
        self.assertEqual(len(self.mod.db_op('chat_history')['Lobby']), 1)

    def test_events_include_room_and_successful_send_has_acknowledgement(self):
        client, token = self.login()
        client.emit('join', {'room': 'Lobby', 'token': token})
        events = client.get_received()
        for name, data_key in [('history', 'messages'), ('update_users', 'users')]:
            data = next(event['args'][0] for event in events if event['name'] == name)
            self.assertEqual(data['room'], 'Lobby')
            self.assertIsInstance(data[data_key], list)
        result = self.send(client, token)
        message = next(event['args'][0] for event in client.get_received() if event['name'] == 'new_msg')
        self.assertEqual(result, {'ok': True, 'id': message['id']})
        self.assertEqual(message['room'], 'Lobby')
        self.assertEqual(message['u'], 'alice')
        self.assertEqual(self.bot.get_bot_decision.call_args.args[0]['user_id'], 'alice')
        self.assertTrue(self.bot.get_bot_decision.call_args.args[0]['client_sid'])
        self.mod.bot_status_callback('Lobby', 'analyzing', '正在识别')
        status = next(event['args'][0] for event in client.get_received() if event['name'] == 'ai_status')
        self.assertEqual(status, {'room': 'Lobby', 'status': 'analyzing', 'message': '正在识别'})

    def test_cancel_requires_current_room_membership(self):
        client, token = self.login()
        result = client.emit('cancel_ai', {'token': token, 'room': 'Lobby'}, callback=True)
        self.assertFalse(result['ok'])
        self.join(client, token)
        result = client.emit('cancel_ai', {'token': token, 'room': 'ZenRoom'}, callback=True)
        self.assertFalse(result['ok'])
        result = client.emit('cancel_ai', {'token': token, 'room': 'Lobby'}, callback=True)
        self.assertTrue(result['ok'])
        self.bot.cancel_pending.assert_called_once_with('Lobby')

    def test_untrusted_media_and_malformed_messages_never_reach_bot(self):
        client, token = self.login()
        self.join(client, token)
        for url in [
            'http://127.0.0.1/private', 'https://example.test/image.png',
            'file:///C:/private', '../private.png', '/static/uploads/../../users.json',
            '/static/uploads/00000000-0000-0000-0000-000000000000.svg',
        ]:
            self.assertFalse(self.send(client, token, t='image', url=url)['ok'])
        for data in [{'m': ' '}, {'m': 'x' * 4001}, {'m': {'bad': 1}}, {'t': 'html'}, {'quote': '<script>'}]:
            payload = {'token': token, 'room': 'Lobby', 'm': 'test'}
            payload.update(data)
            self.assertFalse(client.emit('send_msg', payload, callback=True)['ok'])
        self.assertEqual(self.mod.db_op('chat_history'), {})
        self.bot.get_bot_decision.assert_not_called()

    def test_authenticated_upload_rejects_svg_and_allows_local_media(self):
        client, token = self.login()
        self.join(client, token)
        http = self.mod.app.test_client()
        headers = {'Authorization': f'Bearer {token}'}
        bad = http.post('/upload', headers=headers, data={'file': (io.BytesIO(b'<svg/>'), 'unsafe.svg')})
        self.assertEqual(bad.status_code, 400)
        good = http.post('/upload', headers=headers, data={'file': (io.BytesIO(b'\x89PNG\r\n\x1a\n'), 'image.png')})
        self.assertEqual(good.status_code, 200)
        self.assertEqual(good.headers['X-Content-Type-Options'], 'nosniff')
        url = good.get_json()['url']
        result = self.send(client, token, message='', t='image', url=url)
        self.assertTrue(result['ok'])
        self.assertEqual(self.mod.db_op('chat_history')['Lobby'][0]['url'], url)

    def test_parallel_human_and_bot_writes_keep_every_message(self):
        def user_message(index):
            def append(records):
                messages = records.setdefault('Lobby', [])
                time.sleep(0.001)
                messages.append({'id': f'human-{index}', 'm': 'test'})
            self.mod.db_transaction('chat_history', append)
        def bot_message(index):
            self.mod.bot_reply_callback('Lobby', f'bot-{index}', 'test')
        with ThreadPoolExecutor(max_workers=8) as pool:
            futures = []
            for index in range(24):
                futures.append(pool.submit(user_message, index))
                futures.append(pool.submit(bot_message, index))
            for future in futures:
                future.result(timeout=10)
        records = self.mod.db_op('chat_history')['Lobby']
        self.assertEqual(len(records), 48)
        self.assertEqual(len({item['id'] for item in records}), 48)
        path = Path(self.mod.DATA_USER_DIR) / 'chat_history.json'
        self.assertEqual(json.loads(path.read_text(encoding='utf-8'))['Lobby'], records)
        self.assertFalse(list(Path(self.mod.DATA_USER_DIR).glob('*.tmp')))

    def test_atomic_failure_preserves_existing_records_and_database_path_is_fixed(self):
        self.mod.db_op('chat_history', {'Lobby': [{'id': 'existing'}]})
        with patch.object(self.mod.os, 'replace', side_effect=OSError('simulated disk failure')):
            with self.assertRaises(OSError):
                self.mod.db_transaction('chat_history', lambda db: db['Lobby'].append({'id': 'new'}))
        self.assertEqual(self.mod.db_op('chat_history'), {'Lobby': [{'id': 'existing'}]})
        for filename in ['../users', 'users.json', 'C:/other', 'secret']:
            with self.assertRaises(ValueError):
                self.mod.db_op(filename)


if __name__ == '__main__':
    unittest.main()
