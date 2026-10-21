"""Exercise the real model and SocketIO reply path using a fresh isolated data directory."""
import argparse
import json
import math
import os
import time
from pathlib import Path

# Configure this isolated process before app import. Do not read/write the
# production session secret or decrypt the stored cloud credential.
os.environ["CHAPTER2_DISABLE_CLOUD_API"] = "1"
os.environ["CHAPTER2_SECRET_KEY"] = "isolated-local-ai-smoke-session-signing-key"
for key in ("QWEN_API_KEY", "DASHSCOPE_API_KEY", "COZE_API_TOKEN"):
    os.environ.pop(key, None)

import app as chapter2
import bot


def _bot_messages(events):
    return [
        event["args"][0] for event in events
        if event["name"] == "new_msg" and event["args"][0].get("bot")
    ]


def _ai_statuses(events):
    return [
        event["args"][0] for event in events
        if event["name"] == "ai_status" and event["args"][0].get("room") == "Lobby"
    ]


def _assistant_states(events):
    return [
        event["args"][0] for event in events
        if event["name"] == "assistant_state"
        and event["args"][0].get("room") == "Lobby"
    ]


def _socket_errors(events):
    return [event["name"] for event in events if event["name"] in {"err", "auth_error"}]


def _reset_task(client, token):
    """Obtain a fresh socket revision and use the public new-task action."""
    client.get_received()
    joined = client.emit("join", {"room": "Lobby", "token": token}, callback=True)
    states = _assistant_states(client.get_received())
    if not joined.get("ok") or not states:
        raise RuntimeError("No assistant state on join")
    result = client.emit("assistant_action", {
        "room": "Lobby", "token": token, "action": "new", "version": states[-1]["version"],
    }, callback=True)
    if not result.get("ok"):
        raise RuntimeError("Could not start isolated task")
    client.get_received()


def _card_socket_case(client, token, name, dialogue, intent, expected_fields, timeout_seconds):
    _reset_task(client, token)
    started = time.monotonic()
    ack = client.emit("send_msg", {
        "room": "Lobby", "token": token, "m": dialogue, "t": "text",
    }, callback=True)

    def finished(events):
        return (
            any(snapshot.get("task") and snapshot["task"].get("status") in {"ready", "needs_info"}
                for snapshot in _assistant_states(events))
            or any(status.get("status") in {"idle", "cancelled", "error"} for status in _ai_statuses(events))
            or bool(_socket_errors(events))
        )

    events, timed_out = _receive_until(client, finished, timeout_seconds)
    snapshots = _assistant_states(events)
    snapshot = snapshots[-1] if snapshots else None
    task = (snapshot or {}).get("task") or {}
    fields = task.get("fields", {})
    checks = {key: fragment in fields.get(key, "") for key, fragment in expected_fields.items()}
    replies = _bot_messages(events)
    return {
        "name": name, "dialogue": dialogue, "expected_intent": intent,
        "elapsed_seconds": time.monotonic() - started, "timed_out": timed_out,
        "ack_ok": ack.get("ok") is True, "assistant_state": snapshot,
        "field_checks": checks, "statuses": _ai_statuses(events),
        "bot_reply_count_before_confirmation": len(replies),
        "socket_errors": _socket_errors(events),
        "passed": bool(
            ack.get("ok") is True and not timed_out and task.get("intent") == intent
            and task.get("status") in {"ready", "needs_info"}
            and all(checks.values()) and not replies and not _socket_errors(events)
            and any(item.get("source") == "本地 Qwen3 + LoRA"
                    for item in (snapshot or {}).get("trace", []))
        ),
    }


