import hmac
import json
import os
from pathlib import Path
import random
import re
import secrets
import tempfile
import threading
import time
import uuid

from flask import Flask, render_template, request, jsonify, redirect, url_for
from flask_socketio import SocketIO, emit, join_room, leave_room
from itsdangerous import BadSignature, SignatureExpired, URLSafeTimedSerializer
from werkzeug.security import check_password_hash, generate_password_hash

from bot import (
    get_bot_decision, set_reply_callback, set_status_callback, cancel_pending,
    set_assistant_callback, get_assistant_state, restore_assistant_state,
    handle_assistant_action,
)


PROJECT_DIR = Path(__file__).resolve().parent


def _session_secret():
    configured = os.getenv('CHAPTER2_SECRET_KEY')
    if configured:
        return configured
    secret_path = PROJECT_DIR / '.local' / 'session-secret.txt'
    secret_path.parent.mkdir(parents=True, exist_ok=True)
    try:
        with secret_path.open('x', encoding='ascii') as secret_file:
            secret_file.write(secrets.token_urlsafe(48))
    except FileExistsError:
        pass
    secret = secret_path.read_text(encoding='ascii').strip()
    if len(secret) < 32:
        raise RuntimeError('本地登录签名配置无效，请检查 .local/session-secret.txt')
    return secret


app = Flask(__name__)
app.config['SECRET_KEY'] = _session_secret()
app.config['UPLOAD_FOLDER'] = str(PROJECT_DIR / 'static' / 'uploads')
app.config['MAX_CONTENT_LENGTH'] = 200 * 1024 * 1024
app.config['BOT_CHANNELS'] = []
app.config['AUTH_TOKEN_MAX_AGE'] = 7 * 24 * 60 * 60

IMAGE_EXT = frozenset({'png', 'jpg', 'jpeg', 'gif', 'webp', 'bmp'})
VIDEO_EXT = frozenset({'mp4', 'webm', 'ogg', 'mov', 'm4v'})
AUDIO_EXT = frozenset({'mp3', 'wav', 'm4a', 'aac', 'flac', 'ogg', 'webm'})
ALLOWED_UPLOAD_EXT = IMAGE_EXT | VIDEO_EXT | AUDIO_EXT
ALLOWED_ROOMS = frozenset({'Lobby', 'ZenRoom', 'AI_Lab'})
MAX_MESSAGE_LENGTH = 4000
DATA_USER_DIR = str(PROJECT_DIR / 'data_user')

# One local server process: all request and AI callback threads share this lock.
_db_lock = threading.RLock()
_presence_lock = threading.RLock()
_assistant_callback_lock = threading.RLock()
socketio = SocketIO(app, max_http_buffer_size=1024 * 1024)
online_users = {}
socket_rooms = {}


def _db_path(name):
    if name not in {'users', 'chat_history', 'assistant_state'}:
        raise ValueError('Unsupported database name')
    return Path(DATA_USER_DIR) / f'{name}.json'


def _db_read(name):
    path = _db_path(name)
    if not path.exists():
        return {}
    with path.open('r', encoding='utf-8') as file:
        value = json.load(file)
    if not isinstance(value, dict):
        raise ValueError('Database root must be an object')
    return value


def _db_write(name, data):
    path = _db_path(name)
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary = tempfile.mkstemp(prefix=f'.{name}-', suffix='.tmp', dir=path.parent)
    try:
        with os.fdopen(descriptor, 'w', encoding='utf-8') as file:
            json.dump(data, file, indent=4, ensure_ascii=False)
            file.flush()
            os.fsync(file.fileno())
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def db_op(file, data=None):
    """Compatibility read/write entry point; mutations use db_transaction."""
    with _db_lock:
        if data is None:
            return _db_read(file)
        _db_write(file, data)


def db_transaction(name, mutate):
    """Keep the entire read-modify-replace operation within one lock."""
    with _db_lock:
        data = _db_read(name)
        result = mutate(data)
        _db_write(name, data)
        return result


def _public_user(user_id, user):
    return {'u': user_id, 'nick': user.get('nick', '用户'), 'avatar': user.get('avatar', '')}


