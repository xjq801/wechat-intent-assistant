"""Local retry policy tests; no model weights, GPU or network calls."""

import json
import threading
import unittest
from unittest.mock import Mock

import torch

from intent_common import INSTRUCTION, build_correction_prompt, build_prompt
from model_loader import IntentPredictionError, ModelManager, ask_f
from unittest.mock import patch


VALID = {
    "intent_label": "出行",
    "requirement_label": "北京到上海",
    "action_status": "取消行动",
}
VALID_TEXT = json.dumps(VALID, ensure_ascii=False)
CONFLICT = '{"intent_label":"无意图","requirement_label":"无","action_status":"取消行动"}'


class CharacterTokenizer:
    """Exact round-trip tokenizer to verify prompt budgeting on CPU."""

    def encode(self, text, **kwargs):
        return [ord(char) for char in text]

    def decode(self, values, **kwargs):
        return "".join(chr(int(value)) for value in values)

    def __call__(self, text, **kwargs):
        return {"input_ids": torch.tensor([self.encode(text)], dtype=torch.long)}


def fake_manager(responses):
    manager = ModelManager.__new__(ModelManager)
    manager._predict_lock = threading.Lock()
    manager.load = Mock()
    manager._generate_response = Mock(side_effect=responses)
    return manager


class IntentRetryTests(unittest.TestCase):
    def test_first_success_does_not_retry(self):
        manager = fake_manager([VALID_TEXT])
        self.assertEqual(manager.predict("甲：不去上海了"), VALID)
        manager._generate_response.assert_called_once_with("甲：不去上海了")

    def test_conflict_then_valid_retries_once_without_editing_labels(self):
        dialogue = "甲：明天去上海。\n乙：不去了，取消这次出行。"
        manager = fake_manager([CONFLICT, VALID_TEXT])
        self.assertEqual(manager.predict(dialogue), VALID)
        self.assertEqual(manager._generate_response.call_count, 2)
        self.assertEqual(manager._generate_response.call_args_list[1].args, (dialogue,))
        self.assertEqual(manager._generate_response.call_args_list[1].kwargs, {"correction": True})

    def test_two_invalid_responses_raise_and_never_make_third_attempt(self):
        manager = fake_manager(["not JSON", CONFLICT, VALID_TEXT])
        with self.assertRaisesRegex(IntentPredictionError, "一次结构纠正"):
            manager.predict("甲：取消出行")
        self.assertEqual(manager._generate_response.call_count, 2)

    def test_generation_errors_are_not_retried_even_if_value_error(self):
        for error in (RuntimeError("generation failed"), ValueError("tokenizer config")):
            with self.subTest(error=type(error).__name__):
                manager = fake_manager([error, VALID_TEXT])
                with self.assertRaises(type(error)):
                    manager.predict("甲：取消出行")
                self.assertEqual(manager._generate_response.call_count, 1)

    def test_retry_generation_error_is_propagated_without_third_attempt(self):
        manager = fake_manager([CONFLICT, RuntimeError("generation failed"), VALID_TEXT])
        with self.assertRaisesRegex(RuntimeError, "generation failed"):
            manager.predict("甲：取消出行")
        self.assertEqual(manager._generate_response.call_count, 2)

    def test_correction_prompt_changes_instruction_but_preserves_original_dialogue(self):
        dialogue = "甲：去北京。\n乙：预算六百。\n甲：这次取消。"
        first = build_prompt(dialogue)
        correction = build_correction_prompt(dialogue)
        self.assertNotEqual(first, correction)
        self.assertIn(INSTRUCTION, first)
        self.assertIn(INSTRUCTION, correction)
        self.assertIn(dialogue, correction)
        self.assertIn("结构纠正", correction)
        self.assertTrue(first.endswith(dialogue + "<|im_end|>\n<|im_start|>assistant\n"))

    def test_retry_truncation_keeps_instruction_and_latest_cancellation(self):
        manager = ModelManager.__new__(ModelManager)
        manager.tokenizer = CharacterTokenizer()
        overhead = len(build_correction_prompt(""))
        manager.max_input_tokens = overhead + 80
        recent = "乙：不去上海了，取消本次出行。"
        dialogue = "甲：以前的计划。\n" * 300 + recent
        result = manager._tokenize_dialogue(dialogue, correction=True)
        text = manager.tokenizer.decode(result["input_ids"][0])
        self.assertLessEqual(result["input_ids"].shape[1], manager.max_input_tokens)
        self.assertIn(INSTRUCTION, text)
        self.assertIn("结构纠正", text)
        self.assertIn(recent, text)
        self.assertTrue(text.endswith(recent + "<|im_end|>\n<|im_start|>assistant\n"))

    def test_ask_f_preserves_failure_for_visible_bot_feedback(self):
        predictor = Mock()
        predictor.predict.side_effect = IntentPredictionError("一次结构纠正仍未通过")
        with patch("model_loader.get_predictor", return_value=predictor):
            with self.assertRaises(IntentPredictionError):
                ask_f("甲：取消出行")


if __name__ == "__main__":
    unittest.main()
