import ast
import contextlib
import io
from pathlib import Path
import traceback
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

import requests

from tools.qwen_client import QwenClientError, qwen_chat_completion


class QwenClientTests(unittest.TestCase):
    def setUp(self):
        # Fake credentials only. No environment or local credential store is read.
        self.config = {
            "api_key": "test-only-secret-do-not-log",
            "base_url": "https://maas.qianwenaiapi.com/compatible-mode/v1/",
            "model": "qwen3.8-flash",
            "timeout": 20,
            "max_tokens": 256,
        }
        self.messages = [{"role": "user", "content": "请仅回复测试成功"}]

    def response(self, payload=None, status=200):
        response = Mock(status_code=status)
        response.json.return_value = payload
        response.text = "provider-body-test-only-secret-do-not-log"
        return response

    def assert_safe_failure(self, post, expected_code, *, status=None):
        stdout, stderr = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
            try:
                qwen_chat_completion(self.messages, config=self.config)
            except QwenClientError as exc:
                self.assertEqual(exc.code, expected_code)
                self.assertEqual(exc.status_code, status)
                displayed = "".join(traceback.format_exception(exc))
            else:
                self.fail("Expected QwenClientError")
        for displayed_text in (stdout.getvalue(), stderr.getvalue(), displayed):
            self.assertNotIn(self.config["api_key"], displayed_text)
            self.assertNotIn("provider-body", displayed_text)
        post.assert_called_once()

    def test_success_uses_exact_endpoint_model_and_bounded_request(self):
        response = self.response({"choices": [{"message": {"content": " 测试成功 "}}]})
        with patch("tools.qwen_client.requests.post", return_value=response) as post:
            output = io.StringIO()
            with contextlib.redirect_stdout(output), contextlib.redirect_stderr(output):
                text = qwen_chat_completion(self.messages, config=self.config)
            self.assertEqual(text, "测试成功")
            self.assertEqual(output.getvalue(), "")
            post.assert_called_once_with(
                "https://maas.qianwenaiapi.com/compatible-mode/v1/chat/completions",
                headers={
                    "Authorization": "Bearer test-only-secret-do-not-log",
                    "Content-Type": "application/json",
                },
                json={
                    "model": "qwen3.8-flash",
                    "messages": self.messages,
                    "enable_thinking": False,
                    "max_tokens": 256,
                    "stream": False,
                },
                timeout=20,
                allow_redirects=False,
            )
        response.close.assert_called_once()

    def test_default_config_is_resolved_without_changing_model(self):
        response = self.response({"choices": [{"message": {"content": "测试成功"}}]})
        with patch("tools.qwen_client.Qwen_API_KEY", return_value=self.config) as config:
            with patch("tools.qwen_client.requests.post", return_value=response) as post:
                self.assertEqual(qwen_chat_completion(self.messages), "测试成功")
                self.assertEqual(post.call_args.kwargs["json"]["model"], "qwen3.8-flash")
        config.assert_called_once()

    def test_success_can_return_selected_verification_metadata(self):
        response = self.response({
            "choices": [{"message": {"content": "测试成功"}}],
            "model": "qwen3.8-flash",
            "usage": {"prompt_tokens": 8, "completion_tokens": 4, "total_tokens": 12},
            "id": "chatcmpl-test",
        })
        response.headers = {"x-request-id": "request-test"}
        with patch("tools.qwen_client.requests.post", return_value=response) as post:
            result = qwen_chat_completion(self.messages, config=self.config, return_metadata=True)
        self.assertEqual(result, {
            "text": "测试成功",
            "model": "qwen3.8-flash",
            "usage": {"prompt_tokens": 8, "completion_tokens": 4, "total_tokens": 12},
            "request_id": "request-test",
            "completion_id": "chatcmpl-test",
        })
        post.assert_called_once()
        response.close.assert_called_once()

    def test_http_failures_never_read_or_display_body_or_retry(self):
        errors = {
            301: "QWEN_REDIRECT_BLOCKED",
            307: "QWEN_REDIRECT_BLOCKED",
            400: "QWEN_HTTP_ERROR",
            401: "QWEN_AUTH_ERROR",
            403: "QWEN_AUTH_ERROR",
            404: "QWEN_HTTP_ERROR",
            429: "QWEN_RATE_LIMITED",
            500: "QWEN_HTTP_ERROR",
        }
        for status, code in errors.items():
            with self.subTest(status=status):
                response = self.response(status=status)
                with patch("tools.qwen_client.requests.post", return_value=response) as post:
                    self.assert_safe_failure(post, code, status=status)
                response.json.assert_not_called()
                response.close.assert_called_once()

    def test_network_error_and_timeout_are_sanitized_without_retry(self):
        for error, code in (
            (requests.Timeout("provider-body " + self.config["api_key"]), "QWEN_TIMEOUT"),
            (requests.ConnectionError("provider-body " + self.config["api_key"]), "QWEN_NETWORK_ERROR"),
        ):
            with self.subTest(code=code):
                with patch("tools.qwen_client.requests.post", side_effect=error) as post:
                    self.assert_safe_failure(post, code)

    def test_empty_responses_fail_explicitly(self):
        for payload in (
            {},
            {"choices": []},
            {"choices": [{"message": {"content": None}}]},
            {"choices": [{"message": {"content": ""}}]},
            {"choices": [{"message": {"content": " \n "}}]},
        ):
            with self.subTest(payload=payload):
                response = self.response(payload)
                with patch("tools.qwen_client.requests.post", return_value=response) as post:
                    self.assert_safe_failure(post, "QWEN_EMPTY_RESPONSE")
                response.close.assert_called_once()

    def test_invalid_payloads_fail_without_echoing_content(self):
        for payload in (
            [],
            {"choices": {}},
            {"choices": ["provider-body " + self.config["api_key"]]},
            {"choices": [{}]},
            {"choices": [{"message": {"content": ["unsupported"]}}]},
        ):
            with self.subTest(payload_type=type(payload).__name__):
                response = self.response(payload)
                with patch("tools.qwen_client.requests.post", return_value=response) as post:
                    self.assert_safe_failure(post, "QWEN_INVALID_RESPONSE")
                response.close.assert_called_once()
        response = self.response()
        response.json.side_effect = ValueError("provider-body " + self.config["api_key"])
        with patch("tools.qwen_client.requests.post", return_value=response) as post:
            self.assert_safe_failure(post, "QWEN_INVALID_RESPONSE")

    def test_missing_key_stops_before_network(self):
        with patch("tools.qwen_client.requests.post") as post:
            with self.assertRaises(QwenClientError) as result:
                qwen_chat_completion(self.messages, config=dict(self.config, api_key=" "))
        self.assertEqual(result.exception.code, "QWEN_NOT_CONFIGURED")
        post.assert_not_called()

    def test_invalid_config_stops_before_network(self):
        for override in (
            {"base_url": "http://maas.qianwenaiapi.com/compatible-mode/v1"},
            {"base_url": "https://user:password@example.com/v1"},
            {"base_url": "https://example.com/v1?api_key=secret"},
            {"model": ""},
            {"timeout": 0},
            {"timeout": float("inf")},
            {"max_tokens": 0},
            {"max_tokens": True},
        ):
            with self.subTest(fields=tuple(override)):
                with patch("tools.qwen_client.requests.post") as post:
                    with self.assertRaises(QwenClientError) as result:
                        qwen_chat_completion(self.messages, config=dict(self.config, **override))
                self.assertEqual(result.exception.code, "QWEN_INVALID_CONFIG")
                post.assert_not_called()