def _sign_token(user_id):
    return URLSafeTimedSerializer(app.config['SECRET_KEY'], salt='chat-login-v1').dumps({'u': user_id})


def _token_user(token):
    if not isinstance(token, str) or not token or len(token) > 2048:
        return None
    try:
        data = URLSafeTimedSerializer(app.config['SECRET_KEY'], salt='chat-login-v1').loads(
            token, max_age=app.config['AUTH_TOKEN_MAX_AGE']
        )
    except (BadSignature, SignatureExpired):
        return None
    if not isinstance(data, dict) or not isinstance(data.get('u'), str):
        return None
    return data['u'] if data['u'] in db_op('users') else None


def _socket_user(data):
    if not isinstance(data, dict):
        emit('err', '请求格式错误')
        return None
    user_id = _token_user(data.get('token'))
    if not user_id:
        emit('auth_error', '登录已失效，请重新登录')
        return None
    if data.get('u') is not None and data['u'] != user_id:
        emit('err', '不能以其他账号执行操作')
        return None
    return user_id


def _error(message):
    emit('err', message)
    return {'ok': False, 'error': message}


def _valid_text(value, maximum, allow_empty=False):
    return (
        isinstance(value, str)
        and len(value) <= maximum
        and '\x00' not in value
        and (allow_empty or bool(value.strip()))
    )


def _local_media_url(url, allowed_extensions):
    if not isinstance(url, str):
        return False
    match = re.fullmatch(
        r'/static/uploads/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\.([a-z0-9]+)',
        url,
    )
    if not match or match.group(2) not in allowed_extensions:
        return False
    upload_root = Path(app.config['UPLOAD_FOLDER']).resolve()
    path = upload_root / url.rsplit('/', 1)[-1]
    return path.resolve().parent == upload_root and path.is_file()


def _emit_users(room):
    with _presence_lock:
        users = list(online_users.get(room, {}).values())
    socketio.emit('update_users', {'room': room, 'users': users}, to=room, namespace='/')


def bot_reply_callback(room, reply_text, user_nick):
    if room not in ALLOWED_ROOMS or not isinstance(reply_text, str) or not reply_text.strip():
        return
    bot_msg = {
        'id': str(uuid.uuid4()), 'room': room,
        'u': 'bot_system', 'nick': 'AI 助手',
        'avatar': 'https://cdn-icons-png.flaticon.com/512/4712/4712035.png',
        'm': reply_text, 't': 'text', 'quote': {'nick': user_nick, 'm': ''},
        'bot': True, 'time': time.strftime('%H:%M'),
    }
    db_transaction('chat_history', lambda data: data.setdefault(room, []).append(bot_msg))
    socketio.emit('new_msg', bot_msg, to=room, namespace='/')


def bot_status_callback(room, status, message):
    if room in ALLOWED_ROOMS:
        socketio.emit('ai_status', {'room': room, 'status': status, 'message': message}, to=room, namespace='/')


def bot_assistant_callback(room, snapshot):
    """Persist and publish a detached, current revision of one room's task."""
    if (
        room not in ALLOWED_ROOMS or not isinstance(snapshot, dict)
        or snapshot.get('room') != room
        or type(snapshot.get('version')) is not int or snapshot['version'] < 0
    ):
        return
    try:
        # Copy before storage/emission: subsequent in-memory edits must not
        # alter the event already queued for connected clients.
        detached = json.loads(json.dumps(snapshot, ensure_ascii=False, allow_nan=False))
    except (TypeError, ValueError):
        app.logger.error('AI task state is not JSON serializable')
        return
    with _assistant_callback_lock:
        def save_if_current(records):
            previous = records.get(room)
            if (
                isinstance(previous, dict)
                and type(previous.get('version')) is int
                and previous['version'] >= detached['version']
            ):
                return False
            records[room] = detached
            return True
        if db_transaction('assistant_state', save_if_current):
            socketio.emit('assistant_state', detached, to=room, namespace='/')


set_reply_callback(bot_reply_callback)
set_status_callback(bot_status_callback)
set_assistant_callback(bot_assistant_callback)


@app.after_request
def secure_response(response):
    response.headers['X-Content-Type-Options'] = 'nosniff'
    response.headers['Referrer-Policy'] = 'same-origin'
    if request.path == '/upload':
        response.headers['Cache-Control'] = 'no-store'
    return response


