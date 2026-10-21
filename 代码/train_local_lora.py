"""Train the local intent adapter without using test labels for model selection."""
import argparse
import hashlib
import importlib.metadata
import json
import math
import os
import platform
import random
import sys
import time
import traceback
from datetime import datetime, timezone, timedelta
from pathlib import Path

import numpy as np
import torch
from peft import LoraConfig, TaskType, get_peft_model
from torch.utils.data import Dataset
from transformers import AutoModelForCausalLM, AutoTokenizer, Trainer, TrainerCallback, TrainingArguments

from lora_data import encode_completion, split_training_rows

ROOT = Path(__file__).resolve().parent


def now():
    return datetime.now(timezone(timedelta(hours=8))).isoformat()


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(8 * 1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def write_json(path, value):
    Path(path).write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")


class EncodedDataset(Dataset):
    def __init__(self, rows, tokenizer, max_length):
        self.features = [encode_completion(row, tokenizer, max_length) for row in rows]

    def __len__(self):
        return len(self.features)

    def __getitem__(self, index):
        return self.features[index]


class CompletionCollator:
    def __init__(self, pad_token_id):
        self.pad_token_id = pad_token_id

    def __call__(self, rows):
        length = math.ceil(max(len(row["input_ids"]) for row in rows) / 8) * 8
        return {
            key: torch.tensor([row[key] + [padding] * (length - len(row[key])) for row in rows],
                              dtype=torch.long)
            for key, padding in [("input_ids", self.pad_token_id), ("attention_mask", 0), ("labels", -100)]
        }


class JsonLogCallback(TrainerCallback):
    def __init__(self, output):
        self.path = output / "metrics.jsonl"
        self.start = time.monotonic()

    def on_log(self, args, state, control, logs=None, **kwargs):
        record = {"time": now(), "step": state.global_step, "epoch": state.epoch,
                  "elapsed_seconds": time.monotonic() - self.start, **(logs or {})}
        for key, value in record.items():
            if isinstance(value, float) and not math.isfinite(value):
                raise FloatingPointError(f"Nonfinite metric {key}={value}")
        with self.path.open("a", encoding="utf-8") as handle:
            handle.write(json.dumps(record, ensure_ascii=False) + "\n")
        print(json.dumps(record, ensure_ascii=False), flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model-path", default=str(ROOT.parent / "模型" / "Qwen3-0.6B"))
    parser.add_argument("--train-data", default=str(ROOT.parent / "数据" / "train_data.json"))
    parser.add_argument("--test-data", default=str(ROOT.parent / "数据" / "test_data.json"))
    parser.add_argument("--run-dir", required=True)
    parser.add_argument("--epochs", type=float, default=3)
    parser.add_argument("--batch-size", type=int, default=2)
    parser.add_argument("--gradient-accumulation", type=int, default=4)
    parser.add_argument("--max-length", type=int, default=512)
    parser.add_argument("--learning-rate", type=float, default=1e-4)
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--max-steps", type=int, default=-1)
    parser.add_argument("--no-gradient-checkpointing", action="store_true",
                        help="Use more activation memory to avoid recomputation")
    args = parser.parse_args()
    output = Path(args.run_dir).resolve()
    output.mkdir(parents=True, exist_ok=False)
    write_json(output / "config.json", vars(args))
    started = now()
    try:
        if not torch.cuda.is_available():
            raise RuntimeError("CUDA is required for this local training configuration")
        os.environ.setdefault("CUBLAS_WORKSPACE_CONFIG", ":4096:8")
        random.seed(args.seed)
        np.random.seed(args.seed)
        torch.manual_seed(args.seed)
        torch.cuda.manual_seed_all(args.seed)
        torch.backends.cudnn.benchmark = False
        torch.backends.cudnn.deterministic = True
        torch.use_deterministic_algorithms(True, warn_only=True)
        torch.set_num_threads(4)
        environment = {
            "time": started, "python": sys.version, "platform": platform.platform(),
            "packages": {name: importlib.metadata.version(name)
                         for name in ["torch", "transformers", "peft", "accelerate", "numpy"]},
            "gpu": torch.cuda.get_device_name(0), "cuda": torch.version.cuda,
            "bf16_supported": torch.cuda.is_bf16_supported(),
            "pythonhashseed": os.getenv("PYTHONHASHSEED"), "threads": torch.get_num_threads(),
            "determinism": "seeded; deterministic_algorithms warn_only; cross-device equality not claimed",
        }
        write_json(output / "environment.json", environment)
        code_names = ["train_local_lora.py", "lora_data.py", "intent_common.py"]
        provenance = {
            "started_at": started, "argv": sys.argv,
            "train_sha256": sha256(args.train_data), "test_sha256": sha256(args.test_data),
            "model_config_sha256": sha256(Path(args.model_path) / "config.json"),
            "model_weights_sha256": sha256(Path(args.model_path) / "model.safetensors"),
            "code_sha256": {name: sha256(ROOT / name) for name in code_names},
            "purpose": "local application engineering; not a controlled scientific comparison",
            "selection_rule": "minimum validation completion-only loss over three epochs",
        }
        write_json(output / "provenance.json", provenance)
        rows = json.loads(Path(args.train_data).read_text(encoding="utf-8"))
        heldout = json.loads(Path(args.test_data).read_text(encoding="utf-8"))
        # Only test inputs are consulted for overlap removal; test labels never reach Trainer.
        train_rows, valid_rows, split = split_training_rows(
            rows, [{"input": row["input"]} for row in heldout], args.seed, .1)
        write_json(output / "split.json", split)
        tokenizer = AutoTokenizer.from_pretrained(args.model_path, local_files_only=True,
                                                  padding_side="right")
        if tokenizer.pad_token_id is None:
            tokenizer.pad_token = tokenizer.eos_token
        train_set = EncodedDataset(train_rows, tokenizer, args.max_length)
        valid_set = EncodedDataset(valid_rows, tokenizer, args.max_length)
        lengths = [len(row["input_ids"]) for row in train_set.features + valid_set.features]
        print(json.dumps({"train": len(train_set), "validation": len(valid_set),
                          "excluded_overlap": split["excluded_test_overlap_rows"],
                          "max_tokens": max(lengths), "gpu": environment["gpu"]}), flush=True)
        dtype = torch.bfloat16 if environment["bf16_supported"] else torch.float16
        model = AutoModelForCausalLM.from_pretrained(
            args.model_path, dtype=dtype, local_files_only=True,
            attn_implementation="sdpa").to("cuda")
        model.config.use_cache = False
        model = get_peft_model(model, LoraConfig(
            task_type=TaskType.CAUSAL_LM, r=8, lora_alpha=32, lora_dropout=.1,
            target_modules=["q_proj", "k_proj", "v_proj", "o_proj", "gate_proj", "up_proj", "down_proj"],
            bias="none"))
        model.enable_input_require_grads()
        model.print_trainable_parameters()
        train_args = TrainingArguments(
            output_dir=str(output / "checkpoints"), num_train_epochs=args.epochs,
            max_steps=args.max_steps, per_device_train_batch_size=args.batch_size,
            per_device_eval_batch_size=args.batch_size,
            gradient_accumulation_steps=args.gradient_accumulation,
            learning_rate=args.learning_rate, warmup_ratio=.05, weight_decay=1e-4,
            logging_steps=5, eval_strategy="epoch", save_strategy="epoch",
            save_total_limit=2, load_best_model_at_end=True,
            metric_for_best_model="eval_loss", greater_is_better=False,
            bf16=environment["bf16_supported"], fp16=not environment["bf16_supported"],
            gradient_checkpointing=not args.no_gradient_checkpointing,
            gradient_checkpointing_kwargs={"use_reentrant": False},
            report_to="none", dataloader_num_workers=0, dataloader_pin_memory=True,
            remove_unused_columns=False, seed=args.seed, data_seed=args.seed,
            disable_tqdm=True, optim="adamw_torch", max_grad_norm=1.0,
            logging_nan_inf_filter=False)
        trainer = Trainer(model=model, args=train_args, train_dataset=train_set,
                          eval_dataset=valid_set, data_collator=CompletionCollator(tokenizer.pad_token_id),
                          processing_class=tokenizer, callbacks=[JsonLogCallback(output)])
        result = trainer.train()
        final_eval = trainer.evaluate()
        adapter = output / "lora_adapter"
        trainer.model.save_pretrained(adapter)
        tokenizer.save_pretrained(adapter)
        trainer.save_state()
        summary = {
            "started_at": started, "ended_at": now(),
            "training_completed": args.max_steps == -1,
            "purpose": "full_training" if args.max_steps == -1 else "engineering_smoke_only",
            "train_samples": len(train_set), "validation_samples": len(valid_set),
            "excluded_test_overlap": split["excluded_test_overlap_rows"],
            "best_checkpoint": trainer.state.best_model_checkpoint,
            "best_validation_loss": trainer.state.best_metric,
            "train_metrics": result.metrics, "validation_metrics": final_eval,
            "peak_cuda_memory_bytes": torch.cuda.max_memory_allocated(),
            "adapter_path": str(adapter), "adapter_sha256": sha256(adapter / "adapter_model.safetensors"),
        }
        write_json(output / "training_summary.json", summary)
        print("TRAINING_COMPLETE " + json.dumps(summary, ensure_ascii=False), flush=True)
    except BaseException as error:
        write_json(output / "failure.json", {"started_at": started, "ended_at": now(),
                   "type": type(error).__name__, "error": str(error), "traceback": traceback.format_exc()})
        raise


if __name__ == "__main__":
    main()
