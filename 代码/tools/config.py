import json
import os
from pathlib import Path
from urllib.parse import urlsplit, urlunsplit

from tools.local_credentials import load_qwen_key


QWEN_SETTINGS_PATH = Path(__file__).resolve().parents[1] / ".local" / "qwen_config.json"


def _qwen_settings():
    if not QWEN_SETTINGS_PATH.is_file():
        return {}
    try:
        settings = json.loads(QWEN_SETTINGS_PATH.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        raise RuntimeError("千问本地配置无法读取") from None
    if not isinstance(settings, dict):
        raise RuntimeError("千问本地配置格式错误")
    return settings


def _required_env(name):
    value = os.getenv(name, "").strip()
    if not value:
        raise RuntimeError(f"未设置环境变量 {name}")
    return value


def Doubao_API_KEY():
    from volcenginesdkarkruntime import Ark
    return Ark(
        api_key=_required_env("DOUBAO_API_KEY"),
        base_url=os.getenv("DOUBAO_BASE_URL", "https://ark.cn-beijing.volces.com/api/v3"),
    )


def Qwen_API_KEY():
    settings = _qwen_settings()
    base_url = (os.getenv("QWEN_BASE_URL") or settings.get("base_url") or
                "https://maas.qianwenaiapi.com/compatible-mode/v1").strip().rstrip("/")
    parsed_url = urlsplit(base_url)
    if parsed_url.scheme != "https" or not parsed_url.netloc or parsed_url.username or parsed_url.password:
        raise RuntimeError("千问接口地址必须是无凭据的 HTTPS URL")
    if parsed_url.query or parsed_url.fragment:
        raise RuntimeError("千问接口地址不能包含查询参数")
    if not base_url.endswith("/compatible-mode/v1"):
        raise RuntimeError("QWEN_BASE_URL 需使用 OpenAI 兼容接口 /compatible-mode/v1")
    disabled = os.getenv("CHAPTER2_DISABLE_CLOUD_API", "").strip() == "1"
    api_key = "" if disabled else (
        os.getenv("QWEN_API_KEY", "").strip()
        or os.getenv("DASHSCOPE_API_KEY", "").strip()
        or load_qwen_key()
    )
    native_url = urlunsplit((parsed_url.scheme, parsed_url.netloc, "/api/v1", "", ""))
    return {
        "api_key": api_key,
        "base_url": base_url,
        "dashscope_base_url": os.getenv("QWEN_DASHSCOPE_BASE_URL", native_url).strip(),
        "model": (os.getenv("QWEN_MODEL") or settings.get("model") or "qwen3.8-flash").strip(),
        "timeout": 45,
        "max_tokens": 768,
    }


def Coze_API_KEY_TRAVEL():
    return {
        "WORKFLOW_ID": _required_env("COZE_TRAVEL_WORKFLOW_ID"),
        "SPACE_ID": _required_env("COZE_SPACE_ID"),
        "API_TOKEN": _required_env("COZE_API_TOKEN"),
        "url": os.getenv("COZE_BASE_URL", "https://api.coze.cn/v1/workflow/run"),
    }


def Coze_API_KEY_DINING():
    return {
        "WORKFLOW_ID": _required_env("COZE_DINING_WORKFLOW_ID"),
        "SPACE_ID": _required_env("COZE_SPACE_ID"),
        "API_TOKEN": _required_env("COZE_API_TOKEN"),
        "url": os.getenv("COZE_BASE_URL", "https://api.coze.cn/v1/workflow/run"),
    }