@app.route('/')
def index():
    return redirect(url_for('login_page'))


@app.route('/login')
def login_page():
    return render_template('login.html')


@app.route('/register')
def register_page():
    return render_template('register.html')


@app.route('/chat')
def chat_page():
    return render_template('chat.html')


@app.route('/upload', methods=['POST'])
def upload():
    authorization = request.headers.get('Authorization', '')
    if not authorization.startswith('Bearer ') or not _token_user(authorization[7:]):
        return jsonify({'err': '请先登录'}), 401
    file = request.files.get('file')
    if not file or not file.filename:
        return jsonify({'err': '请选择文件'}), 400
    if '.' not in file.filename:
        return jsonify({'err': '文件缺少扩展名'}), 400
    ext = file.filename.rsplit('.', 1)[1].lower()
    if ext not in ALLOWED_UPLOAD_EXT:
        return jsonify({'err': f'不支持该文件类型 .{ext}'}), 400
    os.makedirs(app.config['UPLOAD_FOLDER'], exist_ok=True)
    filename = f'{uuid.uuid4()}.{ext}'
    file.save(os.path.join(app.config['UPLOAD_FOLDER'], filename))
    return jsonify({'url': f'/static/uploads/{filename}', 'ext': ext})


@app.errorhandler(413)
def upload_too_large(error):
    return jsonify({'err': '上传文件不能超过 200 MB'}), 413


@socketio.on('join')
def on_join(data):
    user_id = _socket_user(data)
    if not user_id:
        return
    room = data.get('room', 'Lobby')
    if not isinstance(room, str) or room not in ALLOWED_ROOMS:
        return _error('房间不存在')
    user = db_op('users')[user_id]
    with _presence_lock:
        previous = socket_rooms.get(request.sid)
        if previous:
            online_users.get(previous['room'], {}).pop(request.sid, None)
            if previous['room'] != room:
                leave_room(previous['room'])
        join_room(room)
        socket_rooms[request.sid] = {'room': room, 'u': user_id}
        online_users.setdefault(room, {})[request.sid] = _public_user(user_id, user)
    if previous and previous['room'] != room:
        _emit_users(previous['room'])
    history = db_op('chat_history').get(room, [])
    emit('history', {'room': room, 'messages': history[-30:]})
    # A callback can acquire _db_lock while the bot holds a room lock. Release
    # the DB read lock before restoring or reading any bot state.
    saved_state = db_op('assistant_state').get(room)
    try:
        restore_assistant_state(room, saved_state)
        emit('assistant_state', get_assistant_state(room))
    except Exception as error:
        app.logger.error('AI task state could not be restored: %s', type(error).__name__)
        emit('ai_status', {
            'room': room, 'status': 'error',
            'message': '聊天已连接，但 AI 任务暂时无法加载，请稍后重新进入',
        })
    _emit_users(room)
    return {'ok': True, 'room': room}


