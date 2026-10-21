import os
import shutil
import time

import app as chapter2
import bot


def event_names(client):
    return [event["name"] for event in client.get_received()]


def main():
    os.environ["CHAPTER2_DISABLE_CLOUD_API"] = "1"
    smoke_data = os.path.join("model_output", "web_smoke_data")
    os.makedirs(smoke_data, exist_ok=True)
    chapter2.DATA_USER_DIR = os.path.abspath(smoke_data)
    for name in ("users.json", "chat_history.json"):
        path = os.path.join(smoke_data, name)
        if os.path.exists(path):
            os.remove(path)

    client = chapter2.socketio.test_client(chapter2.app)
    client.emit("register_action", {"u": "chapter2_smoke", "p": "local-test-only"})
    register_events = event_names(client)
    client.emit("login_action", {"u": "chapter2_smoke", "p": "local-test-only"})
    login_received = client.get_received()
    login_events = [event["name"] for event in login_received]
    token = next(event["args"][0]["token"] for event in login_received if event["name"] == "login_ok")
    client.emit("join", {"room": "Lobby", "u": "chapter2_smoke", "token": token})
    join_events = event_names(client)
    client.emit("send_msg", {
        "room": "Lobby",
        "u": "chapter2_smoke",
        "token": token,
        "m": "今天的课程作业交了吗？老师说明天讲评。",
        "t": "text",
    })
    message_events = event_names(client)
    history = chapter2.db_op("chat_history").get("Lobby", [])

    bot.shutdown()
    client.disconnect()
    result = {
        "register_ok": "ok" in register_events,
        "login_ok": "login_ok" in login_events,
        "join_ok": "history" in join_events and "update_users" in join_events,
        "message_broadcast_ok": "new_msg" in message_events,
        "history_count": len(history),
    }
    print(result)
    if not all([result["register_ok"], result["login_ok"], result["join_ok"], result["message_broadcast_ok"]]):
        raise SystemExit("WEB_SOCKETIO_SMOKE_FAILED")
    print("WEB_SOCKETIO_SMOKE_COMPLETE")


if __name__ == "__main__":
    main()
