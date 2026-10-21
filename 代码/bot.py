import time
import threading
from collections import deque
from model_loader import ask_f
from tools.pre_audio import ask_audio
from tools.pre_image import ask_image
from tools.pre_video import ask_video
from tools.dining import dining_ret
from tools.travel import train_ret
import random
import os
import re
import copy
import uuid
from dataclasses import dataclass, field
from pathlib import Path
from intent_common import validate_prediction
from tools.config import Qwen_API_KEY
from tools.qwen_client import QwenClientError, qwen_chat_completion
from assistant_features import (
    FIELD_KEYS, FIELD_LABELS, extract_fields, missing_fields, summarize_fields,
    generate_proposals, answer_question,
)

# ==================== 配置参数 ====================
WAIT_SECONDS = 10  # 等待新消息的时间（秒）
MAX_HISTORY = 20  # 每个房间最多存储的聊天记录条数
CLEAR_TIMEOUT = 300  # 5分钟无消息则清空列表（秒）
# ================================================

# Kept public for the standalone smoke-test shutdown hooks.
ROOM_TIMERS = {}
REPLY_CALLBACK = None
STATUS_CALLBACK = None
ASSISTANT_CALLBACK = None
_ROOMS = {}
_REGISTRY_LOCK = threading.RLock()


@dataclass
class _Room:
    lock: object = field(default_factory=threading.RLock)
    work_lock: object = field(default_factory=threading.Lock)
    token: object = field(default_factory=object)
    history: deque = field(default_factory=lambda: deque(maxlen=MAX_HISTORY))
    predict_timer: object = None
    clear_timer: object = None
    initialized: bool = False
    version: int = 0
    auto_detect: bool = True
    task: object = None
    proposals: list = field(default_factory=list)
    trace: list = field(default_factory=list)
    last_error: str = ""
    resume_status: str = "ready"


def _room_state(room):
    with _REGISTRY_LOCK:
        return _ROOMS.setdefault(room, _Room())


def set_status_callback(callback):
    global STATUS_CALLBACK
    STATUS_CALLBACK = callback


def set_assistant_callback(callback):
    global ASSISTANT_CALLBACK
    ASSISTANT_CALLBACK = callback


def _snapshot(room, state):
    return copy.deepcopy({
        "room": room, "version": state.version, "auto_detect": state.auto_detect,
        "task": state.task, "proposals": state.proposals,
        "trace": state.trace[-24:], "last_error": state.last_error,
    })


def get_assistant_state(room):
    state = _room_state(room)
    with state.lock:
        return _snapshot(room, state)


def _publish(room, state):
    state.initialized = True
    state.version += 1
    if ASSISTANT_CALLBACK:
        try:
            ASSISTANT_CALLBACK(room, _snapshot(room, state))
        except Exception as exc:
            state.last_error = "需求暂存失败，请保留当前信息并稍后重试。"
            print(f"【需求状态保存失败】{type(exc).__name__}")
            _status(room, "error", state.last_error)


def _trace(state, stage, source, summary, started=None):
    state.trace.append({
        "stage": stage, "source": source,
        "duration_ms": round((time.monotonic() - started) * 1000) if started else 0,
        "summary": str(summary)[:350], "at": time.strftime("%H:%M:%S"),
    })
    state.trace[:] = state.trace[-24:]


def _refresh_task(task):
    task["missing"] = missing_fields(task["intent"], task["fields"])
    task["summary"] = summarize_fields(task["intent"], task["fields"])
    labels = [FIELD_LABELS.get(key, key) for key in task["missing"]]
    task["clarification"] = (
        "请先确认大家有分歧的选项。" if task["conflicts"] else
        "请补充：" + "、".join(labels) + "。" if labels else
        "请核对需求，确认后生成可比较的方案。"
    )
    task["status"] = "needs_info" if task["missing"] or task["conflicts"] else "ready"


