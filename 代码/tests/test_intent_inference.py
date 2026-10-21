import concurrent.futures
import json
from pathlib import Path
import tempfile
import threading
import time
import unittest
from unittest.mock import patch

from intent_common import build_prompt, parse_prediction, validate_prediction
import model_loader


class IntentContractTests(unittest.TestCase):
    def test_real_neutral_label_and_action_constraints(self):
        neutral = {"intent_label": "无意图", "requirement_label": "无", "action_status": "无"}
        self.assertEqual(validate_prediction(neutral), neutral)
        for intent, action in (("无意图", "继续行动"), ("无意图", "取消行动"), ("吃饭", "无"), ("出行", "无")):
            with self.subTest(intent=intent, action=action), self.assertRaises(ValueError):
                validate_prediction(dict(neutral, intent_label=intent, action_status=action))

    def test_json_string_braces_and_newlines_survive_parsing(self):
        value = {"intent_label": "吃饭", "requirement_label": '找一家叫“{小馆}”的店', "action_status": "继续行动"}
        self.assertEqual(parse_prediction(json.dumps(value, ensure_ascii=False)), value)
        newline = '{"intent_label":"出行","requirement_label":"北京\n到上海","action_status":"继续行动"}'
        self.assertEqual(parse_prediction(newline)["requirement_label"], "北京 到上海")

    def test_schema_rejects_ambiguous_or_malformed_outputs(self):
        valid = '{"intent_label":"吃饭","requirement_label":"火锅","action_status":"继续行动"}'
        invalid = (
            valid + valid,
            valid[:-1] + ',"intent_label":"出行"}',
            valid[:-1] + ',"confidence":0.9}',
            valid.replace('"火锅"', "null"),
            valid.replace('"继续行动"', '"已完成"'),
            "模型认为：" + valid,
        )
        for response in invalid:
            with self.subTest(response=response), self.assertRaises(ValueError):
                parse_prediction(response)

    def test_prompt_contains_no_thinking_prefix(self):
        self.assertTrue(build_prompt("甲：吃火锅").endswith("甲：吃火锅<|im_end|>\n<|im_start|>assistant\n"))
        self.assertNotIn("<think>", build_prompt("甲：吃火锅"))


class LoaderSafetyTests(unittest.TestCase):
    def test_paths_are_independent_of_working_directory_and_support_legacy_launcher(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            code = root / "代码"
            code.mkdir()
            downloaded = root / "模型" / "Qwen3-0.6B"
            downloaded.mkdir(parents=True)
            with patch.object(model_loader, "PROJECT_DIR", code):
                manager = model_loader.ModelManager("Qwen3-0.6B", "model_output/adapter")
                self.assertEqual(Path(manager.base_model_path), downloaded)
                self.assertEqual(Path(manager.lora_path), code / "model_output" / "adapter")
                custom = model_loader.ModelManager("different-model", "custom-adapter")
                self.assertEqual(Path(custom.base_model_path), code / "different-model")

    def test_simultaneous_requests_create_only_one_manager(self):
        start = threading.Barrier(8)
        instance = object()

        def construct():
            time.sleep(0.02)
            return instance

        def request():
            start.wait()
            return model_loader.get_predictor()

        with patch.object(model_loader, "_predictor", None), patch.object(model_loader, "ModelManager", side_effect=construct) as factory:
            with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
                results = list(pool.map(lambda _: request(), range(8)))
            self.assertEqual(factory.call_count, 1)
            self.assertTrue(all(result is instance for result in results))


if __name__ == "__main__":
    unittest.main()