@socketio.on('send_msg')
def on_msg(data):
    user_id = _socket_user(data)
    if not user_id:
        return {'ok': False, 'error': '身份验证失败，请重新登录'}
    room = data.get('room')
    with _presence_lock:
        joined = socket_rooms.get(request.sid)
    if not joined or joined['room'] != room or joined['u'] != user_id:
        return _error('请先进入该房间')
    ask_ai = data.get('ask_ai', False)
    if type(ask_ai) is not bool:
        return _error('询问 AI 的选项必须是布尔值')
    msg_type = data.get('t', 'text')
    if not isinstance(msg_type, str) or msg_type not in {'text', 'image', 'video', 'audio'}:
        return _error('不支持该消息类型')
    message = data.get('m', '')
    if not _valid_text(message, MAX_MESSAGE_LENGTH, allow_empty=msg_type != 'text'):
        return _error(f'消息不能为空，且不能超过 {MAX_MESSAGE_LENGTH} 字')
    media_url = data.get('url', '')
    extensions = {'image': IMAGE_EXT, 'video': VIDEO_EXT, 'audio': AUDIO_EXT}
    if msg_type != 'text' and not _local_media_url(media_url, extensions[msg_type]):
        return _error('媒体文件无效，请重新上传')
    if msg_type == 'text':
        media_url = ''
    quote = data.get('quote')
    if quote is not None:
        if (
            not isinstance(quote, dict)
            or not _valid_text(quote.get('nick'), 40)
            or not _valid_text(quote.get('m'), MAX_MESSAGE_LENGTH, allow_empty=True)
        ):
            return _error('引用消息格式错误')
        quote = {'nick': quote['nick'], 'm': quote['m']}
    user = db_op('users')[user_id]
    current_time = time.strftime('%H:%M')
    msg_obj = {
        'id': str(uuid.uuid4()), 'room': room, 'u': user_id,
        'nick': user['nick'], 'avatar': user.get('avatar', ''),
        'm': message, 't': msg_type, 'url': media_url, 'quote': quote,
        'bot': False, 'time': current_time, 'ask_ai': ask_ai,
    }
    db_transaction('chat_history', lambda records: records.setdefault(room, []).append(msg_obj))
    emit('new_msg', msg_obj, to=room)
    try:
        get_bot_decision({
            'msg': message, 'msg_type': msg_type, 'url': media_url,
            'user_nick': user['nick'], 'user_id': user_id, 'send_time': current_time,
            'room': room, 'client_sid': request.sid, 'ask_ai': ask_ai,
        })
    except Exception:
        # The human message is already saved. Report AI failure without asking
        # the sender to retry and accidentally duplicate that saved message.
        app.logger.exception('AI request could not be queued')
        bot_status_callback(room, 'error', '消息已发送，但 AI 暂时无法处理，请稍后重试')
    return {'ok': True, 'id': msg_obj['id']}


@socketio.on('assistant_action')
def on_assistant_action(data):
    user_id = _socket_user(data)
    if not user_id:
        return {'ok': False, 'error': '身份验证失败，请重新登录'}
    room = data.get('room')
    with _presence_lock:
        joined = socket_rooms.get(request.sid)
    if not joined or joined['u'] != user_id or joined['room'] != room:
        return _error('请先进入该房间')
    action = data.get('action')
    if not isinstance(action, str) or action not in {'edit', 'confirm', 'cancel', 'complete', 'new', 'set_auto'}:
        return _error('不支持该 AI 任务操作')
    version = data.get('version')
    if type(version) is not int or version < 0:
        return _error('AI 任务版本无效，请刷新后重试')
    payload = {'action': action, 'version': version}
    if 'task_id' in data:
        if not _valid_text(data['task_id'], 128):
            return _error('AI 任务编号无效')
        payload['task_id'] = data['task_id']
    if action == 'edit':
        fields = data.get('fields')
        known_fields = {'origin', 'destination', 'location', 'time', 'people', 'budget', 'transport', 'cuisine', 'diet'}
        if (
            not isinstance(fields, dict) or not fields or not set(fields).issubset(known_fields)
            or any(not _valid_text(value, 200, allow_empty=True) for value in fields.values())
        ):
            return _error('需求字段无效，每项最多 200 字')
        payload['fields'] = dict(fields)
    elif 'fields' in data:
        return _error('该操作不接受需求字段')
    if action == 'set_auto':
        if type(data.get('auto_detect')) is not bool:
            return _error('自动识别设置必须是布尔值')
        payload['auto_detect'] = data['auto_detect']
    elif 'auto_detect' in data:
        return _error('该操作不接受自动识别设置')
    user = db_op('users')[user_id]
    try:
        # The bot checks version/task_id under its own room lock. Do not add an
        # app-side read/check pair here: another action could race between them.
        result = handle_assistant_action(room, user_id, user['nick'], payload)
    except Exception as error:
        app.logger.error('AI task action failed: %s', type(error).__name__)
        return _error('AI 任务操作暂时失败，请稍后重试')
    if not isinstance(result, dict) or type(result.get('ok')) is not bool:
        return _error('AI 任务操作暂时失败，请稍后重试')
    if result['ok']:
        return {'ok': True}
    message = result.get('error')
    if not _valid_text(message, 300):
        message = 'AI 任务操作未完成，请检查当前需求'
    # Always refresh on a rejected action, including stale revisions. Emit
    # only to this client, not to every other participant in the room.
    try:
        emit('assistant_state', get_assistant_state(room))
    except Exception as error:
        app.logger.error('AI task refresh failed: %s', type(error).__name__)
    return _error(message)


