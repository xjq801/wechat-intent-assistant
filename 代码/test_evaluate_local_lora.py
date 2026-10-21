"""Hand-calculated metric fixtures; imports no ML framework and uses no GPU."""
import unittest

from evaluate_local_lora import PARSE_FAILURE, compute_metrics, record_response


def target(intent, action, requirement):
    return {"intent_label": intent, "action_status": action, "requirement_label": requirement}


class EvaluationMetricTests(unittest.TestCase):
    def test_hand_calculated_confusion_includes_invalid_output(self):
        gold = [
            target("吃饭", "继续行动", "清淡"),
            target("出行", "取消行动", "杭州"),
            target("无意图", "无", "无"),
            target("吃饭", "取消行动", "火锅"),
        ]
        predicted = [
            gold[0], target("吃饭", "继续行动", "朋友聚餐"),
            None, target("吃饭", "取消行动", "少辣"),
        ]
        records = [
            {"index": i, "expected": expected, "predicted": guess}
            for i, (expected, guess) in enumerate(zip(gold, predicted))
        ]
        metrics = compute_metrics(records)
        self.assertEqual(metrics["n"], 4)
        self.assertEqual(metrics["json_valid_rate"], 3 / 4)
        self.assertEqual(metrics["intent"]["accuracy"], 2 / 4)
        self.assertEqual(metrics["action"]["accuracy"], 2 / 4)
        self.assertAlmostEqual(metrics["intent"]["macro_f1"], 4 / 15)
        # Continue: P=.5,R=1,F1=2/3; cancel: P=1,R=.5,F1=2/3; none: F1=0.
        self.assertAlmostEqual(metrics["action"]["macro_f1"], 4 / 9)
        self.assertEqual(metrics["requirement_exact_match_rate"], 1 / 4)
        self.assertEqual(metrics["all_fields_exact_match_rate"], 1 / 4)
        self.assertEqual(metrics["intent"]["confusion"]["无意图"][PARSE_FAILURE], 1)
        self.assertEqual(metrics["action"]["confusion"]["无"][PARSE_FAILURE], 1)

    def test_all_150_rows_remain_in_denominator_with_50_failures(self):
        labels = [
            target("吃饭", "继续行动", "清淡"),
            target("出行", "取消行动", "杭州"),
            target("无意图", "无", "无"),
        ]
        records = [
            {"index": i, "expected": labels[i % 3],
             "predicted": None if i % 3 == 2 else labels[i % 3]}
            for i in range(150)
        ]
        metrics = compute_metrics(records)
        self.assertEqual(metrics["n"], 150)
        self.assertEqual(metrics["invalid_predictions"], 50)
        self.assertEqual(metrics["json_valid_rate"], 100 / 150)
        self.assertEqual(metrics["intent"]["accuracy"], 100 / 150)
        self.assertEqual(metrics["action"]["accuracy"], 100 / 150)
        self.assertEqual(metrics["requirement_exact_match_rate"], 100 / 150)
        self.assertEqual(metrics["intent"]["macro_f1"], 2 / 3)
        for task in ("intent", "action"):
            self.assertEqual(metrics[task]["n"], 150)
            self.assertEqual(sum(sum(row.values()) for row in metrics[task]["confusion"].values()), 150)

    def test_malformed_response_keeps_raw_text_and_error(self):
        record = record_response(0, "测试输入", "not valid json")
        self.assertIsNone(record["predicted"])
        self.assertEqual(record["raw_response"], "not valid json")
        self.assertTrue(record["error"])
        self.assertEqual(len(record["input_hash"]), 64)

    def test_missing_or_repeated_indices_are_rejected(self):
        gold = target("无意图", "无", "无")
        with self.assertRaises(ValueError):
            compute_metrics([{"index": 1, "expected": gold, "predicted": gold}])
        with self.assertRaises(ValueError):
            compute_metrics([
                {"index": 0, "expected": gold, "predicted": gold},
                {"index": 0, "expected": gold, "predicted": None},
            ])


if __name__ == "__main__":
    unittest.main()
