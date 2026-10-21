"""Opt-in smoke test using only fixed synthetic chat, never a screen or user history."""
import argparse
import contextlib
import io
import json
from pathlib import Path

from assistant_bridge import AssistantBridge


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--web-root", required=True)
    parser.add_argument("--lora", action="store_true")
    parser.add_argument("--live-cloud", action="store_true", help="Make exactly one billable Qwen request using synthetic messages.")
    parser.add_argument("--output")
    args = parser.parse_args()
    subject = AssistantBridge(args.web_root)
    messages = [{"speaker": "other", "text": "明天晚上一起吃火锅吗？在北京朝阳找一家。"},
                {"speaker": "self", "text": "可以，就我们两个人，人均100元，我不能吃辣。"}]
    results = {}
    for op in (["analyze", "reply"] if args.live_cloud else ["analyze"]):
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            result = subject.dispatch({"op": op, "version": 11, "messages": messages,
                                       "useLora": args.lora, "cloudConsent": op == "reply"})
        results[op] = result
    checks = {"local_intent": results["analyze"]["intent"] == "吃饭",
              "local_fields": "100" in results["analyze"]["summary"]}
    if args.lora:
        checks["real_lora"] = results["analyze"]["source"] == "本地 LoRA + 本地字段提取"
    if args.live_cloud:
        checks["real_cloud"] = results["reply"]["source"] == "千问 qwen3.8-flash" and 1 <= len(results["reply"]["replies"]) <= 2
    evidence = {"synthetic_only": True, "cloud_requests": int(args.live_cloud), "checks": checks, "results": results}
    if args.output:
        Path(args.output).write_text(json.dumps(evidence, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(evidence, ensure_ascii=False, indent=2))
    return 0 if all(checks.values()) else 1


if __name__ == "__main__":
    raise SystemExit(main())