@socketio.on('cancel_ai')
def on_cancel_ai(data):
    user_id = _socket_user(data)
    if not user_id:
        return {'ok': False, 'error': '身份验证失败，请重新登录'}
    with _presence_lock:
        joined = socket_rooms.get(request.sid)
    if not joined or joined['u'] != user_id or joined['room'] != data.get('room'):
        return _error('请先进入该房间')
    cancel_pending(joined['room'])
    return {'ok': True}


@socketio.on('login_action')
def on_login(data):
    if not isinstance(data, dict) or not _valid_text(data.get('u'), 64) or not _valid_text(data.get('p'), 128):
        return _error('账号或密码格式错误')
    user_id, password = data['u'], data['p']
    def authenticate(users):
        user = users.get(user_id)
        if not isinstance(user, dict):
            return None
        password_hash = user.get('password_hash')
        if password_hash:
            valid = check_password_hash(password_hash, password)
        else:
            legacy = user.get('p')
            valid = isinstance(legacy, str) and hmac.compare_digest(
                legacy.encode('utf-8'), password.encode('utf-8')
            )
            if valid:
                user['password_hash'] = generate_password_hash(password)
                user.pop('p', None)
        return _public_user(user_id, user) if valid else None
    user = db_transaction('users', authenticate)
    if user:
        emit('login_ok', {**user, 'token': _sign_token(user_id)})
    else:
        emit('err', '账号或密码错误')


@socketio.on('register_action')
def on_reg(data):
    if not isinstance(data, dict) or not _valid_text(data.get('u'), 64) or not _valid_text(data.get('p'), 128):
        return _error('账号和密码不能为空，账号最多 64 字，密码最多 128 字')
    user_id, password = data['u'], data['p']
    if user_id != user_id.strip() or any(ord(char) < 32 for char in user_id) or user_id == 'bot_system':
        return _error('账号格式无效')
    if len(password) < 6:
        return _error('密码至少需要 6 个字符')
    password_hash = generate_password_hash(password)
    def register(users):
        if user_id in users:
            return False
        users[user_id] = {
            'password_hash': password_hash,
            'nick': f'用户_{random.randint(100, 999)}',
            'avatar': 'https://cdn-icons-png.flaticon.com/512/847/847969.png',
        }
        return True
    if db_transaction('users', register):
        emit('ok', '注册成功')
    else:
        emit('err', '账号已存在')


@socketio.on('update_profile')
def on_update(data):
    user_id = _socket_user(data)
    if not user_id:
        return
    nickname = data.get('nick')
    if not _valid_text(nickname, 40):
        return _error('昵称不能为空，且不能超过 40 字')
    previous_user = db_op('users')[user_id]
    avatar = data.get('avatar')
    if avatar and avatar != previous_user.get('avatar') and not _local_media_url(avatar, IMAGE_EXT):
        return _error('头像文件无效，请重新上传图片')
    def update(users):
        users[user_id]['nick'] = nickname.strip()
        if avatar:
            users[user_id]['avatar'] = avatar
        return _public_user(user_id, users[user_id])
    user = db_transaction('users', update)
    with _presence_lock:
        affected = set()
        for sid, membership in socket_rooms.items():
            if membership['u'] == user_id:
                online_users[membership['room']][sid] = user.copy()
                affected.add(membership['room'])
    emit('update_res', {'status': 'success', 'user': user})
    for room in affected:
        _emit_users(room)


@socketio.on('disconnect')
def on_disconnect():
    with _presence_lock:
        membership = socket_rooms.pop(request.sid, None)
        if membership:
            online_users.get(membership['room'], {}).pop(request.sid, None)
    if membership:
        _emit_users(membership['room'])


if __name__ == '__main__':
    app.config['BOT_CHANNELS'] = ['ZenRoom', 'AI_Lab']
    socketio.run(
        app, host=os.getenv('CHAPTER2_HOST', '127.0.0.1'),
        port=int(os.getenv('CHAPTER2_PORT', '5000')),
        debug=False, allow_unsafe_werkzeug=True,
    )
