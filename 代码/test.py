import argparse
import csv
import json
import os
import re

import matplotlib.pyplot as plt
import seaborn as sns
import torch
from peft import PeftModel
from sklearn.metrics import accuracy_score, classification_report, confusion_matrix
from transformers import AutoModelForCausalLM, AutoTokenizer


LABELS = ["吃饭", "出行", "无意图"]
DISPLAY_LABELS = ["Dining", "Travel", "No intent"]


def parse_args():
    parser = argparse.ArgumentParser(description="Evaluate the Chapter 2 Qwen3 LoRA model")
    parser.add_argument("--model-path", default="Qwen3-0.6B")
    parser.add_argument("--adapter-path", default="model_output/lora_qwen_0.6b_intent/lora_adapter")
    parser.add_argument("--data-path", default="data/test_data.json")
    parser.add_argument("--output-dir", default="model_output/test_results_qwen_0.6b")
    parser.add_argument("--max-samples", type=int, default=0)
    parser.add_argument("--max-new-tokens", type=int, default=192)
    parser.add_argument("--batch-size", type=int, default=4)
    return parser.parse_args()


def prompt_for(sample):
    return f"""<|im_start|>system
你是一个专业的群聊意图识别助手，请根据群聊对话历史识别核心意图。<|im_end|>
<|im_start|>user
{sample['instruction']}

对话内容：
{sample['input']}<|im_end|>
<|im_start|>assistant
"""


def parse_json(text):
    match = re.search(r"\{.*?\}", text, re.DOTALL)
    if not match:
        return {}
    try:
        return json.loads(match.group())
    except json.JSONDecodeError:
        return {}


def main():
    args = parse_args()
    os.makedirs(args.output_dir, exist_ok=True)
    with open(args.data_path, "r", encoding="utf-8") as handle:
        samples = json.load(handle)
    if args.max_samples > 0:
        samples = samples[:args.max_samples]

    tokenizer = AutoTokenizer.from_pretrained(args.model_path, trust_remote_code=True, padding_side="left")
    if tokenizer.pad_token is None:
        tokenizer.pad_token = tokenizer.eos_token
    base = AutoModelForCausalLM.from_pretrained(
        args.model_path,
        dtype=torch.bfloat16 if torch.cuda.is_available() else torch.float32,
        device_map="auto" if torch.cuda.is_available() else None,
        trust_remote_code=True,
    )
    model = PeftModel.from_pretrained(base, args.adapter_path)
    model.eval()

    rows = []
    true_intents, pred_intents = [], []
    true_actions, pred_actions = [], []
    for start in range(0, len(samples), args.batch_size):
        batch = samples[start:start + args.batch_size]
        inputs = tokenizer([prompt_for(sample) for sample in batch], return_tensors="pt", padding=True)
        inputs = {key: value.to(model.device) for key, value in inputs.items()}
        with torch.inference_mode():
            outputs = model.generate(
                **inputs,
                max_new_tokens=args.max_new_tokens,
                do_sample=False,
                pad_token_id=tokenizer.pad_token_id,
                eos_token_id=tokenizer.eos_token_id,
            )
        prompt_length = inputs["input_ids"].shape[1]
        for offset, (sample, output) in enumerate(zip(batch, outputs)):
            index = start + offset + 1
            expected = json.loads(sample["output"])
            raw = tokenizer.decode(output[prompt_length:], skip_special_tokens=True)
            predicted = parse_json(raw)
            true_intent = expected.get("intent_label", "")
            pred_intent = predicted.get("intent_label", "解析失败")
            true_action = expected.get("action_status", "")
            pred_action = predicted.get("action_status", "解析失败")
            true_intents.append(true_intent)
            pred_intents.append(pred_intent)
            true_actions.append(true_action)
            pred_actions.append(pred_action)
            rows.append({
                "index": index,
                "true_intent": true_intent,
                "pred_intent": pred_intent,
                "true_action": true_action,
                "pred_action": pred_action,
                "true_requirement": expected.get("requirement_label", ""),
                "pred_requirement": predicted.get("requirement_label", ""),
                "raw_response": raw,
            })
            print(f"[{index}/{len(samples)}] intent={true_intent}->{pred_intent} action={true_action}->{pred_action}")

    intent_accuracy = accuracy_score(true_intents, pred_intents)
    action_accuracy = accuracy_score(true_actions, pred_actions)
    report = classification_report(true_intents, pred_intents, labels=LABELS, output_dict=True, zero_division=0)
    matrix = confusion_matrix(true_intents, pred_intents, labels=LABELS)

    with open(os.path.join(args.output_dir, "predictions.csv"), "w", encoding="utf-8-sig", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=rows[0].keys())
        writer.writeheader()
        writer.writerows(rows)
    metrics = {
        "sample_count": len(samples),
        "intent_accuracy": intent_accuracy,
        "action_accuracy": action_accuracy,
        "parse_failure_count": sum(value == "解析失败" for value in pred_intents),
        "classification_report": report,
        "confusion_matrix_labels": LABELS,
        "confusion_matrix": matrix.tolist(),
    }
    with open(os.path.join(args.output_dir, "metrics.json"), "w", encoding="utf-8") as handle:
        json.dump(metrics, handle, ensure_ascii=False, indent=2)

    plt.figure(figsize=(7, 6))
    sns.heatmap(matrix, annot=True, fmt="d", cmap="Blues", xticklabels=DISPLAY_LABELS, yticklabels=DISPLAY_LABELS)
    plt.xlabel("Predicted label")
    plt.ylabel("True label")
    plt.title("Intent Classification Confusion Matrix")
    plt.tight_layout()
    plt.savefig(os.path.join(args.output_dir, "confusion_matrix.png"), dpi=180)
    plt.close()
    print("EVALUATION_COMPLETE")
    print(json.dumps(metrics, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
