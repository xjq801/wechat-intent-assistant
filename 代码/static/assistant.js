(function () {
    'use strict';
    const LABELS = {
        origin: '出发地', destination: '目的地', location: '用餐地点', time: '时间',
        people: '人数', budget: '预算', transport: '交通方式', cuisine: '菜系偏好', diet: '忌口 / 饮食要求'
    };
    const FIELDS = {
        '出行': ['origin', 'destination', 'time', 'people', 'budget', 'transport'],
        '吃饭': ['location', 'time', 'people', 'budget', 'cuisine', 'diet']
    };
    const REQUIRED = {'出行': ['origin', 'destination', 'time'], '吃饭': ['location', 'time', 'people']};
    const STATUS = {
        draft: '整理需求', needs_info: '需要补充', ready: '待你确认', generating: '生成方案中',
        done: '方案已生成', cancelled: '已取消', completed: '已完成', error: '需要重试'
    };
    const STAGE = {
        intent: '识别意图', extraction: '提取需求', extract: '提取需求', clarification: '检查需求',
        edit: '人工修改', confirm: '确认需求', proposals: '生成方案', recommendation: '生成方案',
        question: '回答问题', answer: '回答问题', cancel: '停止任务', complete: '完成任务',
        error: '处理失败', restore: '恢复任务'
    };
    const byId = id => document.getElementById(id);
    function el(tag, className, text) {
        const element = document.createElement(tag);
        element.className = className || '';
        if (text !== undefined) element.textContent = String(text ?? '');
        return element;
    }
    function duration(value) {
        if (!Number.isFinite(Number(value)) || Number(value) < 0) return '';
        return Number(value) < 1000 ? `${Math.round(Number(value))} 毫秒` : `${(Number(value) / 1000).toFixed(1)} 秒`;
    }
    function sourceLabel(value) {
        const text = String(value || '');
        // The server's Chinese labels distinguish models, rules and storage.
        if (/[\u3400-\u9fff]/.test(text)) return text;
        if (/cloud|qwen3\.8|云端/i.test(text)) return '云端千问';
        if (/local|lora|本地/i.test(text)) return '本地模型';
        if (/rule|offline|规则|离线/i.test(text)) return '规则处理';
        if (/user|manual|人工/i.test(text)) return '用户操作';
        return text || '系统';
    }
    window.createAssistantUI = function (options) {
        let room = options.getRoom(), state = null, pending = null;
        let draft = {}, dirty = false, draftVersion = null, draftTask = null;
        const touchedKeys = new Set();
        let fieldInputs = {}, controls = [];
        const versions = new Map();
        function notice(text, error = false) {
            byId('assistant-notice').textContent = String(text || '');
            byId('assistant-notice').className = error ? 'assistant-notice is-error' : 'assistant-notice';
        }
        function setOpen(open) {
            byId('assistant-panel').classList.toggle('is-open', open);
            byId('assistant-backdrop').hidden = !open;
            byId('toggle-assistant').setAttribute('aria-expanded', String(open));
        }
        function activeTask() {
            return state && state.task && !['cancelled', 'completed'].includes(state.task.status);
        }
        function refreshControls() {
            const connected = options.isReady(), blocked = !connected || !!pending || !state;
            const task = state && state.task;
            const stale = dirty && draftVersion !== (state && state.version);
            byId('auto-detect').disabled = blocked;
            byId('task-save').disabled = blocked || !dirty || stale || !activeTask() || ['draft', 'generating'].includes(task.status);
            byId('task-confirm').disabled = blocked || dirty || !activeTask() ||
                ['draft', 'generating', 'done'].includes(task.status) ||
                (task.missing || []).length > 0 || (task.conflicts || []).length > 0;
            byId('task-cancel').disabled = blocked || !activeTask();
            byId('task-complete').disabled = blocked || !task || task.status !== 'done';
            byId('task-new').disabled = blocked;
            byId('task-reload').hidden = !dirty;
            byId('task-reload').disabled = blocked;
            byId('task-edit-hint').textContent = stale
                ? '共享需求已更新。请重新载入后修改，避免覆盖其他成员的内容。'
                : dirty ? '修改尚未保存。保存后，可重新确认并生成方案。' : '';
            byId('task-edit-hint').className = stale ? 'field-edit-hint is-error' : 'field-edit-hint';
            controls.forEach(input => { input.disabled = blocked || !activeTask() || ['draft', 'generating'].includes(task.status); });
        }
        function finishPending() {
            if (pending) clearTimeout(pending.timer);
            pending = null;
        }
        function resetDraft() {
            draft = {...((state && state.task && state.task.fields) || {})};
            dirty = false;
            touchedKeys.clear();
            draftVersion = state && state.version;
            draftTask = state && state.task && state.task.id;
        }
        function action(name, extra = {}) {
            if (!state || pending || !options.isReady()) return;
            if (name === 'edit' && dirty && draftVersion !== state.version) {
                notice('需求已被更新，请先重新载入最新内容。', true);
                return;
            }
            const request = {
                room, token: options.token, version: state.version, action: name,
                ...(state.task ? {task_id: state.task.id} : {}), ...extra
            };
            const current = {room, name};
            pending = current;
            current.timer = setTimeout(() => {
                if (pending !== current) return;
                finishPending();
                notice('尚未收到操作确认，请检查任务状态后再试。', true);
                refreshControls();
            }, 15000);
            notice('正在保存…');
            refreshControls();
            options.socket.emit('assistant_action', request, result => {
                if (pending !== current || room !== current.room) return;
                finishPending();
                if (!result || result.ok !== true) {
                    notice(result && result.error || '操作未成功，请重试。', true);
                    byId('auto-detect').checked = state.auto_detect !== false;
                } else {
                    if (name === 'edit') {
                        resetDraft();
                        renderFields();
                    }
                    const messages = {
                        edit: '需求已保存，请确认后生成方案。', confirm: '正在生成可比较的方案…',
                        cancel: '任务已取消。', complete: '任务已完成，可以开始新安排。',
                        new: '已开始新的需求整理。', set_auto: '本群的自动识别设置已更新。'
                    };
                    notice(messages[name] || '已更新。');
                }
                refreshControls();
            });
        }
        function renderFields() {
            fieldInputs = {};
            controls = [];
            const task = state && state.task;
            if (!task) {
                byId('task-fields').replaceChildren();
                return;
            }
            const keys = FIELDS[task.intent] || Object.keys(LABELS);
            const items = keys.map(key => {
                const wrap = el('label', 'need-field');
                const label = el('span', 'need-field-label', LABELS[key]);
                if ((REQUIRED[task.intent] || []).includes(key)) label.append(el('span', 'required-mark', '必填'));
                const input = el('input', 'need-field-input');
                input.type = 'text';
                input.maxLength = 200;
                input.value = String(draft[key] || '');
                input.placeholder = (REQUIRED[task.intent] || []).includes(key) ? '待补充' : '未提供（可选）';
                input.setAttribute('aria-label', LABELS[key]);
                input.setAttribute('data-field', key);
                if ((task.missing || []).includes(key)) input.classList.add('is-missing');
                if ((task.conflicts || []).some(conflict => conflict.field === key)) input.classList.add('is-conflict');
                input.addEventListener('input', () => {
                    draft[key] = input.value;
                    touchedKeys.add(key);
                    dirty = true;
                    refreshControls();
                });
                fieldInputs[key] = input;
                controls.push(input);
                wrap.append(label, input);
                return wrap;
            });
            byId('task-fields').replaceChildren(...items);
        }
        function renderConflicts() {
            const conflicts = state && state.task && state.task.conflicts || [];
            const rows = conflicts.map(conflict => {
                const item = el('div', 'conflict-item');
                item.append(el('strong', '', `${LABELS[conflict.field] || '需求'}存在不同意见`));
                item.append(el('p', '', conflict.message || '请与群成员确认，再选择或修改。'));
                const choices = el('div', 'conflict-choices');
                (Array.isArray(conflict.values) ? conflict.values : []).forEach(value => {
                    const button = el('button', 'conflict-choice', value);
                    button.type = 'button';
                    button.addEventListener('click', () => {
                        if (!fieldInputs[conflict.field] || button.disabled) return;
                        draft[conflict.field] = String(value).slice(0, 200);
                        touchedKeys.add(conflict.field);
                        fieldInputs[conflict.field].value = draft[conflict.field];
                        dirty = true;
                        notice('已填入所选意见。保存需求后，系统会解除这一项冲突。');
                        refreshControls();
                    });
                    controls.push(button);
                    choices.append(button);
                });
                item.append(choices);
                return item;
            });
            byId('task-conflicts').replaceChildren(...rows);
            byId('task-conflicts').hidden = !rows.length;
        }
        function renderProposals() {
            const proposals = Array.isArray(state && state.proposals) ? state.proposals.slice(0, 3) : [];
            const cards = proposals.map((proposal, index) => {
                const card = el('article', 'proposal-card');
                const header = el('div', 'proposal-header');
                header.append(el('span', 'proposal-number', String(index + 1).padStart(2, '0')));
                header.append(el('h4', '', proposal.title || `方案 ${index + 1}`));
                card.append(header, el('p', 'proposal-description', proposal.description));
                const grid = el('dl', 'proposal-details');
                [['匹配理由', proposal.reason], ['预算匹配', proposal.budget_fit],
                    ['需要取舍', proposal.tradeoff], ['需再核实', proposal.verify]].forEach(([label, value]) => {
                    const row = el('div', '');
                    row.append(el('dt', '', label), el('dd', '', value || '暂无信息，需要进一步核实'));
                    grid.append(row);
                });
                card.append(grid);
                return card;
            });
            byId('proposal-list').replaceChildren(...cards);
            byId('proposal-section').hidden = !cards.length;
        }
        function renderTrace() {
            const trace = Array.isArray(state && state.trace) ? state.trace.slice(-16) : [];
            byId('trace-count').textContent = String(trace.length);
            const rows = trace.map(item => {
                const row = el('li', 'trace-item');
                const header = el('div', 'trace-item-head');
                header.append(el('strong', '', STAGE[item.stage] || item.stage || '处理记录'));
                header.append(el('span', '', duration(item.duration_ms)));
                row.append(header, el('span', 'trace-source', sourceLabel(item.source)));
                row.append(el('p', '', item.summary));
                return row;
            });
            if (!rows.length) rows.push(el('li', 'trace-empty', '处理需求后，将显示步骤、来源与耗时。'));
            byId('trace-list').replaceChildren(...rows);
        }
        function render() {
            if (!state) return;
            const task = state.task;
            byId('auto-detect').checked = state.auto_detect !== false;
            byId('auto-detect-help').textContent = state.auto_detect === false
                ? '普通消息只发送到群聊；点击“询问 AI”可单独提问。'
                : '自动整理吃饭、出行需求；确认后才生成方案。';
            byId('composer-mode-hint').textContent = state.auto_detect === false
                ? '普通发送 · 自动识别已关闭' : '普通发送 · 自动整理需求';
            byId('assistant-empty').hidden = !!task;
            byId('assistant-task').hidden = !task;
            byId('assistant-badge').textContent = task ? STATUS[task.status] || '整理中' : '等待需求';
            byId('assistant-badge').className = `assistant-badge state-${task ? task.status : 'empty'}`;
            byId('assistant-dot').classList.toggle('has-task', !!activeTask());
            if (task) {
                byId('task-title').textContent = task.intent === '吃饭' ? '一起吃顿饭' : task.intent === '出行' ? '安排一次出行' : '当前需求';
                byId('task-summary').textContent = task.summary || '补充下面的需求，确认后获取建议。';
                byId('task-owner').textContent = task.owner ? `由 ${task.owner} 发起 · 本群共享` : '本群共享需求';
                const missing = (task.missing || []).map(key => LABELS[key] || key);
                const clarification = task.clarification || (missing.length ? `还需要补充：${missing.join('、')}。` : '');
                byId('task-clarification').textContent = clarification;
                byId('task-clarification').hidden = !clarification;
                byId('task-confirm').textContent = task.status === 'error' ? '重新生成方案' : '确认并生成方案';
                renderFields();
                renderConflicts();
            }
            renderProposals();
            renderTrace();
            if (state.last_error) notice(state.last_error, /失败|不可用|错误|未通过|无效/.test(state.last_error));
            else if (!pending) notice('');
            refreshControls();
        }
        function accept(data) {
            if (!data || data.room !== room || !Number.isSafeInteger(data.version) || data.version < 0) return false;
            const known = versions.get(room);
            if (known !== undefined && data.version < known) return false;
            versions.set(room, data.version);
            const taskId = data.task && data.task.id;
            state = data;
            if (!dirty || taskId !== draftTask || (pending && pending.name === 'edit')) resetDraft();
            render();
            return true;
        }
        function setRoom(nextRoom) {
            finishPending();
            room = nextRoom;
            state = null;
            draft = {};
            dirty = false;
            touchedKeys.clear();
            draftVersion = null;
            draftTask = null;
            fieldInputs = {};
            controls = [];
            byId('assistant-empty').hidden = false;
            byId('assistant-task').hidden = true;
            byId('proposal-section').hidden = true;
            byId('assistant-badge').textContent = '正在同步';
            byId('assistant-badge').className = 'assistant-badge';
            byId('assistant-dot').classList.remove('has-task');
            byId('trace-list').replaceChildren(el('li', 'trace-empty', '正在同步本群的处理记录…'));
            byId('trace-count').textContent = '0';
            notice('');
            refreshControls();
        }
        byId('toggle-assistant').addEventListener('click', () => setOpen(true));
        byId('close-assistant').addEventListener('click', () => setOpen(false));
        byId('assistant-backdrop').addEventListener('click', () => setOpen(false));
        document.addEventListener('keydown', event => { if (event.key === 'Escape') setOpen(false); });
        byId('auto-detect').addEventListener('change', () => action('set_auto', {auto_detect: byId('auto-detect').checked}));
        byId('task-save').addEventListener('click', () => {
            if (byId('task-save').disabled) return;
            const fields = {};
            touchedKeys.forEach(key => {
                if (fieldInputs[key]) fields[key] = String(draft[key] || '').trim().slice(0, 200);
            });
            action('edit', {fields});
        });
        byId('task-reload').addEventListener('click', () => { resetDraft(); render(); notice('已载入最新需求。'); });
        [['task-confirm', 'confirm'], ['task-cancel', 'cancel'], ['task-complete', 'complete'], ['task-new', 'new']]
            .forEach(([id, name]) => byId(id).addEventListener('click', () => { if (!byId(id).disabled) action(name); }));
        byId('example-dining').addEventListener('click', () => options.prefill('今晚 7 点在北京朝阳区，3 个人吃饭，人均 100 元，不吃辣'));
        byId('example-travel').addEventListener('click', () => options.prefill('明天上午从北京去上海，2 个人坐高铁，每人预算 600 元'));
        setRoom(room);
        return {
            accept, setRoom, setConnected: refreshControls,
            disconnect() { finishPending(); refreshControls(); },
            open() { setOpen(true); }
        };
    };
}());