def _confirm_dining(client, token, case, timeout_seconds):
    """Exercise editable requirements and offline comparable proposals."""
    snapshot = case.get("assistant_state") or {}
    task = snapshot.get("task") or {}
    if task.get("intent") != "吃饭":
        return {"passed": False, "reason": "No dining card to edit"}
    original_fields = dict(task.get("fields", {}))
    edits = {"people": "两人"}
    # Keep this confirmation check meaningful even when a field-extraction
    # assertion failed; the extraction case above remains a separate failure.
    for key, fallback in {"location": "北京三里屯", "time": "今晚七点"}.items():
        if not original_fields.get(key):
            edits[key] = fallback
    ack = client.emit("assistant_action", {
        "room": "Lobby", "token": token, "action": "edit",
        "version": snapshot["version"], "task_id": task["id"], "fields": edits,
    }, callback=True)
    edit_events = client.get_received()
    edited_states = _assistant_states(edit_events)
    edited = edited_states[-1] if edited_states else {}
    edited_task = edited.get("task") or {}
    preserved = all(
        edited_task.get("fields", {}).get(key) == value
        for key, value in original_fields.items() if key not in edits
    )
    if not ack.get("ok") or not edited_task or edited_task.get("status") != "ready":
        return {"passed": False, "edit_ack": ack, "edited_state": edited}
    confirmation = client.emit("assistant_action", {
        "room": "Lobby", "token": token, "action": "confirm",
        "version": edited["version"], "task_id": edited_task["id"],
    }, callback=True)

    def finished(events):
        snapshots = _assistant_states(events)
        return (
            bool(_bot_messages(events))
            and any((value.get("task") or {}).get("status") == "done" for value in snapshots)
        ) or any(value.get("status") == "error" for value in _ai_statuses(events)) or bool(_socket_errors(events))

    events, timed_out = _receive_until(client, finished, timeout_seconds)
    states = _assistant_states(events)
    final = states[-1] if states else {}
    proposals = final.get("proposals", [])
    replies = _bot_messages(events)
    comparison_keys = {"id", "title", "description", "reason", "budget_fit", "tradeoff", "verify"}
    comparable = bool(2 <= len(proposals) <= 3 and all(
        isinstance(proposal, dict) and comparison_keys.issubset(proposal)
        and all(isinstance(proposal[key], str) and proposal[key].strip() for key in comparison_keys)
        and "非实时" in proposal["verify"]
        for proposal in proposals
    ))
    offline_source = any(item.get("source") == "本地通用规划" for item in final.get("trace", []))
    persisted = chapter2.db_op("assistant_state").get("Lobby") == final
    return {
        "name": "dining_edit_and_confirm",
        "edit_ack_ok": ack.get("ok") is True,
        "unmodified_fields_preserved": preserved, "confirmation_ack_ok": confirmation.get("ok") is True,
        "timed_out": timed_out, "assistant_state": final, "proposal_count": len(proposals),
        "comparable_proposals": comparable, "offline_source_confirmed": offline_source,
        "state_persisted": persisted, "bot_reply_count": len(replies),
        "bot_replies": [reply["m"] for reply in replies],
        "socket_errors": _socket_errors(events),
        "passed": bool(
            confirmation.get("ok") is True and not timed_out and preserved
            and (final.get("task") or {}).get("status") == "done"
            and comparable and offline_source and persisted
            and len(replies) == 1 and not _socket_errors(events)
        ),
    }


def _receive_until(client, finished, timeout_seconds):
    """Bound SocketIO waiting and retain the evidence when a case fails."""
    events = []
    deadline = time.monotonic() + timeout_seconds
    while time.monotonic() < deadline:
        events.extend(client.get_received())
        if finished(events):
            return events, False
        time.sleep(.05)
    events.extend(client.get_received())
    return events, not finished(events)


