import os
import unittest
from unittest.mock import patch

from tools.config import Qwen_API_KEY


class QwenConfigTests(unittest.TestCase):
    def test_stored_key_and_explicit_model(self):
        with patch.dict(os.environ, {}, clear=True), \
             patch("tools.config._qwen_settings", return_value={
                 "base_url": "https://maas.qianwenaiapi.com/compatible-mode/v1",
                 "model": "qwen3.8-flash",
             }), \
             patch("tools.config.load_qwen_key", return_value="fake-test-only"):
            config = Qwen_API_KEY()
        self.assertEqual(config["api_key"], "fake-test-only")
        self.assertEqual(config["model"], "qwen3.8-flash")
        self.assertEqual(config["dashscope_base_url"], "https://maas.qianwenaiapi.com/api/v1")

    def test_offline_test_cannot_use_stored_or_environment_key(self):
        with patch.dict(os.environ, {
            "CHAPTER2_DISABLE_CLOUD_API": "1", "QWEN_API_KEY": "fake-env-key",
        }, clear=True), patch("tools.config._qwen_settings", return_value={}), \
             patch("tools.config.load_qwen_key", side_effect=AssertionError("Must not read stored key")):
            self.assertEqual(Qwen_API_KEY()["api_key"], "")

    def test_reject_credential_url_and_plain_http(self):
        for endpoint in (
            "http://maas.qianwenaiapi.com/compatible-mode/v1",
            "https://user:password@example.com/compatible-mode/v1",
            "https://maas.qianwenaiapi.com/compatible-mode/v1?key=value",
        ):
            with self.subTest(endpoint=endpoint), \
                 patch.dict(os.environ, {"QWEN_BASE_URL": endpoint}, clear=True), \
                 patch("tools.config._qwen_settings", return_value={}):
                with self.assertRaises(RuntimeError):
                    Qwen_API_KEY()


if __name__ == "__main__":
    unittest.main()
