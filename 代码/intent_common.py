"""Shared training and inference contract for the local intent model."""

import json
import re


INTENT_LABELS = frozenset(("吃饭", "出行", "无意图"))
ACTION_LABELS = frozenset(("继续行动", "取消行动", "无"))
PREDICTION_FIELDS = ("intent_label", "requirement_label", "action_status")
INSTRUCTION = (
    '请阅读分析以下群聊多轮对话历史，准确识别出聊天成员的整体核心意图标签'
    '（从"吃饭"、"出行"、"无意图"中选择）、具体的要求标签（如无具体要求则填"无"），'
    '以及最终的行动状态。无意图时，行动状态为"无"；吃饭或出行时，行动状态从'
    '"继续行动"、"取消行动"中选择。请只输出一个JSON对象，恰好包含'
    '"intent_label"、"requirement_label"、"action_status"三个字段，字段值均为字符串。'
)
_PROMPT_START = (
    "<|im_start|>system\n"
    "你是一个专业的群聊意图识别助手，请根据群聊对话历史识别核心意图。<|im_end|>\n"
    "<|im_start|>user\n"
    f"{INSTRUCTION}\n\n"
    "对话内容：\n"
)
_PROMPT_END = "<|im_end|>\n<|im_start|>assistant\n"


def build_prompt(dialogue: str) -> str:
    """Build the identical ChatML prefix used by training and inference."""
    if not isinstance(dialogue, str):
        raise TypeError("对话必须是字符串")
    return _PROMPT_START + dialogue + _PROMPT_END


def build_correction_prompt(dialogue: str) -> str:
    """Ask once for a fresh, valid result without repairing labels in code."""
    if not isinstance(dialogue, str):
        raise TypeError("对话必须是字符串")
    correction = (
        "结构纠正：上次回答未通过JSON格式或字段一致性检查。"
        "请重新阅读下方原始对话并独立判断，不要只修改标签来凑合格式。"
        "必须恰好输出三个字符串字段，不得附加解释、思考过程或Markdown。"
        '有效组合只有：无意图搭配无；吃饭或出行搭配继续行动或取消行动。'
        "根据对话最后确认的计划判断是否取消，不要把取消改为继续行动。"
    )
    return (
        "<|im_start|>system\n"
        "你是一个专业的群聊意图识别助手，请根据群聊对话历史识别核心意图。<|im_end|>\n"
        "<|im_start|>user\n"
        f"{INSTRUCTION}\n\n{correction}\n\n"
        f"对话内容：\n{dialogue}"
        f"{_PROMPT_END}"
    )


def validate_prediction(value: dict) -> dict[str, str]:
    """Validate all fields and label combinations without guessing a fallback."""
    if not isinstance(value, dict) or set(value) != set(PREDICTION_FIELDS):
        raise ValueError(f"结果必须恰好包含三个字段: {', '.join(PREDICTION_FIELDS)}")
    if any(not isinstance(value[field], str) for field in PREDICTION_FIELDS):
        raise ValueError("JSON 的三个字段值必须均为字符串")
    result = {field: value[field].strip() for field in PREDICTION_FIELDS}
    if result["intent_label"] not in INTENT_LABELS:
        raise ValueError(f"未知意图标签: {result['intent_label']!r}")
    if not result["requirement_label"]:
        raise ValueError('要求标签不能为空，无具体要求时应使用"无"')
    action = result["action_status"]
    if action not in ACTION_LABELS:
        raise ValueError(f"未知行动状态: {action!r}")
    if result["intent_label"] == "无意图":
        if action != "无":
            raise ValueError('无意图的行动状态必须为"无"')
    elif action == "无":
        raise ValueError('吃饭或出行的行动状态必须为"继续行动"或"取消行动"')
    return result


def _unique_json_fields(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"JSON 含重复字段: {key!r}")
        result[key] = value
    return result


def parse_prediction(text: str) -> dict[str, str]:
    """Parse one JSON response, permitting only formatting wrappers."""
    if not isinstance(text, str):
        raise TypeError("模型输出必须是字符串")
    candidate = text.strip()
    # A complete reasoning block or code fence is a formatting wrapper. Never
    # search for an arbitrary brace: braces can occur inside requirement text.
    if candidate.startswith("<think>"):
        end = candidate.find("</think>")
        if end == -1:
            raise ValueError("模型未完成思考段，未返回完整JSON")
        candidate = candidate[end + len("</think>"):].strip()
    fence = re.fullmatch(r"```(?:json)?\s*\n?(.*?)\s*```", candidate, re.DOTALL | re.IGNORECASE)
    if fence:
        candidate = fence.group(1).strip()
    try:
        parsed = json.loads(candidate, object_pairs_hook=_unique_json_fields)
    except json.JSONDecodeError:
        # Preserve the existing narrow recovery for literal control characters
        # in a generated JSON string; malformed labels are still rejected.
        repaired = re.sub(r"[\x00-\x1f]", " ", candidate)
        parsed = json.loads(repaired, object_pairs_hook=_unique_json_fields)
    return validate_prediction(parsed)