def _silent_socket_case(client, token, name, dialogue, expected_status, timeout_seconds):
    """Require an explicit terminal status and no bot reply on this socket."""
    _reset_task(client, token)
    started = time.monotonic()
    client.emit("send_msg", {
        "u": "local_ai_smoke", "room": "Lobby", "token": token,
        "m": dialogue, "t": "text",
    })
    terminal = {"done", "cancelled", "idle", "error", "ready", "needs_info"}

    def finished(events):
        return (
            bool(_bot_messages(events))
            or any(status.get("status") in terminal for status in _ai_statuses(events))
            or any(event["name"] in {"err", "auth_error"} for event in events)
        )

    events, timed_out = _receive_until(client, finished, timeout_seconds)
    # A short bounded observation also catches a wrongly emitted bot message
    # immediately after the status. No following case is queued until this ends.
    observation_seconds = .3
    if not timed_out:
        trailing, _ = _receive_until(client, lambda _: False, observation_seconds)
        events.extend(trailing)
    statuses = _ai_statuses(events)
    replies = _bot_messages(events)
    terminal_statuses = [status["status"] for status in statuses if status.get("status") in terminal]
    socket_errors = _socket_errors(events)
    passed = (
        not timed_out
        and terminal_statuses and all(status == expected_status for status in terminal_statuses)
        and not replies
        and not socket_errors
    )
    return {
        "name": name,
        "dialogue": dialogue,
        "expected_status": expected_status,
        "same_login_token_used": True,
        "elapsed_seconds": time.monotonic() - started,
        "timeout_seconds": timeout_seconds,
        "timed_out": timed_out,
        "post_status_observation_seconds": observation_seconds if not timed_out else 0,
        "statuses": statuses,
        "terminal_statuses": terminal_statuses,
        "bot_reply_count": len(replies),
        "bot_replies": [reply["m"] for reply in replies],
        "socket_errors": socket_errors,
        "passed": passed,
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-dir", required=True)
    parser.add_argument("--timeout-seconds", type=float, default=30)
    args = parser.parse_args()
    if not math.isfinite(args.timeout_seconds) or args.timeout_seconds <= 0:
        parser.error("--timeout-seconds must be a positive finite number")
    output = Path(args.output_dir).resolve()
    output.mkdir(parents=True, exist_ok=False)
    chapter2.DATA_USER_DIR = str(output / "test_user_data")
    # Only this in-process test uses the short delay; production retains 10 seconds.
    bot.WAIT_SECONDS = .05
    client = chapter2.socketio.test_client(chapter2.app)
    result = {
        "production_data_modified": False,
        "external_api_calls_allowed": False,
        "socket_wait_timeout_seconds": args.timeout_seconds,
        "passed": False,
        "socket_cases": [],
        "card_cases": [],
    }
    try:
        client.emit("register_action", {"u": "local_ai_smoke", "p": "test-only-unused-outside-this-dir"})
        result["registration_ok"] = any(e["name"] == "ok" for e in client.get_received())
        client.emit("login_action", {"u": "local_ai_smoke", "p": "test-only-unused-outside-this-dir"})
        login_events = [e for e in client.get_received() if e["name"] == "login_ok"]
        result["login_ok"] = bool(login_events)
        if not login_events:
            raise RuntimeError("Isolated smoke-test login failed")
        token = login_events[0]["args"][0]["token"]
        client.emit("join", {"u": "local_ai_smoke", "room": "Lobby", "token": token})
        client.get_received()
        text = "小王：今晚七点在北京三里屯吃川菜吧，人均一百元，不吃花生。\n小李：好，就这么定了。"
        dining = _card_socket_case(
            client, token, "dining_card", text, "吃饭",
            {"location": "三里屯", "time": "七点", "budget": "一百", "cuisine": "川菜", "diet": "花生"},
            args.timeout_seconds,
        )
        result["card_cases"].append(dining)
        result["dining_confirmation"] = _confirm_dining(client, token, dining, args.timeout_seconds)
        travel = _card_socket_case(
            client, token, "travel_card",
            "小王：明天上午从北京坐高铁去上海，预算六百元。\n小李：可以，我们一起出发。",
            "出行", {"origin": "北京", "destination": "上海", "time": "明天上午", "budget": "六百", "transport": "高铁"},
            args.timeout_seconds,
        )
        result["card_cases"].append(travel)
        # All cases use the same authenticated SocketIO client and public
        # new-task action; production accounts/history remain untouched.
        for name, dialogue, expected_status in (
            ("cancel_socket", "小王：明天去上海旅游吧。\n小李：临时要加班，取消这次出行，不去了。\n小王：好，那就取消。", "cancelled"),
            ("neutral_socket", "小王：今天的作业已经提交了。\n小李：好的，老师明天讲评。", "idle"),
        ):
            result["socket_cases"].append(_silent_socket_case(
                client, token, name, dialogue, expected_status, args.timeout_seconds,
            ))
        result["passed"] = all([
            result["registration_ok"], result["login_ok"], result["dining_confirmation"]["passed"],
            all(case["passed"] for case in result["card_cases"]),
            all(case["passed"] for case in result["socket_cases"]),
        ])
    except Exception as exc:
        # Keep a usable failure artifact even if a model call or socket handler
        # raises; never dump provider bodies or credentials to the result.
        result["failure_exception_type"] = type(exc).__name__
        result["passed"] = False
    finally:
        bot.shutdown()
        if client.is_connected():
            client.disconnect()
        (output / "result.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(result, ensure_ascii=False, indent=2), flush=True)
    if not result.get("passed"):
        raise SystemExit("AI_WEB_SMOKE_FAILED")
    print("AI_WEB_SMOKE_PASSED")


if __name__ == "__main__":
    main()
