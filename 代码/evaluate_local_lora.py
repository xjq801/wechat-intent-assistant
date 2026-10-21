"""Run the fixed held-out intent evaluation once, retaining every failed output.

Only the input field is passed to generation. Gold outputs are decoded after all
generation finishes; this script does not train, select, or tune an adapter.
"""
import argparse
import csv
import hashlib
import importlib.metadata
import json
import os
import platform
import sys
import time
import traceback
from datetime import datetime, timedelta, timezone
from pathlib import Path

from intent_common import (
    ACTION_LABELS, INTENT_LABELS, build_prompt, parse_prediction, validate_prediction,
)


ROOT = Path(__file__).resolve().parent
PARSE_FAILURE = "__PARSE_OR_GENERATION_FAILURE__"


def now():
    return datetime.now(timezone(timedelta(hours=8))).isoformat()


def sha256(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as handle:
        for chunk in iter(lambda: handle.read(8 * 1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def input_hash(text):
    """Hash exact UTF-8 input, without whitespace normalization."""
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def write_json(path, value):
    Path(path).write_text(
        json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False),
        encoding="utf-8",
    )


def hash_directory(path):
    root = Path(path)
    return {
        file.relative_to(root).as_posix(): {
            "size_bytes": file.stat().st_size, "sha256": sha256(file),
        }
        for file in sorted(root.rglob("*"))
        if file.is_file()
    }


def classify_metrics(records, field, labels):
    """Invalid predictions contribute a false negative, never disappear."""
    labels = sorted(labels)
    columns = labels + [PARSE_FAILURE]
    confusion = {label: {column: 0 for column in columns} for label in labels}
    for record in records:
        truth = record["expected"][field]
        predicted = record["predicted"]
        guess = predicted[field] if predicted is not None else PARSE_FAILURE
        if truth not in confusion or guess not in columns:
            raise ValueError(f"Unexpected {field} label: {truth!r}, {guess!r}")
        confusion[truth][guess] += 1
    per_class = {}
    for label in labels:
        tp = confusion[label][label]
        fp = sum(confusion[other][label] for other in labels if other != label)
        fn = sum(value for guess, value in confusion[label].items() if guess != label)
        precision = tp / (tp + fp) if tp + fp else 0.0
        recall = tp / (tp + fn) if tp + fn else 0.0
        f1 = 2 * tp / (2 * tp + fp + fn) if 2 * tp + fp + fn else 0.0
        per_class[label] = {
            "support": tp + fn, "true_positive": tp, "false_positive": fp,
            "false_negative": fn, "precision": precision, "recall": recall, "f1": f1,
        }
    correct = sum(confusion[label][label] for label in labels)
    return {
        "n": len(records), "correct": correct, "accuracy": correct / len(records),
        "macro_f1": sum(row["f1"] for row in per_class.values()) / len(labels),
        "macro_f1_definition": "Unweighted mean over all three predefined labels; zero division is 0.",
        "per_class": per_class, "confusion_row_labels": labels,
        "confusion_column_labels": columns, "confusion": confusion,
    }


def compute_metrics(records):
    if not records:
        raise ValueError("Cannot evaluate an empty dataset")
    indices = [record["index"] for record in records]
    if indices != list(range(len(records))):
        raise ValueError("Records must preserve every zero-based input index exactly once")
    for record in records:
        validate_prediction(record["expected"])
        if record["predicted"] is not None:
            validate_prediction(record["predicted"])
    n = len(records)
    valid = sum(record["predicted"] is not None for record in records)
    exact_requirement = sum(
        record["predicted"] is not None
        and record["expected"]["requirement_label"] == record["predicted"]["requirement_label"]
        for record in records
    )
    exact_all = sum(
        record["predicted"] is not None and record["expected"] == record["predicted"]
        for record in records
    )
    return {
        "n": n, "valid_predictions": valid, "invalid_predictions": n - valid,
        "json_valid_rate": valid / n,
        "json_valid_rate_definition": (
            "Accepted by the application's shared parse_prediction contract, including its "
            "documented formatting wrappers/control-character recovery; invalid outputs are failures."
        ),
        "requirement_exact_matches": exact_requirement,
        "requirement_exact_match_rate": exact_requirement / n,
        "requirement_exact_match_note": (
            "Exact string agreement after shared field trimming; this is NOT semantic quality. "
            "Paraphrases may be correct in meaning and still score 0. Invalid outputs score 0."
        ),
        "all_fields_exact_matches": exact_all,
        "all_fields_exact_match_rate": exact_all / n,
        "intent": classify_metrics(records, "intent_label", INTENT_LABELS),
        "action": classify_metrics(records, "action_status", ACTION_LABELS),
        "denominator_policy": "All held-out rows, including parsing and generation failures.",
    }


def tokenize_dialogue(tokenizer, dialogue, max_input_tokens):
    """Match ModelManager._tokenize_dialogue; return unpadded token lists."""
    inputs = tokenizer(build_prompt(dialogue), add_special_tokens=False)
    if len(inputs["input_ids"]) <= max_input_tokens:
        return inputs
    overhead = len(tokenizer.encode(build_prompt(""), add_special_tokens=False))
    budget = max_input_tokens - overhead
    if budget < 1:
        raise ValueError("CHAPTER2_MAX_INPUT_TOKENS cannot preserve the full instruction")
    dialogue_tokens = tokenizer.encode(dialogue, add_special_tokens=False)
    while budget > 0:
        tail = tokenizer.decode(dialogue_tokens[-budget:], skip_special_tokens=False)
        inputs = tokenizer(build_prompt(tail), add_special_tokens=False)
        overflow = len(inputs["input_ids"]) - max_input_tokens
        if overflow <= 0:
            return inputs
        budget -= max(1, overflow)
    raise ValueError("Dialogue cannot be encoded within the current input limit")


def record_response(index, dialogue, raw_response="", error=None):
    record = {
        "index": index, "input_hash": input_hash(dialogue), "predicted": None,
        "raw_response": raw_response, "error": error,
    }
    if error is None:
        try:
            record["predicted"] = parse_prediction(raw_response)
        except (TypeError, ValueError) as exc:
            record["error"] = f"{type(exc).__name__}: {exc}"
    return record


def save_predictions(output, records):
    fields = ("index", "input_hash", "expected", "predicted", "raw_response", "error")
    with (output / "predictions.jsonl").open("x", encoding="utf-8") as json_file:
        for record in records:
            json_file.write(json.dumps(record, ensure_ascii=False, allow_nan=False) + "\n")
    with (output / "predictions.csv").open("x", encoding="utf-8-sig", newline="") as csv_file:
        writer = csv.DictWriter(csv_file, fieldnames=fields)
        writer.writeheader()
        for record in records:
            csv_record = {key: record[key] for key in fields}
            for key in ("expected", "predicted"):
                csv_record[key] = json.dumps(csv_record[key], ensure_ascii=False)
            writer.writerow(csv_record)


def save_confusions(output, metrics):
    for task in ("intent", "action"):
        metric = metrics[task]
        columns = metric["confusion_column_labels"]
        with (output / f"{task}_confusion.csv").open("x", encoding="utf-8-sig", newline="") as handle:
            writer = csv.writer(handle)
            writer.writerow(["expected\\predicted"] + columns)
            for label in metric["confusion_row_labels"]:
                writer.writerow([label] + [metric["confusion"][label][col] for col in columns])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model-path", default=str(ROOT.parent / "模型" / "Qwen3-0.6B"))
    parser.add_argument("--adapter-path", required=True)
    parser.add_argument("--data-path", default=str(ROOT.parent / "数据" / "test_data.json"))
    parser.add_argument("--output-dir", required=True)
    parser.add_argument("--batch-size", type=int, default=4)
    parser.add_argument("--max-new-tokens", type=int, default=192)
    args = parser.parse_args()
    if args.batch_size < 1 or args.max_new_tokens < 1:
        parser.error("--batch-size and --max-new-tokens must be positive")
    output = Path(args.output_dir).resolve()
    # A run directory must be new, even if an existing directory is empty.
    output.mkdir(parents=True, exist_ok=False)
    started_at, started_clock = now(), time.monotonic()
    runtime = {"started_at": started_at, "status": "running"}
    max_input_tokens = int(os.getenv("CHAPTER2_MAX_INPUT_TOKENS", "1024"))
    config = {
        **vars(args), "max_input_tokens": max_input_tokens,
        "dtype": "bfloat16", "device": "cuda:0", "padding_side": "left",
        "do_sample": False, "temperature": 1.0, "top_p": 1.0, "top_k": 50,
        "local_files_only": True, "input_hash_definition": "sha256_exact_utf8",
        "generation_contract": (
            "Shared application prompt/parser and newest-dialogue truncation. Same greedy "
            "decoding settings, with explicit evaluation max_new_tokens and left-padded batches."
        ),
        "label_use": "Gold labels are decoded only after all generation; no model selection.",
    }
    write_json(output / "config.json", config)
    write_json(output / "runtime.json", runtime)
    try:
        # Delay heavy imports so metric tests never load a model or initialize CUDA.
        import torch
        from peft import PeftModel
        from transformers import AutoModelForCausalLM, AutoTokenizer

        if not torch.cuda.is_available():
            raise RuntimeError("CUDA is required for the held-out evaluation")
        if not torch.cuda.is_bf16_supported():
            raise RuntimeError("This evaluation requires a CUDA device with BF16 support")
        torch.set_num_threads(4)
        rows = json.loads(Path(args.data_path).read_text(encoding="utf-8"))
        if not isinstance(rows, list) or not rows:
            raise ValueError("The test data must be a nonempty JSON array")
        inputs = []
        for index, row in enumerate(rows):
            if not isinstance(row, dict) or not isinstance(row.get("input"), str):
                raise ValueError(f"Invalid input at test row {index}")
            inputs.append(row["input"])
        runtime["n"] = len(inputs)
        write_json(output / "provenance.json", {
            "argv": sys.argv, "data_sha256": sha256(args.data_path),
            "data_path": str(Path(args.data_path).resolve()),
            "model_path": str(Path(args.model_path).resolve()),
            "adapter_path": str(Path(args.adapter_path).resolve()),
            "model_files": hash_directory(args.model_path),
            "adapter_files": hash_directory(args.adapter_path),
            "code_sha256": {
                name: sha256(ROOT / name)
                for name in ("evaluate_local_lora.py", "intent_common.py", "model_loader.py")
            },
            "input_hashes_in_order": [input_hash(text) for text in inputs],
        })
        write_json(output / "environment.json", {
            "python": sys.version, "platform": platform.platform(),
            "packages": {
                name: importlib.metadata.version(name)
                for name in ("torch", "transformers", "peft", "accelerate")
            },
            "gpu": torch.cuda.get_device_name(0), "cuda": torch.version.cuda,
            "bf16_supported": True, "threads": torch.get_num_threads(),
        })
        load_clock = time.monotonic()
        tokenizer = AutoTokenizer.from_pretrained(
            args.model_path, local_files_only=True, trust_remote_code=True,
            padding_side="left",
        )
        if tokenizer.pad_token is None:
            tokenizer.pad_token = tokenizer.eos_token
        base = AutoModelForCausalLM.from_pretrained(
            args.model_path, dtype=torch.bfloat16,
            trust_remote_code=True, local_files_only=True,
        ).to("cuda:0")
        model = PeftModel.from_pretrained(base, args.adapter_path, local_files_only=True)
        model.eval()
        model.config.use_cache = True
        runtime["load_seconds"] = time.monotonic() - load_clock
        config.update({
            "pad_token_id": tokenizer.pad_token_id,
            "eos_token_id": tokenizer.eos_token_id,
            "actual_model_dtype": str(next(model.parameters()).dtype),
            "attention_implementation": getattr(model.config, "_attn_implementation", None),
            "generation_config": model.generation_config.to_dict(),
        })
        write_json(output / "config.json", config)
        records = []
        generation_clock = time.monotonic()
        with (output / "generation.jsonl").open("x", encoding="utf-8") as progress:
            for start in range(0, len(inputs), args.batch_size):
                batch_indices = list(range(start, min(start + args.batch_size, len(inputs))))
                features, ready, batch_records = [], [], {}
                for index in batch_indices:
                    try:
                        feature = tokenize_dialogue(tokenizer, inputs[index], max_input_tokens)
                        features.append(feature)
                        ready.append(index)
                    except Exception as exc:
                        batch_records[index] = record_response(
                            index, inputs[index], error=f"tokenization {type(exc).__name__}: {exc}",
                        )
                if ready:
                    try:
                        tokens = tokenizer.pad(features, padding=True, return_tensors="pt")
                        tokens = {key: value.to(model.device) for key, value in tokens.items()}
                        prompt_width = tokens["input_ids"].shape[1]
                        with torch.inference_mode():
                            generated = model.generate(
                                **tokens, max_new_tokens=args.max_new_tokens, do_sample=False,
                                temperature=1.0, top_p=1.0, top_k=50,
                                pad_token_id=tokenizer.pad_token_id,
                                eos_token_id=tokenizer.eos_token_id,
                            )
                        if len(generated) != len(ready):
                            raise RuntimeError("Generation did not return one output per input")
                        for index, sequence in zip(ready, generated):
                            response = tokenizer.decode(sequence[prompt_width:], skip_special_tokens=True)
                            batch_records[index] = record_response(index, inputs[index], response)
                    except Exception as exc:
                        message = f"generation {type(exc).__name__}: {exc}"
                        for index in ready:
                            batch_records[index] = record_response(index, inputs[index], error=message)
                for index in batch_indices:
                    record = batch_records[index]
                    records.append(record)
                    progress.write(json.dumps(record, ensure_ascii=False) + "\n")
                progress.flush()
                print(json.dumps({
                    "completed": len(records), "total": len(inputs),
                    "valid_so_far": sum(row["predicted"] is not None for row in records),
                    "elapsed_seconds": time.monotonic() - generation_clock,
                }), flush=True)
        torch.cuda.synchronize()
        runtime["generation_seconds"] = time.monotonic() - generation_clock
        # Gold labels enter only here, after the entire prediction stream is fixed.
        for record, source in zip(records, rows):
            expected = source.get("output")
            if isinstance(expected, str):
                expected = json.loads(expected)
            record["expected"] = validate_prediction(expected)
        save_predictions(output, records)
        metrics = compute_metrics(records)
        write_json(output / "metrics.json", metrics)
        save_confusions(output, metrics)
        runtime.update({
            "status": "complete", "completed_at": now(),
            "elapsed_seconds": time.monotonic() - started_clock,
            "peak_cuda_allocated_bytes": torch.cuda.max_memory_allocated(0),
            "peak_cuda_reserved_bytes": torch.cuda.max_memory_reserved(0),
        })
        write_json(output / "runtime.json", runtime)
        print(json.dumps({
            "output_dir": str(output), "n": metrics["n"],
            "json_valid_rate": metrics["json_valid_rate"],
            "intent_accuracy": metrics["intent"]["accuracy"],
            "intent_macro_f1": metrics["intent"]["macro_f1"],
            "action_accuracy": metrics["action"]["accuracy"],
            "action_macro_f1": metrics["action"]["macro_f1"],
            "requirement_exact_match_rate": metrics["requirement_exact_match_rate"],
        }, ensure_ascii=False), flush=True)
    except BaseException as exc:
        runtime.update({
            "status": "failed", "completed_at": now(),
            "elapsed_seconds": time.monotonic() - started_clock,
            "error": f"{type(exc).__name__}: {exc}",
        })
        write_json(output / "runtime.json", runtime)
        (output / "error.txt").write_text(traceback.format_exc(), encoding="utf-8")
        raise


if __name__ == "__main__":
    main()
