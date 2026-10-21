import os
from pathlib import Path
import threading

import torch
from peft import PeftModel
from transformers import AutoModelForCausalLM, AutoTokenizer

from intent_common import INSTRUCTION, build_correction_prompt, build_prompt, parse_prediction


PROJECT_DIR = Path(__file__).resolve().parent


def _absolute_path(path):
    resolved = Path(path).expanduser()
    if not resolved.is_absolute():
        resolved = PROJECT_DIR / resolved
    return resolved.resolve()


def _base_model_path(path):
    resolved = _absolute_path(path)
    # Older launchers set this literal name. Retain compatibility with the
    # actual downloaded sibling directory without overriding other user paths.
    if str(path) == "Qwen3-0.6B" and not resolved.is_dir():
        downloaded = PROJECT_DIR.parent / "模型" / "Qwen3-0.6B"
        if downloaded.is_dir():
            return downloaded.resolve()
    return resolved


BASE_MODEL_PATH = str(_base_model_path(os.getenv("CHAPTER2_BASE_MODEL") or "../模型/Qwen3-0.6B"))
LORA_PATH = str(_absolute_path(os.getenv("CHAPTER2_LORA_PATH") or "model_output/lora_qwen_0.6b_intent/lora_adapter"))


class IntentPredictionError(ValueError):
    """The local model failed the output contract after one correction."""


class ModelManager:
    def __init__(self, base_model_path=BASE_MODEL_PATH, lora_path=LORA_PATH):
        self.base_model_path = str(_base_model_path(base_model_path))
        self.lora_path = str(_absolute_path(lora_path))
        self.model = None
        self.tokenizer = None
        self.device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
        self.max_input_tokens = int(os.getenv("CHAPTER2_MAX_INPUT_TOKENS", "1024"))
        self._load_lock = threading.Lock()
        self._predict_lock = threading.Lock()

    def load(self):
        with self._load_lock:
            if self.model is not None:
                return
            if not os.path.isdir(self.base_model_path):
                raise FileNotFoundError(f"基础模型不存在: {self.base_model_path}")
            if not os.path.isdir(self.lora_path):
                raise FileNotFoundError(f"LoRA 适配器不存在: {self.lora_path}")
            print(f"正在加载基础模型: {self.base_model_path}")
            tokenizer = AutoTokenizer.from_pretrained(
                self.base_model_path, trust_remote_code=True, local_files_only=True,
            )
            if tokenizer.pad_token is None:
                tokenizer.pad_token = tokenizer.eos_token
            dtype = torch.float32
            if self.device.type == "cuda":
                dtype = torch.bfloat16 if torch.cuda.is_bf16_supported() else torch.float16
            base = AutoModelForCausalLM.from_pretrained(
                self.base_model_path,
                dtype=dtype,
                device_map="auto" if self.device.type == "cuda" else None,
                trust_remote_code=True,
                local_files_only=True,
            )
            model = PeftModel.from_pretrained(base, self.lora_path, local_files_only=True)
            model.eval()
            model.config.use_cache = True
            # Publish only fully initialized objects while holding the load lock.
            self.tokenizer = tokenizer
            self.model = model
            print(f"模型加载完成，设备: {self.device}")

    def _tokenize_dialogue(self, dialogue, *, correction=False):
        prompt_builder = build_correction_prompt if correction else build_prompt
        prompt = prompt_builder(dialogue)
        inputs = self.tokenizer(prompt, return_tensors="pt", add_special_tokens=False)
        if inputs["input_ids"].shape[1] <= self.max_input_tokens:
            return inputs
        overhead = len(self.tokenizer.encode(prompt_builder(""), add_special_tokens=False))
        budget = self.max_input_tokens - overhead
        if budget < 1:
            raise ValueError("CHAPTER2_MAX_INPUT_TOKENS 太小，无法保留完整识别指令")
        dialogue_tokens = self.tokenizer.encode(dialogue, add_special_tokens=False)
        # Keep the newest conversation text, including cancellation messages.
        # Re-tokenize the complete prompt to preserve training-time boundaries.
        while budget > 0:
            tail = self.tokenizer.decode(dialogue_tokens[-budget:], skip_special_tokens=False)
            inputs = self.tokenizer(prompt_builder(tail), return_tensors="pt", add_special_tokens=False)
            overflow = inputs["input_ids"].shape[1] - self.max_input_tokens
            if overflow <= 0:
                return inputs
            budget -= max(1, overflow)
        raise ValueError("对话无法在当前输入长度限制内编码")

    def _generate_response(self, dialogue, *, correction=False):
        """Generate one raw response; the caller holds the prediction lock."""
        inputs = self._tokenize_dialogue(dialogue, correction=correction)
        inputs = {key: value.to(self.model.device) for key, value in inputs.items()}
        with torch.inference_mode():
            outputs = self.model.generate(
                **inputs,
                max_new_tokens=256,
                do_sample=False,
                temperature=1.0,
                top_p=1.0,
                top_k=50,
                pad_token_id=self.tokenizer.pad_token_id,
                eos_token_id=self.tokenizer.eos_token_id,
            )
        return self.tokenizer.decode(
            outputs[0][inputs["input_ids"].shape[1]:], skip_special_tokens=True,
        )

    def predict(self, dialogue):
        self.load()
        with self._predict_lock:
            response = self._generate_response(dialogue)
            try:
                return parse_prediction(response)
            except ValueError:
                # Only output-contract failures trigger correction. Generation,
                # device and tokenization errors are deliberately outside this
                # handler, including ValueError raised by those operations.
                pass

            # Reuse the original dialogue, not the earlier truncated prompt or
            # the model's invalid answer. Token budgeting retains the complete
            # correction instruction and the newest dialogue text.
            response = self._generate_response(dialogue, correction=True)
            try:
                return parse_prediction(response)
            except ValueError as exc:
                raise IntentPredictionError(
                    "本地意图识别经过一次结构纠正仍未通过校验，未生成推荐。"
                ) from exc


_predictor = None
_predictor_lock = threading.Lock()


def get_predictor():
    global _predictor
    with _predictor_lock:
        if _predictor is None:
            _predictor = ModelManager()
        return _predictor


def ask_f(dialogue):
    """Return valid labels, or let the caller present a visible failure."""
    result = get_predictor().predict(dialogue)
    return result["intent_label"], result["requirement_label"], result["action_status"]
