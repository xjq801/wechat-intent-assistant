"""Data boundaries shared by local LoRA training and its contract tests."""
import hashlib
import json
import random
import re
from collections import defaultdict

from intent_common import build_prompt, validate_prediction


def normalized_input(text):
    return re.sub(r"\s+", "", text)


def input_key(text):
    return hashlib.sha256(normalized_input(text).encode("utf-8")).hexdigest()


def split_training_rows(rows, heldout_rows, seed=42, validation_fraction=.1):
    """Exclude held-out inputs, then split by input group and intent/action."""
    if not 0 < validation_fraction < 1:
        raise ValueError("validation_fraction must be between zero and one")
    heldout = {input_key(row["input"]) for row in heldout_rows}
    groups = {}
    excluded = []
    duplicates = []
    for index, row in enumerate(rows):
        expected = validate_prediction(json.loads(row["output"]))
        key = input_key(row["input"])
        if key in heldout:
            excluded.append(index)
            continue
        if key in groups:
            if expected != groups[key][2]:
                raise ValueError(f"Conflicting labels for identical input: row {index}")
            duplicates.append(index)
            continue
        groups[key] = (index, row, expected)
    strata = defaultdict(list)
    for key, (_, _, target) in groups.items():
        strata[(target["intent_label"], target["action_status"])].append(key)
    train_keys, valid_keys = [], []
    rng = random.Random(seed)
    for stratum in sorted(strata):
        keys = sorted(strata[stratum])
        rng.shuffle(keys)
        n_valid = min(len(keys) - 1, max(1, round(len(keys) * validation_fraction)))
        valid_keys.extend(keys[:n_valid])
        train_keys.extend(keys[n_valid:])
    rng.shuffle(train_keys)
    rng.shuffle(valid_keys)
    assert not set(train_keys) & set(valid_keys)
    assert not (set(train_keys) | set(valid_keys)) & heldout
    manifest = {
        "seed": seed, "validation_fraction": validation_fraction,
        "normalization": "remove_all_whitespace_then_sha256",
        "excluded_test_overlap_rows": excluded,
        "excluded_duplicate_training_rows": duplicates,
        "train_indices": [groups[k][0] for k in train_keys],
        "validation_indices": [groups[k][0] for k in valid_keys],
        "train_input_hashes": train_keys, "validation_input_hashes": valid_keys,
        "heldout_input_hashes": sorted(heldout),
    }
    return ([groups[k][1] for k in train_keys],
            [groups[k][1] for k in valid_keys], manifest)


def encode_completion(row, tokenizer, max_length):
    expected = validate_prediction(json.loads(row["output"]))
    prompt = build_prompt(row["input"])
    answer = json.dumps(expected, ensure_ascii=False) + "<|im_end|>"
    prompt_ids = tokenizer(prompt, add_special_tokens=False)["input_ids"]
    answer_ids = tokenizer(answer, add_special_tokens=False)["input_ids"]
    ids = prompt_ids + answer_ids
    if len(ids) > max_length:
        raise ValueError(f"Sample needs {len(ids)} tokens, exceeds {max_length}; no silent truncation")
    return {"input_ids": ids, "attention_mask": [1] * len(ids),
            "labels": [-100] * len(prompt_ids) + answer_ids}
