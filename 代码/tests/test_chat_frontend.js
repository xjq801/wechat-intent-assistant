'use strict';
// Pure Node DOM/socket doubles: no browser, user records, model, or external API.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const templates = path.join(__dirname, '..', 'templates');

class Element {
    constructor(tag = 'div') {
        this.tagName = tag.toUpperCase();
        this.children = [];
        this.listeners = {};
        this.attributes = {};
        this.style = {};
        this.className = '';
        this.value = '';
        this.files = [];
        this.disabled = false;
        this.hidden = false;
        this._text = '';
        this.classList = {
            add: (...names) => { this.className = [...new Set([...this.className.split(' ').filter(Boolean), ...names])].join(' '); },
            remove: (...names) => { this.className = this.className.split(' ').filter(name => !names.includes(name)).join(' '); },
            toggle: (name, force) => {
                const has = this.className.split(' ').includes(name);
                if (force === true || (force === undefined && !has)) this.classList.add(name);
                else this.classList.remove(name);
            }
        };
    }
    set textContent(value) { this._text = String(value); this.children = []; }
    get textContent() { return this._text + this.children.map(child => child.textContent).join(''); }
    set innerText(value) { this.textContent = value; }
    get innerText() { return this.textContent; }
    set innerHTML(_) { throw new Error('Unsafe innerHTML assignment'); }
    append(...children) { this.children.push(...children); }
    appendChild(child) { this.append(child); }
    replaceChildren(...children) { this._text = ''; this.children = children; }
    setAttribute(name, value) { this.attributes[name] = String(value); }
    addEventListener(name, fn) { (this.listeners[name] ||= []).push(fn); }
    dispatch(name, event = {}) { for (const fn of this.listeners[name] || []) fn.call(this, event); }
    click() { this.dispatch('click'); }
    focus() {}
    scrollTo() {}
    querySelector() { return new Element('i'); }
}

function storage(initial = {}) {
    const values = new Map(Object.entries(initial));
    return {
        getItem: key => values.get(key) ?? null,
        setItem: (key, value) => values.set(key, String(value)),
        removeItem: key => values.delete(key)
    };
}

function scriptsFor(filename) {
    const html = fs.readFileSync(path.join(templates, filename), 'utf8');
    return {html, scripts: [...html.matchAll(/<script(?:\s[^>]*)?>([\s\S]*?)<\/script>/g)].map(match => match[1]).filter(text => text.trim())};
}