class QwenRoutingTests(unittest.TestCase):
    """Exercise the entry points without importing models or media SDKs."""

    @staticmethod
    def load_functions(relative_path, names, namespace):
        source = Path(__file__).resolve().parents[1] / relative_path
        tree = ast.parse(source.read_text(encoding="utf-8"), filename=str(source))
        functions = [
            item for item in tree.body
            if isinstance(item, ast.FunctionDef) and item.name in names
        ]
        if len(functions) != len(names):
            raise AssertionError("Expected entry-point functions were not found")
        module = ast.Module(body=functions, type_ignores=[])
        exec(compile(module, str(source), "exec"), namespace)
        return namespace

    def bot_namespace(self, api_key="fake-locally-loaded-key"):
        return self.load_functions(
            "bot.py",
            {"qwen_simulated_recommendation", "generate_reply"},
            {
                "Qwen_API_KEY": Mock(return_value={"api_key": api_key}),
                "qwen_chat_completion": Mock(return_value="模拟建议正文"),
                "QwenClientError": QwenClientError,
                # All environment variables are absent; key comes from config.
                "os": SimpleNamespace(getenv=Mock(return_value=None)),
            },
        )

    def test_local_stored_key_enables_simulation_without_environment_key(self):
        namespace = self.bot_namespace()
        result = namespace["generate_reply"]("吃饭", "想吃清淡的晚餐")
        self.assertIn("意图：吃饭", result)
        self.assertIn("模拟建议正文", result)
        namespace["qwen_chat_completion"].assert_called_once()

    def test_missing_key_preserves_recognized_need_without_network_call(self):
        namespace = self.bot_namespace(api_key="")
        result = namespace["generate_reply"]("出行", "北京到上海")
        self.assertIn("需求：北京到上海", result)
        self.assertIn("尚未配置千问 API", result)
        namespace["qwen_chat_completion"].assert_not_called()

    def test_both_simulation_prompts_keep_non_live_data_disclosures(self):
        for intent, disclosure, verification in (
            ("出行", "【模拟出行建议（非实时票务）】", "12306实时查询"),
            ("吃饭", "【模拟餐厅建议（非实时地图数据）】", "地图平台核验"),
        ):
            with self.subTest(intent=intent):
                namespace = self.bot_namespace()
                namespace["qwen_simulated_recommendation"](intent, "用户的需求")
                arguments = namespace["qwen_chat_completion"].call_args
                self.assertIn(disclosure, arguments.args[0][0]["content"])
                self.assertIn(verification, arguments.args[0][0]["content"])
                self.assertEqual(arguments.args[0][1]["content"], "用户的需求")
                self.assertEqual(arguments.kwargs["config"]["api_key"], "fake-locally-loaded-key")

    def test_bot_error_log_omits_unexpected_exception_details(self):
        namespace = self.bot_namespace()
        namespace["qwen_chat_completion"].side_effect = RuntimeError("private-error-body")
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            result = namespace["generate_reply"]("吃饭", "清淡晚餐")
        self.assertIn("QWEN_UNEXPECTED_ERROR", output.getvalue())
        self.assertNotIn("private-error-body", output.getvalue())
        self.assertNotIn("private-error-body", result)

    def test_dining_and_travel_share_the_client(self):
        for path in ("tools/dining.py", "tools/travel.py"):
            with self.subTest(path=path):
                client = Mock(return_value="模型输出")
                namespace = self.load_functions(path, {"qwen_ask"}, {"qwen_chat_completion": client})
                self.assertEqual(namespace["qwen_ask"]("系统指令", "用户需求"), "模型输出")
                client.assert_called_once_with([
                    {"role": "system", "content": "系统指令"},
                    {"role": "user", "content": "用户需求"},
                ])


if __name__ == "__main__":
    unittest.main()