def restore_assistant_state(room, snapshot):
    state = _room_state(room)
    with state.lock:
        if state.initialized:
            return
        state.initialized = True
        if not isinstance(snapshot, dict):
            return
        revision = snapshot.get("version", 0)
        state.version = revision if type(revision) is int and revision >= 0 else 0
        state.auto_detect = snapshot.get("auto_detect", True) is not False
        task = snapshot.get("task")
        if isinstance(task, dict) and task.get("intent") in {"吃饭", "出行"}:
            fields = task.get("fields", {})
            if not isinstance(fields, dict):
                fields = {}
            state.task = {
                "id": str(task.get("id") or uuid.uuid4()), "intent": task["intent"],
                "fields": {key: str(fields.get(key, ""))[:200] for key in FIELD_KEYS},
                "conflicts": copy.deepcopy(task.get("conflicts", []))[:10],
                "owner": str(task.get("owner", "")),
                "field_owners": copy.deepcopy(task.get("field_owners", {})),
            }
            _refresh_task(state.task)
            if task.get("status") in {"cancelled", "completed", "done"}:
                state.task["status"] = task["status"]
                state.task["clarification"] = {
                    "cancelled": "本次安排已取消，可以开始新的需求。",
                    "completed": "本次安排已完成，可以开始新的需求。",
                    "done": "方案已生成，可比较取舍；修改需求后可重新生成。",
                }[task["status"]]
        state.proposals = copy.deepcopy(snapshot.get("proposals", []))[:3]
        state.trace = copy.deepcopy(snapshot.get("trace", []))[-22:]
        _trace(state, "恢复任务", "本地存储", "已恢复需求卡片；未自动恢复中断的请求。")
        _publish(room, state)


def _status(room, status, message):
    if STATUS_CALLBACK:
        try:
            STATUS_CALLBACK(room, status, message)
        except Exception as exc:
            print(f"【状态通知失败】{type(exc).__name__}")


def _cancel_timer(room, state, kind):
    timer = getattr(state, kind)
    if timer:
        timer.cancel()
        setattr(state, kind, None)
        key = room + ("_predict" if kind == "predict_timer" else "_clear")
        if ROOM_TIMERS.get(key) is timer:
            ROOM_TIMERS.pop(key, None)


def _clear_if_current(room, state, token):
    with state.lock:
        if state.token is not token:
            return
        state.token = object()
        state.history.clear()
        _cancel_timer(room, state, "predict_timer")
        _cancel_timer(room, state, "clear_timer")


def clear_room_history(room):
    state = _room_state(room)
    with state.lock:
        _clear_if_current(room, state, state.token)
        state.task = None
        state.proposals = []
        state.trace = []
        state.last_error = ""
        _publish(room, state)


def cancel_pending(room):
    """Explicit user stop invalidates pending and in-flight results immediately."""
    state = _room_state(room)
    with state.lock:
        state.token = object()
        state.history.clear()
        _cancel_timer(room, state, "predict_timer")
        _cancel_timer(room, state, "clear_timer")
        if state.task and state.task["status"] not in {"completed", "cancelled"}:
            state.task["status"] = "cancelled"
            state.task["clarification"] = "本次安排已取消，可以开始新的需求。"
        state.proposals = []
        state.last_error = ""
        _trace(state, "停止处理", "用户操作", "待处理任务已取消，已开始的请求结果也不会发布。")
        _publish(room, state)
        _status(room, "cancelled", "已停止本次回复。")


def shutdown():
    """Cancel background timers, primarily for isolated tests."""
    with _REGISTRY_LOCK:
        rooms = list(_ROOMS.items())
        _ROOMS.clear()
    for room, state in rooms:
        with state.lock:
            state.token = object()
            _cancel_timer(room, state, "predict_timer")
            _cancel_timer(room, state, "clear_timer")


def qwen_simulated_recommendation(intent_label, requirement_label):
    """Generate a teaching-only recommendation without claiming live data."""
    config = Qwen_API_KEY()
    if intent_label == "出行":
        system_prompt = """你是出行方案教学演示助手。根据用户需求生成一份简短方案。
硬性要求：
1. 第一行必须是“【模拟出行建议（非实时票务）】”。
2. 明确说明车次、票价和余票需要以12306实时查询为准。
3. 只整理出发地、目的地、日期/时段、预算，并给出2至3条操作建议。
4. 不得给出未经查询的具体车次、余票、票价或估计价格区间。
5. 使用简洁普通文本，不要使用Markdown星号或标题标记。"""
    else:
        system_prompt = """你是餐厅推荐教学演示助手。根据用户需求生成一份简短方案。
硬性要求：
1. 第一行必须是“【模拟餐厅建议（非实时地图数据）】”。
2. 明确说明营业状态、距离和人均价格需要在地图平台核验。
3. 归纳地点、口味、忌口和预算，并给出2至3种餐厅类型建议。
4. 不得虚构具体门店地址、实时排队或营业状态。
5. 使用简洁普通文本，不要使用Markdown星号或标题标记。"""
    return qwen_chat_completion(
        [
            {"role": "system", "content": system_prompt},
            {"role": "user", "content": requirement_label},
        ],
        config=config,
    )

