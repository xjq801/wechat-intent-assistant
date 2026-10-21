"""Small, non-streaming client for Qianwen's OpenAI-compatible chat API."""

import math
from urllib.parse import urlsplit

import requests

from tools.config import Qwen_API_KEY


class QwenClientError(RuntimeError):
    """Safe error details that never include credentials or provider bodies."""

    def __init__(self, code, status_code=None):
        self.code = code
        self.status_code = status_code
        message = code if status_code is None else f"{code} (HTTP {status_code})"
        super().__init__(message)


def qwen_chat_completion(messages, *, config=None, return_metadata=False):
    """Return text (optionally with usage) from one request, without retries."""
    settings = Qwen_API_KEY() if config is None else config
    api_key = settings.get("api_key", "")
    if not isinstance(api_key, str) or not api_key.strip():
        raise QwenClientError("QWEN_NOT_CONFIGURED")

    base_url = settings.get("base_url", "")
    model = settings.get("model", "")
    timeout = settings.get("timeout", 30)
    max_tokens = settings.get("max_tokens", 512)
    try:
        endpoint = urlsplit(base_url if isinstance(base_url, str) else "")
        valid_url = (
            endpoint.scheme == "https"
            and endpoint.hostname
            and not endpoint.username
            and not endpoint.password
            and not endpoint.query
            and not endpoint.fragment
        )
    except (TypeError, ValueError):
        valid_url = False
    if (
        not valid_url
        or not isinstance(model, str)
        or not model.strip()
        or isinstance(timeout, bool)
        or not isinstance(timeout, (int, float))
        or not math.isfinite(timeout)
        or timeout <= 0
        or isinstance(max_tokens, bool)
        or not isinstance(max_tokens, int)
        or max_tokens <= 0
    ):
        raise QwenClientError("QWEN_INVALID_CONFIG")

    try:
        response = requests.post(
            base_url.rstrip("/") + "/chat/completions",
            headers={
                "Authorization": f"Bearer {api_key.strip()}",
                "Content-Type": "application/json",
            },
            json={
                "model": model.strip(),
                "messages": messages,
                "enable_thinking": False,
                "max_tokens": max_tokens,
                "stream": False,
            },
            timeout=timeout,
            allow_redirects=False,
        )
    except requests.Timeout:
        raise QwenClientError("QWEN_TIMEOUT") from None
    except requests.RequestException:
        raise QwenClientError("QWEN_NETWORK_ERROR") from None

    try:
        status = response.status_code
        if status != 200:
            if status in (401, 403):
                code = "QWEN_AUTH_ERROR"
            elif status == 429:
                code = "QWEN_RATE_LIMITED"
            elif 300 <= status < 400:
                code = "QWEN_REDIRECT_BLOCKED"
            else:
                code = "QWEN_HTTP_ERROR"
            # The response body can contain provider diagnostics or request data.
            # Deliberately do not read it or use raise_for_status().
            raise QwenClientError(code, status_code=status)
        try:
            payload = response.json()
        except ValueError:
            raise QwenClientError("QWEN_INVALID_RESPONSE") from None
        if not isinstance(payload, dict):
            raise QwenClientError("QWEN_INVALID_RESPONSE")
        choices = payload.get("choices")
        if choices is None or choices == []:
            raise QwenClientError("QWEN_EMPTY_RESPONSE")
        if not isinstance(choices, list) or not isinstance(choices[0], dict):
            raise QwenClientError("QWEN_INVALID_RESPONSE")
        message = choices[0].get("message")
        if not isinstance(message, dict):
            raise QwenClientError("QWEN_INVALID_RESPONSE")
        content = message.get("content")
        if content is None or (isinstance(content, str) and not content.strip()):
            raise QwenClientError("QWEN_EMPTY_RESPONSE")
        if not isinstance(content, str):
            raise QwenClientError("QWEN_INVALID_RESPONSE")
        text = content.strip()
        if return_metadata:
            return {
                "text": text,
                "model": payload.get("model"),
                "usage": payload.get("usage"),
                "request_id": response.headers.get("x-request-id"),
                "completion_id": payload.get("id"),
            }
        return text
    finally:
        response.close()
