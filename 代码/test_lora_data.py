import unittest

from lora_data import normalized_input, split_training_rows, encode_completion


def sample(text, intent="吃饭", action="继续行动"):
    import json
    return {"input": text, "instruction": "legacy",
            "output": json.dumps({"intent_label": intent, "requirement_label": "清淡",
                                  "action_status": action}, ensure_ascii=False)}


class CharacterTokenizer:
    def __call__(self, text, **kwargs):
        return {"input_ids": [ord(c) for c in text]}


class DataContractTests(unittest.TestCase):
    def test_holdout_and_whitespace_duplicates_never_train(self):
        rows = [sample("重复 对话"), sample("a"), sample("b"), sample("c"), sample("d")]
        train, valid, manifest = split_training_rows(rows, [{"input": "重复\n对话"}], 42, .25)
        self.assertNotIn(normalized_input("重复对话"),
                         {normalized_input(r["input"]) for r in train + valid})
        self.assertEqual(manifest["excluded_test_overlap_rows"], [0])
        self.assertFalse({r["input"] for r in train} & {r["input"] for r in valid})

    def test_split_membership_is_stable_when_input_order_changes(self):
        rows = [sample(str(i)) for i in range(20)]
        first = split_training_rows(rows, [], 42, .2)
        second = split_training_rows(list(reversed(rows)), [], 42, .2)
        self.assertEqual({r["input"] for r in first[1]}, {r["input"] for r in second[1]})

    def test_completion_mask_and_answer_terminator(self):
        encoded = encode_completion(sample("晚上吃饭"), CharacterTokenizer(), 2000)
        ids, labels = encoded["input_ids"], encoded["labels"]
        first_target = next(i for i, value in enumerate(labels) if value != -100)
        self.assertTrue(all(value == -100 for value in labels[:first_target]))
        self.assertEqual(labels[first_target:], ids[first_target:])
        target = "".join(map(chr, labels[first_target:]))
        self.assertTrue(target.startswith('{"intent_label"'))
        self.assertTrue(target.endswith("<|im_end|>"))

    def test_overlong_answer_is_rejected_instead_of_truncated(self):
        with self.assertRaises(ValueError):
            encode_completion(sample("过长"), CharacterTokenizer(), 10)


if __name__ == "__main__":
    unittest.main()