def set_reply_callback(callback):
    global REPLY_CALLBACK
    REPLY_CALLBACK = callback


def generate_reply(intent_label, requirement_label):
    if intent_label == "无意图":
        return ""

    qwen_ready = bool(Qwen_API_KEY()["api_key"])
    if intent_label == "吃饭":
        coze_ready = all((os.getenv(name) or "").strip() for name in (
            "COZE_DINING_WORKFLOW_ID", "COZE_SPACE_ID", "COZE_API_TOKEN"
        ))
        service_name = "餐厅推荐"
    elif intent_label == "出行":
        coze_ready = all((os.getenv(name) or "").strip() for name in (
            "COZE_TRAVEL_WORKFLOW_ID", "COZE_SPACE_ID", "COZE_API_TOKEN"
        ))
        service_name = "高铁推荐"
    else:
        coze_ready = False
        service_name = "推荐"

    if not qwen_ready:
        return (
            f"【语义理解】意图：{intent_label}\n"
            f"需求：{requirement_label}\n"
            f"行动状态：继续行动\n\n"
            f"已识别需求，但{service_name}暂不可用：尚未配置千问 API。"
        )

    if not coze_ready:
        try:
            recommendation = qwen_simulated_recommendation(intent_label, requirement_label)
            return (
                f"【语义理解】意图：{intent_label}\n"
                f"需求：{requirement_label}\n"
                f"行动状态：继续行动\n\n"
                f"{recommendation}"
            )
        except Exception as exc:
            error_code = exc.code if isinstance(exc, QwenClientError) else "QWEN_UNEXPECTED_ERROR"
            print(f"【千问模拟推荐失败】{error_code}")
            return (
                f"【语义理解】意图：{intent_label}\n"
                f"需求：{requirement_label}\n"
                f"行动状态：继续行动\n\n"
                f"语义识别已完成，但千问模拟推荐调用失败，请检查 QWEN_API_KEY 和账户状态。"
            )

    try:
        if intent_label == "吃饭":
            result = dining_ret(requirement_label)
            print(f"【dining_ret】返回结果: {result}")
            if isinstance(result, str):
                return result
            if isinstance(result, list) and result:
                return result[0]
            return "已为您找到合适的餐厅，请查看详情。"

        if intent_label == "出行":
            result = train_ret(requirement_label)
            print(f"【train_ret】返回结果: {result}")
            if isinstance(result, str):
                return result
            if isinstance(result, list) and result:
                return result[0]
            return "已为您找到合适的出行方案，请查看详情。"
    except Exception as exc:
        print(f"【外部推荐失败】{type(exc).__name__}")
        return (
            f"【语义理解】意图：{intent_label}\n"
            f"需求：{requirement_label}\n"
            f"行动状态：继续行动\n\n"
            f"语义识别已完成，但{service_name}调用失败，请检查 API 与工作流配置。"
        )

    templates = [
        f"好的，收到您的需求：{requirement_label}，我来处理。",
        f"明白了，{requirement_label}，我会尽快安排。",
    ]
    return random.choice(templates)

def _format_record(nick, text):
    lines = [line.strip() for line in text.splitlines() if line.strip()]
    # A pasted multi-speaker dialogue already has speaker labels. Do not add a
    # second outer speaker to its first line and change the model input format.
    if len(lines) > 1 and all(re.match(r"^[^:：\n]{1,30}[:：]", line) for line in lines):
        return "\n".join(lines)
    return f"{nick}:{text}"


