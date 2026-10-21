import argparse
import json
import os
import random

import matplotlib.pyplot as plt
import numpy as np
import torch
from datasets import Dataset
from peft import LoraConfig, TaskType, get_peft_model
from transformers import (
    AutoModelForCausalLM,
    AutoTokenizer,
    DataCollatorForSeq2Seq,
    EarlyStoppingCallback,
    Trainer,
    TrainerCallback,
    TrainingArguments,
)


class LossRecorderCallback(TrainerCallback):
    def __init__(self):
        self.train_steps = []
        self.train_losses = []
        self.eval_steps = []
        self.eval_losses = []

    def on_log(self, args, state, control, logs=None, **kwargs):
        logs = logs or {}
        if "loss" in logs and "eval_loss" not in logs:
            self.train_steps.append(state.global_step)
            self.train_losses.append(logs["loss"])
        if "eval_loss" in logs:
            self.eval_steps.append(state.global_step)
            self.eval_losses.append(logs["eval_loss"])


def parse_args():
    parser = argparse.ArgumentParser(description="Qwen3 LoRA intent training")
    parser.add_argument("--model-path", default="Qwen3-0.6B")
    parser.add_argument("--data-path", default="data/train_data.json")
    parser.add_argument("--output-dir", default="model_output/lora_qwen_0.6b_intent")
    parser.add_argument("--picture-dir", default="model_output/train_results_qwen_0.6b")
    parser.add_argument("--epochs", type=float, default=3.0)
    parser.add_argument("--batch-size", type=int, default=1)
    parser.add_argument("--gradient-accumulation-steps", type=int, default=8)
    parser.add_argument("--learning-rate", type=float, default=1e-4)
    parser.add_argument("--max-length", type=int, default=768)
    parser.add_argument("--max-samples", type=int, default=0,
                        help="0 means use the complete training set")
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--save-steps", type=int, default=20)
    parser.add_argument("--logging-steps", type=int, default=5)
    return parser.parse_args()


def format_instruction(sample):
    return {"text": f"""<|im_start|>system
你是一个专业的群聊意图识别助手，请根据群聊对话历史识别核心意图。<|im_end|>
<|im_start|>user
{sample['instruction']}

对话内容：
{sample['input']}<|im_end|>
<|im_start|>assistant
{sample['output']}<|im_end|>"""}


def plot_loss(recorder, output_dir):
    os.makedirs(output_dir, exist_ok=True)
    plt.figure(figsize=(9, 5.5))
    if recorder.train_steps:
        plt.plot(recorder.train_steps, recorder.train_losses, label="Train Loss", marker="o", markersize=3)
    if recorder.eval_steps:
        plt.plot(recorder.eval_steps, recorder.eval_losses, label="Validation Loss", marker="s", markersize=4)
    plt.xlabel("Step")
    plt.ylabel("Loss")
    plt.title("Qwen3 LoRA Training and Validation Loss")
    plt.grid(alpha=0.3)
    plt.legend()
    plt.tight_layout()
    path = os.path.join(output_dir, "loss_curve.png")
    plt.savefig(path, dpi=180)
    plt.close()
    return path


def main():
    args = parse_args()
    random.seed(args.seed)
    np.random.seed(args.seed)
    torch.manual_seed(args.seed)

    with open(args.data_path, "r", encoding="utf-8") as handle:
        raw_data = json.load(handle)
    if args.max_samples > 0:
        raw_data = raw_data[:args.max_samples]
    if len(raw_data) < 10:
        raise ValueError("At least 10 samples are required for a train/validation split")

    dataset = Dataset.from_list(raw_data).map(format_instruction)
    split = dataset.train_test_split(test_size=0.1, seed=args.seed)

    tokenizer = AutoTokenizer.from_pretrained(args.model_path, trust_remote_code=True, padding_side="right")
    if tokenizer.pad_token is None:
        tokenizer.pad_token = tokenizer.eos_token

    model = AutoModelForCausalLM.from_pretrained(
        args.model_path,
        dtype=torch.bfloat16 if torch.cuda.is_available() else torch.float32,
        device_map="auto" if torch.cuda.is_available() else None,
        trust_remote_code=True,
    )
    model.config.use_cache = False
    model.gradient_checkpointing_enable()
    model = get_peft_model(model, LoraConfig(
        task_type=TaskType.CAUSAL_LM,
        r=8,
        lora_alpha=32,
        lora_dropout=0.1,
        target_modules=["q_proj", "k_proj", "v_proj", "o_proj", "gate_proj", "up_proj", "down_proj"],
        bias="none",
    ))
    model.print_trainable_parameters()

    def tokenize(examples):
        result = tokenizer(examples["text"], truncation=True, max_length=args.max_length, padding=False)
        result["labels"] = result["input_ids"].copy()
        return result

    train_set = split["train"].map(tokenize, remove_columns=split["train"].column_names)
    eval_set = split["test"].map(tokenize, remove_columns=split["test"].column_names)
    os.makedirs(args.output_dir, exist_ok=True)
    recorder = LossRecorderCallback()
    training_args = TrainingArguments(
        output_dir=args.output_dir,
        num_train_epochs=args.epochs,
        per_device_train_batch_size=args.batch_size,
        per_device_eval_batch_size=1,
        gradient_accumulation_steps=args.gradient_accumulation_steps,
        learning_rate=args.learning_rate,
        warmup_ratio=0.05,
        weight_decay=1e-4,
        logging_steps=args.logging_steps,
        save_steps=args.save_steps,
        eval_strategy="steps",
        eval_steps=args.save_steps,
        save_total_limit=2,
        load_best_model_at_end=True,
        metric_for_best_model="eval_loss",
        greater_is_better=False,
        bf16=torch.cuda.is_available(),
        gradient_checkpointing=True,
        report_to="none",
        dataloader_num_workers=0,
        remove_unused_columns=False,
        seed=args.seed,
    )
    trainer = Trainer(
        model=model,
        args=training_args,
        train_dataset=train_set,
        eval_dataset=eval_set,
        data_collator=DataCollatorForSeq2Seq(tokenizer=tokenizer, model=model, padding=True, label_pad_token_id=-100),
        callbacks=[EarlyStoppingCallback(early_stopping_patience=2), recorder],
    )
    print(f"device={'cuda' if torch.cuda.is_available() else 'cpu'} samples={len(raw_data)} train={len(train_set)} valid={len(eval_set)}")
    trainer.train()
    adapter_dir = os.path.join(args.output_dir, "lora_adapter")
    model.save_pretrained(adapter_dir)
    tokenizer.save_pretrained(adapter_dir)
    curve_path = plot_loss(recorder, args.picture_dir)
    summary = {
        "base_model": os.path.abspath(args.model_path),
        "adapter_dir": os.path.abspath(adapter_dir),
        "samples": len(raw_data),
        "train_samples": len(train_set),
        "validation_samples": len(eval_set),
        "epochs": args.epochs,
        "max_length": args.max_length,
        "final_train_loss": recorder.train_losses[-1] if recorder.train_losses else None,
        "final_eval_loss": recorder.eval_losses[-1] if recorder.eval_losses else None,
        "loss_curve": os.path.abspath(curve_path),
    }
    with open(os.path.join(args.output_dir, "training_summary.json"), "w", encoding="utf-8") as handle:
        json.dump(summary, handle, ensure_ascii=False, indent=2)
    print("TRAINING_COMPLETE")
    print(json.dumps(summary, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