function harness(filename = 'chat.html', initial = {chat_u: 'test-user', chat_token: 'fake-signed-token', chat_nick: '测试'}) {
    const {html, scripts} = scriptsFor(filename);
    const elements = new Map([...html.matchAll(/\bid="([^"]+)"/g)].map(match => [match[1], new Element()]));
    const document = {
        getElementById: id => {
            if (!elements.has(id)) throw new Error(`Missing DOM element ${id}`);
            return elements.get(id);
        },
        createElement: tag => new Element(tag),
        querySelectorAll: selector => selector === '.room' ? [elements.get('btn-Lobby'), elements.get('btn-ZenRoom')] : [],
        addEventListener() {}
    };
    const handlers = {};
    const sent = [];
    const socket = {
        connected: false,
        on: (name, fn) => { handlers[name] = fn; },
        emit: (event, payload, callback) => sent.push({event, payload, callback}),
        connect: () => { socket.connected = true; handlers.connect?.(); },
        disconnect: () => { socket.connected = false; handlers.disconnect?.(); }
    };
    const timers = new Map();
    let nextTimer = 1;
    const localStorage = storage(initial);
    const sessionStorage = storage();
    const fetches = [];
    const location = {origin: 'http://127.0.0.1:5000', href: '', replace: url => { location.href = url; }};
    const context = vm.createContext({
        document, localStorage, sessionStorage, URL, console,
        window: {location, innerWidth: 1000, innerHeight: 800},
        io: options => { socket.connected = !options || options.autoConnect !== false; return socket; },
        setTimeout: (fn, ms) => { const id = nextTimer++; timers.set(id, {fn, ms}); return id; },
        clearTimeout: id => timers.delete(id),
        FormData: class { append() {} },
        fetch: async (url, options) => {
            fetches.push({url, options});
            return {ok: true, status: 200, json: async () => ({url: '/static/uploads/test-upload.png'})};
        }
    });
    if (filename === 'chat.html') {
        vm.runInContext(fs.readFileSync(path.join(templates, '..', 'static', 'assistant.js'), 'utf8'), context);
    }
    vm.runInContext(scripts.at(-1), context);
    return {context, elements, document, handlers, sent, socket, timers, localStorage, sessionStorage, location, fetches,
        run: source => vm.runInContext(source, context)};
}

function joined(h) {
    h.handlers.history({room: 'Lobby', messages: []});
}
function walk(el) { return [el, ...el.children.flatMap(walk)]; }
function message(overrides = {}) {
    return {id: 'm1', room: 'Lobby', u: 'other', nick: '小王', avatar: '', m: '你好', t: 'text', time: '12:00', ...overrides};
}

test('all inline scripts parse; chat does not use HTML sinks or inline handlers', () => {
    for (const filename of ['chat.html', 'login.html', 'register.html']) {
        const {html, scripts} = scriptsFor(filename);
        scripts.forEach(source => new vm.Script(source, {filename}));
        if (filename === 'chat.html') {
            assert.doesNotMatch(html, /\binnerHTML\b|\bouterHTML\b|\binsertAdjacentHTML\b|\bon(?:click|change|error|load)=/i);
        }
    }
});

test('URLs reject executable and mismatched media while preserving safe local and HTTPS images', () => {
    const h = harness();
    const unsafe = ['javascript:alert(1)', 'data:image/svg+xml,<svg onload=alert(1)>', 'https://example.org/a.svg',
        'https://example.org/a.%73vg', '//example.org/a.png', '/static/uploads/../evil.png',
        'https://user:pass@example.org/a.png', 'http://example.org/a.png', '/static/uploads/f.png?x=1'];
    for (const url of unsafe) {
        h.context.candidate = url;
        assert.equal(h.run('safeMediaUrl(candidate)'), '', url);
    }
    assert.equal(h.run("safeMediaUrl('/static/uploads/a-b_c.png')"), '/static/uploads/a-b_c.png');
    assert.equal(h.run("safeMediaUrl('https://example.org/a.png')"), 'https://example.org/a.png');
    assert.equal(h.run("safeMediaUrl('https://example.org/a.mp3', 'image')"), '');
});

test('stored message, nickname, quote and timestamp attacks remain literal text; reply uses event binding', () => {
    const h = harness();
    joined(h);
    const attack = `<img src=x onerror="globalThis.pwned=1"><script>globalThis.pwned=2</script>'"\n`;
    h.handlers.new_msg(message({m: attack, nick: attack, time: attack, quote: {nick: attack, m: attack}, avatar: 'javascript:alert(1)'}));
    const nodes = walk(h.elements.get('msgs'));
    assert.equal(nodes.some(el => el.tagName === 'SCRIPT'), false);
    assert.equal(nodes.filter(el => el.tagName === 'IMG').length, 1, 'only the explicit avatar image exists');
    assert.match(h.elements.get('msgs').textContent, /<script>/);
    assert.equal(nodes.some(el => Object.keys(el.attributes).some(name => /^on/i.test(name))), false);
    nodes.find(el => el.tagName === 'BUTTON').click();
    assert.equal(h.elements.get('replyText').textContent, attack);
    assert.equal(h.context.pwned, undefined);
});

test('room switch rejects stale history, messages, online users and AI status', () => {
    const h = harness();
    joined(h);
    h.run("chRoom(byId('btn-ZenRoom'), 'ZenRoom')");
    h.handlers.history({room: 'Lobby', messages: [message()]});
    h.handlers.new_msg(message());
    h.handlers.update_users({room: 'Lobby', users: [{nick: '不应出现'}]});
    h.handlers.ai_status({room: 'Lobby', status: 'error', message: '旧房间错误'});
    assert.equal(h.elements.get('msgs').children.length, 0);
    assert.equal(h.elements.get('user-list').children.length, 0);
    assert.notEqual(h.elements.get('chat-status').textContent, '旧房间错误');
    h.handlers.history({room: 'ZenRoom', messages: [message({room: 'ZenRoom'})]});
    assert.match(h.elements.get('msgs').textContent, /你好/);
    assert.equal(h.sent.at(-1).payload.room, 'ZenRoom');
    assert.equal(h.sent.at(-1).payload.token, 'fake-signed-token');
});

test('live messages arriving before history survive its older snapshot', () => {
    const h = harness();
    h.handlers.new_msg(message({id: 'new-after-snapshot', m: '先收到的实时消息'}));
    assert.equal(h.elements.get('msgs').children.length, 0, 'wait until the initial snapshot is loaded');
    h.handlers.history({room: 'Lobby', messages: [message({id: 'old', m: '较早的历史消息'})]});
    const text = h.elements.get('msgs').textContent;
    assert.match(text, /较早的历史消息.*先收到的实时消息/);
});

test('buffered live messages already in history are rendered only once by id', () => {
    const h = harness();
    const same = message({id: 'same-message', m: '只能出现一次'});
    h.handlers.new_msg(same);
    h.handlers.history({room: 'Lobby', messages: [same]});
    assert.equal(h.elements.get('msgs').textContent.split('只能出现一次').length - 1, 1);
});

test('room switches and disconnect discard pending messages and AI state', () => {
    const h = harness();
    h.handlers.new_msg(message({m: '旧房间缓冲'}));
    h.handlers.ai_status({room: 'Lobby', status: 'generating', message: '旧房间生成中'});
    h.run("chRoom(byId('btn-ZenRoom'), 'ZenRoom')");
    h.handlers.history({room: 'ZenRoom', messages: []});
    assert.equal(h.elements.get('msgs').textContent, '');
    assert.notEqual(h.elements.get('chat-status').textContent, '旧房间生成中');
    h.socket.disconnect();
    h.socket.connect();
    h.handlers.new_msg(message({room: 'ZenRoom', m: '断线前缓冲'}));
    h.socket.disconnect();
    h.socket.connect();
    h.handlers.history({room: 'ZenRoom', messages: []});
    assert.equal(h.elements.get('msgs').textContent, '');
});

test('AI busy status received while loading history remains visible afterwards', () => {
    const h = harness();
    h.handlers.ai_status({room: 'Lobby', status: 'generating', message: '正在生成建议'});
    h.handlers.history({room: 'Lobby', messages: []});
    assert.equal(h.elements.get('chat-status').textContent, '正在生成建议');
    assert.equal(h.elements.get('cancel-ai').hidden, false);
});

test('send includes token; ACK alone clears unchanged draft; errors preserve content', () => {
    const h = harness();
    joined(h);
    h.elements.get('mi').value = '今晚吃饭';
    h.run('send()');
    const first = h.sent.at(-1);
    assert.equal(first.event, 'send_msg');
    assert.equal(first.payload.token, 'fake-signed-token');
    assert.equal(h.elements.get('mi').value, '今晚吃饭');
    h.run('send()');
    assert.equal(h.sent.at(-1), first, 'pending send prevents duplicate clicks');
    first.callback({ok: true});
    assert.equal(h.elements.get('mi').value, '');
    h.elements.get('mi').value = '别丢失这条';
    h.run('send()');
    h.sent.at(-1).callback({ok: false, error: '失败'});
    assert.equal(h.elements.get('mi').value, '别丢失这条');
    h.run('send()');
    h.elements.get('mi').value = '发出后又编辑了';
    h.sent.at(-1).callback({ok: true});
    assert.equal(h.elements.get('mi').value, '发出后又编辑了');
});

test('Chinese IME confirmation does not send; disconnect disables send without deleting draft', () => {
    const h = harness();
    joined(h);
    h.elements.get('mi').value = '输入中';
    const before = h.sent.length;
    h.elements.get('mi').dispatch('keydown', {key: 'Enter', isComposing: true, preventDefault: () => assert.fail('IME interrupted')});
    assert.equal(h.sent.length, before);
    h.socket.disconnect();
    assert.equal(h.elements.get('send-message').disabled, true);
    assert.equal(h.elements.get('connection-status').textContent, '未连接');
    assert.equal(h.elements.get('mi').value, '输入中');
});

test('AI status safely renders text and exposes authenticated stop action only while running', () => {
    const h = harness();
    joined(h);
    h.handlers.ai_status({room: 'Lobby', status: 'generating', message: '<img src=x onerror=alert(1)>'});
    assert.equal(h.elements.get('cancel-ai').hidden, false);
    assert.equal(h.elements.get('chat-status').textContent, '<img src=x onerror=alert(1)>');
    h.elements.get('cancel-ai').click();
    const sent = h.sent.at(-1);
    assert.equal(sent.event, 'cancel_ai');
    assert.equal(sent.payload.token, 'fake-signed-token');
    h.handlers.ai_status({room: 'Lobby', status: 'cancelled', message: '已停止'});
    assert.equal(h.elements.get('cancel-ai').hidden, true);
    assert.equal(h.elements.get('chat-status').textContent, '已停止');
});

test('upload carries Bearer credential and rejects SVG client-side', async () => {
    const h = harness();
    await h.run("upload({name: 'test.png'})");
    assert.equal(h.fetches[0].options.headers.Authorization, 'Bearer fake-signed-token');
    assert.equal(h.run("detectUploadType({name:'evil.svg', type:'image/svg+xml'})"), null);
});

test('old unauthenticated localStorage is cleared and redirected without joining', () => {
    const h = harness('chat.html', {chat_u: 'old-user'});
    assert.equal(h.location.href, '/login');
    assert.equal(h.sent.length, 0);
    assert.equal(h.localStorage.getItem('chat_u'), null);
    assert.ok(h.sessionStorage.getItem('chat_login_notice'));
});

test('login stores signed token and connection failure releases loading buttons', () => {
    const h = harness('login.html', {});
    h.elements.get('username').value = 'test-user';
    h.elements.get('password').value = 'fixture-password';
    h.elements.get('login-form').dispatch('submit', {preventDefault() {}});
    assert.equal(h.elements.get('login-btn').disabled, true);
    h.handlers.connect_error();
    assert.equal(h.elements.get('login-btn').disabled, false);
    h.handlers.login_ok({u: 'test-user', nick: '测试', avatar: '', token: 'signed-token'});
    assert.equal(h.localStorage.getItem('chat_token'), 'signed-token');
});

function assistantState(overrides = {}) {
    return {
        room: 'Lobby', version: 1, auto_detect: true,
        task: {
            id: 'task-1', intent: '出行', status: 'ready', owner: '测试',
            fields: {origin: '北京', destination: '上海', time: '明天上午', people: '2', budget: '600 元', transport: '高铁'},
            missing: [], conflicts: [], summary: '明天上午北京去上海', clarification: ''
        },
        proposals: [], trace: [], last_error: '', ...overrides
    };
}
function taskInputs(h) {
    return Object.fromEntries(walk(h.elements.get('task-fields')).filter(node => node.tagName === 'INPUT').map(node => [node.attributes['data-field'], node]));
}

test('explicit AI button and @AI prefix carry ask_ai; ordinary sending remains ordinary', () => {
    const h = harness();
    joined(h);
    h.elements.get('mi').value = '有什么要注意的？';
    h.elements.get('ask-ai').click();
    assert.equal(h.sent.at(-1).payload.ask_ai, true);
    h.sent.at(-1).callback({ok: true});
    h.elements.get('mi').value = '@AI 明天出行需要带什么？';
    h.run('send()');
    assert.equal(h.sent.at(-1).payload.ask_ai, true);
    h.sent.at(-1).callback({ok: true});
    h.elements.get('mi').value = '好的，明天见';
    h.run('send()');
    assert.equal(h.sent.at(-1).payload.ask_ai, false);
});

test('assistant rejects cross-room and stale versions and replaces state on room switch', () => {
    const h = harness();
    joined(h);
    h.handlers.assistant_state(assistantState({version: 5}));
    h.handlers.assistant_state(assistantState({version: 4, task: null}));
    h.handlers.assistant_state(assistantState({room: 'ZenRoom', version: 100, task: null}));
    assert.equal(h.elements.get('assistant-task').hidden, false);
    assert.equal(h.elements.get('task-summary').textContent, '明天上午北京去上海');
    h.run("chRoom(byId('btn-ZenRoom'), 'ZenRoom')");
    assert.equal(h.elements.get('assistant-task').hidden, true);
    h.handlers.assistant_state(assistantState({room: 'ZenRoom', version: 1, task: null}));
    assert.equal(h.elements.get('assistant-badge').textContent, '等待需求');
    h.handlers.history({room: 'ZenRoom', messages: []});
    assert.equal(h.elements.get('auto-detect').disabled, false);
});

test('missing fields and conflict values block confirmation; edits include signed version and explicit empty', () => {
    const h = harness();
    joined(h);
    const state = assistantState();
    state.task.status = 'needs_info';
    state.task.fields.time = '';
    state.task.missing = ['time'];
    state.task.conflicts = [{field: 'budget', values: ['300 元', '600 元'], message: '成员预算意见不同'}];
    h.handlers.assistant_state(state);
    assert.equal(h.elements.get('task-confirm').disabled, true);
    assert.match(h.elements.get('task-clarification').textContent, /时间/);
    const inputs = taskInputs(h);
    inputs.time.value = '后天上午';
    inputs.time.dispatch('input');
    inputs.transport.value = '';
    inputs.transport.dispatch('input');
    walk(h.elements.get('task-conflicts')).find(node => node.tagName === 'BUTTON').click();
    assert.equal(inputs.budget.value, '300 元');
    h.elements.get('task-save').click();
    const request = h.sent.at(-1);
    assert.equal(request.event, 'assistant_action');
    assert.equal(request.payload.action, 'edit');
    assert.equal(request.payload.token, 'fake-signed-token');
    assert.equal(request.payload.version, 1);
    assert.equal(request.payload.task_id, 'task-1');
    assert.equal(request.payload.fields.time, '后天上午');
    assert.equal(request.payload.fields.budget, '300 元');
    assert.equal(request.payload.fields.transport, '');
});

test('incoming collaborative revision preserves local draft but prevents overwriting newer fields', () => {
    const h = harness();
    joined(h);
    h.handlers.assistant_state(assistantState());
    const input = taskInputs(h).budget;
    input.value = '300 元';
    input.dispatch('input');
    const revised = assistantState({version: 2});
    revised.task.fields.budget = '500 元';
    h.handlers.assistant_state(revised);
    assert.equal(taskInputs(h).budget.value, '300 元');
    assert.equal(h.elements.get('task-save').disabled, true);
    assert.match(h.elements.get('task-edit-hint').textContent, /共享需求已更新/);
    h.elements.get('task-reload').click();
    assert.equal(taskInputs(h).budget.value, '500 元');
    assert.equal(h.elements.get('task-confirm').disabled, false);
});

test('assistant operations debounce; revision errors are visible and latest state remains usable', () => {
    const h = harness();
    joined(h);
    h.handlers.assistant_state(assistantState());
    h.elements.get('task-confirm').click();
    const first = h.sent.at(-1);
    h.elements.get('task-confirm').click();
    assert.equal(h.sent.at(-1), first);
    assert.equal(first.payload.action, 'confirm');
    h.handlers.assistant_state(assistantState({version: 2}));
    first.callback({ok: false, error: '需求已更新，请重新确认'});
    assert.match(h.elements.get('assistant-notice').textContent, /需求已更新/);
    assert.equal(h.elements.get('task-confirm').disabled, false);
    h.elements.get('task-confirm').click();
    assert.equal(h.sent.at(-1).payload.version, 2);
});

test('automatic detection setting is scoped to current room and failed ACK restores checkbox', () => {
    const h = harness();
    joined(h);
    h.handlers.assistant_state(assistantState({task: null}));
    h.elements.get('auto-detect').checked = false;
    h.elements.get('auto-detect').dispatch('change');
    const request = h.sent.at(-1);
    assert.equal(request.payload.action, 'set_auto');
    assert.equal(request.payload.room, 'Lobby');
    assert.equal(request.payload.auto_detect, false);
    request.callback({ok: false, error: '暂时无法保存'});
    assert.equal(h.elements.get('auto-detect').checked, true);
    h.handlers.assistant_state(assistantState({version: 2, auto_detect: false, task: null}));
    assert.match(h.elements.get('auto-detect-help').textContent, /普通消息只发送到群聊/);
});

test('proposals, clarification and trace render attacks literally and disclose unverified live information', () => {
    const h = harness();
    joined(h);
    const attack = '<img src=x onerror=alert(1)><script>bad()</script>';
    const state = assistantState({
        proposals: [1, 2, 3].map(id => ({id, title: attack, description: attack, reason: attack, budget_fit: attack, tradeoff: attack, verify: attack})),
        trace: [{stage: 'intent', source: 'local_lora', duration_ms: 1250, summary: attack}]
    });
    state.task.summary = attack;
    state.task.clarification = attack;
    h.handlers.assistant_state(state);
    assert.equal(h.elements.get('proposal-section').hidden, false);
    assert.equal(h.elements.get('proposal-list').children.length, 3);
    assert.equal(walk(h.elements.get('proposal-list')).some(node => ['IMG', 'SCRIPT'].includes(node.tagName)), false);
    assert.match(h.elements.get('proposal-list').textContent, /<script>/);
    assert.match(h.elements.get('trace-list').textContent, /本地模型/);
    assert.match(h.elements.get('trace-list').textContent, /1.3 秒/);
    assert.match(scriptsFor('chat.html').html, /实时价格、余票和营业情况均需另行核实/);
});

test('disconnect retains edited task values and disables every network action', () => {
    const h = harness();
    joined(h);
    h.handlers.assistant_state(assistantState());
    taskInputs(h).budget.value = '300 元';
    taskInputs(h).budget.dispatch('input');
    h.socket.disconnect();
    assert.equal(taskInputs(h).budget.value, '300 元');
    for (const id of ['task-save', 'task-confirm', 'task-cancel', 'auto-detect', 'ask-ai']) assert.equal(h.elements.get(id).disabled, true, id);
    h.socket.connect();
    h.handlers.history({room: 'Lobby', messages: []});
    h.handlers.assistant_state(assistantState());
    assert.equal(taskInputs(h).budget.value, '300 元', 'reconnecting does not discard a field draft');
});

test('drawer and examples are local UI actions; examples never send automatically', () => {
    const h = harness();
    joined(h);
    const count = h.sent.length;
    h.elements.get('toggle-assistant').click();
    assert.equal(h.elements.get('assistant-backdrop').hidden, false);
    h.elements.get('close-assistant').click();
    assert.equal(h.elements.get('assistant-backdrop').hidden, true);
    h.elements.get('example-travel').click();
    assert.match(h.elements.get('mi').value, /明天上午从北京去上海/);
    assert.equal(h.sent.length, count);
});

test('completed tasks are read only, can start new context, and action timeout releases controls', () => {
    const h = harness();
    joined(h);
    const state = assistantState();
    state.task.status = 'completed';
    h.handlers.assistant_state(state);
    assert.equal(h.elements.get('task-confirm').disabled, true);
    assert.equal(taskInputs(h).time.disabled, true);
    h.elements.get('task-new').click();
    assert.equal(h.sent.at(-1).payload.action, 'new');
    [...h.timers.values()].find(timer => timer.ms === 15000).fn();
    assert.match(h.elements.get('assistant-notice').textContent, /尚未收到操作确认/);
    assert.equal(h.elements.get('task-new').disabled, false);
});

test('process source distinguishes local rules from local models and cloud calls', () => {
    const h = harness();
    joined(h);
    h.handlers.assistant_state(assistantState({trace: [
        {stage: '提取需求', source: '本地规则', duration_ms: 1, summary: '仅保留原文信息'},
        {stage: '生成方案', source: '本地通用规划', duration_ms: 2, summary: '离线备选'},
        {stage: '提取需求', source: '本地规则 + 千问云端（原文证据校验）', duration_ms: 300, summary: '联合校验'}
    ]}));
    const text = h.elements.get('trace-list').textContent;
    assert.match(text, /本地规则/);
    assert.match(text, /本地通用规划/);
    assert.match(text, /本地规则 \+ 千问云端（原文证据校验）/);
    assert.doesNotMatch(text, /本地模型/);
});

test('saving one edited field never sends untouched conflicting fields', () => {
    const h = harness();
    joined(h);
    const state = assistantState();
    state.task.intent = '吃饭';
    state.task.status = 'needs_info';
    state.task.fields = {location: '北京', time: '今晚', people: '2', budget: '100 元', cuisine: '川菜', diet: ''};
    state.task.conflicts = [
        {field: 'budget', values: ['100 元', '200 元'], message: '预算不同'},
        {field: 'cuisine', values: ['川菜', '粤菜'], message: '菜系不同'}
    ];
    h.handlers.assistant_state(state);
    taskInputs(h).people.value = '3';
    taskInputs(h).people.dispatch('input');
    h.elements.get('task-save').click();
    const fields = h.sent.at(-1).payload.fields;
    assert.deepEqual(Object.keys(fields), ['people']);
    assert.equal(fields.people, '3');
    assert.equal(Object.hasOwn(fields, 'budget'), false);
    assert.equal(Object.hasOwn(fields, 'cuisine'), false);
});

test('explicitly selecting an existing conflict value still submits that field as a resolution', () => {
    const h = harness();
    joined(h);
    const state = assistantState();
    state.task.status = 'needs_info';
    state.task.conflicts = [{field: 'budget', values: ['600 元', '300 元'], message: '预算不同'}];
    h.handlers.assistant_state(state);
    assert.equal(taskInputs(h).budget.value, '600 元');
    walk(h.elements.get('task-conflicts')).find(node => node.tagName === 'BUTTON').click();
    h.elements.get('task-save').click();
    const fields = h.sent.at(-1).payload.fields;
    assert.deepEqual(Object.keys(fields), ['budget']);
    assert.equal(fields.budget, '600 元');
});

test('pending conversational update blocks stale card editing and confirmation while allowing cancellation or a new task', () => {
    const h = harness();
    joined(h);
    h.handlers.assistant_state(assistantState());
    taskInputs(h).people.value = '3';
    taskInputs(h).people.dispatch('input');
    const state = assistantState({version: 2});
    state.task.status = 'draft';
    h.handlers.assistant_state(state);
    assert.equal(h.elements.get('assistant-badge').textContent, '整理需求');
    assert.equal(h.elements.get('task-confirm').disabled, true);
    assert.equal(h.elements.get('task-save').disabled, true);
    assert.equal(taskInputs(h).people.disabled, true);
    assert.equal(h.elements.get('task-cancel').disabled, false);
    assert.equal(h.elements.get('task-new').disabled, false);
    h.elements.get('task-cancel').click();
    assert.equal(h.sent.at(-1).payload.action, 'cancel');
});