def _resolve_record(record):
    if record["type"] == "text":
        return record["text"]
    if record.get("resolved") is not None:
        return record["resolved"]
    # Defense in depth: media parsers only receive files in this app's uploads.
    project = Path(__file__).resolve().parent
    uploads = (project / "static" / "uploads").resolve()
    relative = record["url"]
    if not relative.startswith("/static/uploads/"):
        raise ValueError("不支持的附件地址")
    path = (project / relative.lstrip("/")).resolve()
    if not path.is_relative_to(uploads) or not path.is_file():
        raise ValueError("附件文件不存在")
    parsers = {"image": ask_image, "audio": ask_audio, "video": ask_video}
    result = parsers[record["type"]](str(path))
    if not isinstance(result, str) or not result.strip():
        raise ValueError("附件识别未返回文本")
    record["resolved"] = result.strip()
    return record["resolved"]


def _active_task(state):
    return state.task and state.task["status"] not in {"cancelled", "completed"}


def _looks_like_update(text):
    return bool(re.search(
        r"改[成为到]|换[成到]|预算|人均|不吃|别吃|忌口|过敏|"
        r"从.+出发|出发地|目的地|交通|口味|地点|时间|人数|"
        r"^[\s\S]{0,10}(明天|后天|今晚|今天|周[一二三四五六日天])|"
        r"[一二两三四五六七八九十\d]+\s*(人|位|元|块)", text
    ))


def _is_cancel(text):
    if re.search(r"不要取消|别取消|不取消|不必取消|不用取消|不想取消|不能取消|不是取消|[?？]|怎么|哪里|按钮", text):
        return False
    return bool(re.search(r"取消(这次|本次|安排|计划|出行|聚餐|吧|了)?|不去了|算了吧", text))


def _topic_switch(text, intent):
    if intent == "吃饭":
        return bool(re.search(r"高铁|火车|飞机|出行|旅游|出差|从.+(?:到|去).+", text))
    return bool(re.search(r"聚餐|吃饭|就餐|餐厅|火锅|吃.+菜", text))


def _invalidate(room, state, clear_history=True):
    state.token = object()
    if clear_history:
        state.history.clear()
    _cancel_timer(room, state, "predict_timer")
    _cancel_timer(room, state, "clear_timer")


def _run_prediction(room, state, token, snapshot, user_nick):
    with state.work_lock:
        started = time.monotonic()
        try:
            with state.lock:
                if state.token is not token:
                    return
                previous = copy.deepcopy(state.task) if _active_task(state) else None
                _status(room, "analyzing", "正在识别意图并整理需求卡片…")
            records = []
            for record in snapshot:
                with state.lock:
                    if state.token is not token:
                        return
                records.append(_format_record(record["nick"], _resolve_record(record)))
            dialogue = "\n".join(records)
            latest_text = _resolve_record(snapshot[-1])
            if previous and _is_cancel(latest_text):
                with state.lock:
                    if state.token is token:
                        cancel_pending(room)
                return
            updating = previous and not _topic_switch(latest_text, previous["intent"]) and (
                _looks_like_update(latest_text) or
                bool(re.search(r"不要取消|别取消|不取消|照常|继续原计划", latest_text))
            )
            if updating:
                intent, requirement, action = previous["intent"], previous["summary"], "继续行动"
                classification_source = "当前任务修改规则"
            else:
                intent, requirement, action = ask_f(dialogue)
                validate_prediction({"intent_label": intent, "requirement_label": requirement,
                                     "action_status": action})
                classification_source = "本地 Qwen3 + LoRA"
            with state.lock:
                if state.token is not token:
                    return
                _trace(state, "意图识别", classification_source,
                       f"意图：{intent}；行动：{action}", started)
                if intent == "无意图":
                    state.history.clear()
                    if state.task and state.task["status"] == "draft":
                        _refresh_task(state.task)
                        if state.resume_status == "done":
                            state.task["status"] = "done"
                    _publish(room, state)
                    _status(room, "idle", "当前是普通聊天。需要回答时可选择“询问 AI”。")
                    return
                if action == "取消行动":
                    cancel_pending(room)
                    _status(room, "cancelled", "已识别取消安排，停止本次推荐。")
                    return
                base = previous if previous and previous["intent"] == intent else None
            extraction_start = time.monotonic()
            extraction = extract_fields(dialogue, intent, previous=base["fields"] if base else None)
            with state.lock:
                if state.token is not token:
                    return
                fields = dict(base["fields"]) if base else {key: "" for key in FIELD_KEYS}
                owners = dict(base.get("field_owners", {})) if base else {}
                conflicts = copy.deepcopy(base.get("conflicts", [])) if base else []
                new_conflicts = extraction.get("conflicts", [])
                sender = snapshot[-1].get("uid") or snapshot[-1]["nick"]
                speaker_ids = {}
                for record in snapshot:
                    speaker_ids.setdefault(record["nick"], set()).add(record.get("uid") or record["nick"])
                touched = extraction.get("touched_fields", list(extraction["fields"]))
                resolved = extraction.get("resolved_fields", [])
                mergeable = extraction.get("mergeable_fields", [])
                for key, value in extraction["fields"].items():
                    if key not in FIELD_KEYS or key not in touched or not isinstance(value, str):
                        continue
                    value = value.strip()[:200]
                    old = fields.get(key, "")
                    speaker = extraction.get("field_speakers", {}).get(key)
                    candidates = speaker_ids.get(speaker, set())
                    author = next(iter(candidates)) if len(candidates) == 1 else (
                        f"speaker:{speaker}" if speaker else sender
                    )
                    if key in resolved:
                        conflicts = [c for c in conflicts if c.get("field") != key]
                        fields[key], owners[key] = value, author
                        continue
                    if value == old:
                        continue
                    prior_owner = owners.get(key)
                    # A different person's preference does not silently replace
                    # the already collected requirement in a group.
                    if old and prior_owner and prior_owner != author and key not in mergeable:
                        conflicts = [c for c in conflicts if c.get("field") != key]
                        conflicts.append({"field": key, "values": [old, value or "不作要求"],
                                          "message": f"{FIELD_LABELS[key]}有不同意见，请在卡片中确认。"})
                    else:
                        fields[key] = value
                        owners[key] = author
                        conflicts = [c for c in conflicts if c.get("field") != key]
                for conflict in new_conflicts:
                    key = conflict.get("field")
                    if key in FIELD_KEYS:
                        conflicts = [c for c in conflicts if c.get("field") != key]
                        conflicts.append(conflict)
                state.task = {
                    "id": base["id"] if base else str(uuid.uuid4()),
                    "intent": intent, "fields": fields, "conflicts": conflicts[:10],
                    "owner": base["owner"] if base else sender, "field_owners": owners,
                }
                _refresh_task(state.task)
                state.proposals = []
                state.last_error = extraction.get("warning", "")
                state.history.clear()
                _trace(state, "需求整理", extraction.get("source", "本地规则"),
                       state.task["summary"], extraction_start)
                _publish(room, state)
                _status(room, "needs_info" if state.task["missing"] or conflicts else "ready",
                        state.task["clarification"])
        except Exception as exc:
            print(f"【AI处理失败】{type(exc).__name__}")
            with state.lock:
                if state.token is token:
                    state.last_error = "这次没能完成识别，请重新描述或直接编辑需求卡片。"
                    if state.task and state.task["status"] == "draft":
                        state.task["status"] = "error"
                    _trace(state, "识别失败", "本地服务", state.last_error, started)
                    _publish(room, state)
                    _status(room, "error", state.last_error)
        finally:
            with state.lock:
                if state.token is token:
                    _cancel_timer(room, state, "predict_timer")


def _run_proposals(room, state, token, task, nick):
    with state.work_lock:
        started = time.monotonic()
        try:
            with state.lock:
                if state.token is not token:
                    return
            result = generate_proposals(task["intent"], task["fields"])
            with state.lock:
                if state.token is not token:
                    return
                proposals = result["proposals"]
                if not isinstance(proposals, list) or not 2 <= len(proposals) <= 3:
                    raise ValueError("Invalid proposal count")
                state.proposals = proposals
                state.task["status"] = "done"
                state.task["clarification"] = "方案已生成，可比较取舍；修改需求后可重新生成。"
                state.last_error = result.get("warning", "")
                _trace(state, "方案生成", result.get("source", "云端千问"),
                       f"生成 {len(proposals)} 个待核实方案；不含实时库存或价格。", started)
                if REPLY_CALLBACK:
                    lines = [f"已根据确认的{task['intent']}需求整理 {len(proposals)} 个方案：",
                             summarize_fields(task["intent"], task["fields"])]
                    lines += [f"{i + 1}. {p['title']}：{p['description']}" for i, p in enumerate(proposals)]
                    lines.append("请在需求面板比较匹配理由、预算和待核实信息。以上为非实时建议。")
                    REPLY_CALLBACK(room, "\n".join(lines), nick)
                _publish(room, state)
                _status(room, "done", "方案已生成，可在需求面板比较或继续修改。")
        except Exception as exc:
            print(f"【方案生成失败】{type(exc).__name__}")
            with state.lock:
                if state.token is token:
                    state.task["status"] = "error"
                    state.last_error = "方案生成失败，需求已保留，可重试。"
                    _trace(state, "方案失败", "推荐服务", state.last_error, started)
                    _publish(room, state)
                    _status(room, "error", state.last_error)
        finally:
            with state.lock:
                if state.token is token:
                    _cancel_timer(room, state, "predict_timer")


def _run_question(room, state, token, record, task):
    with state.work_lock:
        started = time.monotonic()
        try:
            with state.lock:
                if state.token is not token:
                    return
                _status(room, "generating", "正在回答你的问题…")
            question = re.sub(r"^\s*@AI(?:\s|[：:，,])*\s*", "", _resolve_record(record),
                              flags=re.IGNORECASE).strip()
            if not question:
                raise ValueError("Empty question")
            result = answer_question(question, context=task)
            with state.lock:
                if state.token is not token:
                    return
                if REPLY_CALLBACK:
                    REPLY_CALLBACK(room, result["text"], record["nick"])
                state.last_error = result.get("warning", "")
                _trace(state, "主动问答", result.get("source", "云端千问"),
                       "已按主动提问回答，未修改需求卡片。", started)
                _publish(room, state)
                _status(room, "done", "AI 已回复，可继续提问或完善需求。")
        except Exception as exc:
            print(f"【主动问答失败】{type(exc).__name__}")
            with state.lock:
                if state.token is token:
                    state.last_error = "主动问答暂不可用，请检查千问服务后重试。"
                    _trace(state, "问答失败", "云端千问", state.last_error, started)
                    _publish(room, state)
                    _status(room, "error", state.last_error)
        finally:
            with state.lock:
                if state.token is token:
                    _cancel_timer(room, state, "predict_timer")


def _schedule(room, state, function, args, delay):
    timer = threading.Timer(delay, function, args=args)
    timer.daemon = True
    state.predict_timer = timer
    ROOM_TIMERS[room + "_predict"] = timer
    timer.start()


def handle_assistant_action(room, user_id, user_nick, payload):
    state = _room_state(room)
    with state.lock:
        revision = payload.get("version")
        if type(revision) is not int or revision != state.version:
            return {"ok": False, "error": "需求已更新，请查看最新卡片后重试。"}
        action = payload.get("action")
        if action == "set_auto":
            enabled = payload.get("auto_detect")
            if type(enabled) is not bool:
                return {"ok": False, "error": "自动识别设置无效。"}
            if not enabled:
                _invalidate(room, state)
                if _active_task(state) and state.task["status"] in {"generating", "draft"}:
                    _refresh_task(state.task)
            state.auto_detect = enabled
            _trace(state, "识别设置", "用户操作", "自动识别已开启。" if enabled else "仅主动询问 AI；聊天消息照常发送。")
            _publish(room, state)
            _status(room, "idle", "自动识别已开启。" if enabled else "自动识别已关闭，可使用“询问 AI”。")
            return {"ok": True}
        if action == "new":
            _invalidate(room, state)
            state.task, state.proposals, state.trace, state.last_error = None, [], [], ""
            _trace(state, "新建需求", "用户操作", "已结束旧上下文，可描述新的吃饭或出行需求。")
            _publish(room, state)
            _status(room, "idle", "已开始新需求，请在聊天中描述你的安排。")
            return {"ok": True}
        if not state.task or payload.get("task_id") != state.task["id"]:
            return {"ok": False, "error": "当前需求不存在或已更换，请刷新卡片。"}
        if action == "cancel":
            cancel_pending(room)
            return {"ok": True}
        if action == "complete":
            _invalidate(room, state)
            state.task["status"] = "completed"
            state.task["clarification"] = "本次安排已完成，可以开始新的需求。"
            _trace(state, "结束需求", "用户操作", "本次需求已完成，新消息将开始独立需求。")
            _publish(room, state)
            _status(room, "done", "本次需求已完成。")
            return {"ok": True}
        if action == "edit":
            values = payload.get("fields")
            if (not isinstance(values, dict) or not values or
                    any(key not in FIELD_KEYS or not isinstance(value, str) or len(value) > 200
                        for key, value in values.items())):
                return {"ok": False, "error": "需求字段无效，每项最多 200 字。"}
            if state.task["status"] in {"completed", "cancelled"}:
                return {"ok": False, "error": "这次需求已结束，请先新建需求。"}
            _invalidate(room, state)
            for key, value in values.items():
                state.task["fields"][key] = value.strip()
                state.task["field_owners"][key] = user_id
            state.task["conflicts"] = [c for c in state.task["conflicts"] if c["field"] not in values]
            _refresh_task(state.task)
            state.proposals, state.last_error = [], ""
            _trace(state, "人工修正", "用户确认",
                   "已更新：" + "、".join(FIELD_LABELS[key] for key in values))
            _publish(room, state)
            _status(room, state.task["status"], state.task["clarification"])
            return {"ok": True}
        if action == "confirm":
            if state.task["status"] in {"completed", "cancelled", "generating", "draft"}:
                return {"ok": False, "error": "当前需求不可重复生成，请等待或新建需求。"}
            _refresh_task(state.task)
            if state.task["missing"] or state.task["conflicts"]:
                return {"ok": False, "error": state.task["clarification"]}
            _invalidate(room, state)
            state.task["status"] = "generating"
            state.task["clarification"] = "需求已确认，正在生成可比较的方案。"
            state.proposals, state.last_error = [], ""
            _trace(state, "确认需求", "用户确认", state.task["summary"])
            _publish(room, state)
            _status(room, "generating", "正在根据确认的需求生成可比较方案…")
            _schedule(room, state, _run_proposals,
                      (room, state, state.token, copy.deepcopy(state.task), user_nick), .01)
            return {"ok": True}
        return {"ok": False, "error": "不支持该操作。"}


def custom_bot_reply(context):
    room = context.get("room", "Lobby")
    state = _room_state(room)
    record = {
        "nick": context.get("user_nick", ""), "uid": context.get("user_id", ""),
        "type": (context.get("msg_type") or "text").lower(),
        "text": (context.get("msg") or "").strip(),
        "url": context.get("url") or "", "resolved": None,
    }
    explicit = context.get("ask_ai") is True or bool(re.match(r"^\s*@AI(?![A-Za-z0-9_])", record["text"], re.I))
    with state.lock:
        state.initialized = True
        if not explicit and not state.auto_detect:
            return False, ""
        # A new input immediately invalidates all old output, before inference.
        _invalidate(room, state, clear_history=explicit)
        if explicit:
            if _active_task(state) and state.task["status"] in {"generating", "draft"}:
                _refresh_task(state.task)
            state.last_error = ""
            _trace(state, "主动提问", "用户操作", "使用云端千问回答此次问题。")
            _publish(room, state)
            _status(room, "generating", "正在回答你的问题…")
            _schedule(room, state, _run_question,
                      (room, state, state.token, record,
                       copy.deepcopy(state.task) if _active_task(state) else None), .01)
            return False, ""
        if state.task and state.task["status"] in {"completed", "cancelled"}:
            state.history.clear()
            state.task, state.proposals = None, []
        elif _active_task(state) and state.task["status"] == "generating":
            _refresh_task(state.task)
            state.proposals = []
        if _active_task(state):
            if state.task["status"] != "draft":
                state.resume_status = state.task["status"]
            state.task["status"] = "draft"
            state.task["clarification"] = "正在整理最新消息，请等待卡片更新后确认。"
        state.last_error = ""
        state.history.append(record)
        snapshot = tuple(state.history)
        clear_timer = threading.Timer(CLEAR_TIMEOUT, _clear_if_current, args=(room, state, state.token))
        clear_timer.daemon = True
        state.clear_timer = clear_timer
        ROOM_TIMERS[room + "_clear"] = clear_timer
        clear_timer.start()
        _publish(room, state)
        _status(room, "waiting", f"等待补充消息，约{WAIT_SECONDS:g}秒后整理需求卡片…")
        _schedule(room, state, _run_prediction,
                  (room, state, state.token, snapshot, record["nick"]), WAIT_SECONDS)
    return False, ""


def get_bot_decision(context):
    return custom_bot_reply(context)
