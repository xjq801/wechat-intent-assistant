import json
import unittest
from unittest.mock import patch

import assistant_features as features
from tools.qwen_client import QwenClientError


class OfflineFeatureTestCase(unittest.TestCase):
    def setUp(self):
        # Feature-level request budgets must never make tests consult the user's
        # local encrypted credential store.
        self.settings = {
            "api_key": "test-only-key", "model": "test-qwen-model",
            "base_url": "https://example.invalid/compatible-mode/v1",
            "timeout": 45, "max_tokens": 768,
        }
        patcher = patch.object(features, "Qwen_API_KEY", return_value=self.settings)
        patcher.start()
        self.addCleanup(patcher.stop)


class ExtractionTests(OfflineFeatureTestCase):
    def extract(self, dialogue, intent="出行", previous=None):
        return features.extract_fields(dialogue, intent, previous, use_cloud=False)

    def test_full_travel_retains_time_and_budget(self):
        result = self.extract("小明：明天上午9点从北京到上海，坐高铁，2个人，预算600元以内。")
        fields = result["fields"]
        self.assertEqual(fields["origin"], "北京")
        self.assertEqual(fields["destination"], "上海")
        self.assertEqual(fields["time"], "明天上午9点")
        self.assertEqual(fields["people"], "2个人")
        self.assertEqual(fields["transport"], "高铁")
        self.assertEqual(fields["budget"], "预算600元以内")
        self.assertEqual(features.missing_fields("出行", fields), [])

    def test_full_dining(self):
        result = self.extract("小明：明天晚上在北京五道口吃川菜，3个人，人均200元，不吃海鲜。", "吃饭")
        fields = result["fields"]
        self.assertEqual(fields["location"], "北京五道口")
        self.assertEqual(fields["people"], "3个人")
        self.assertEqual(fields["cuisine"], "川菜")
        self.assertEqual(fields["diet"], "不吃海鲜")
        self.assertEqual(fields["budget"], "人均200元")
        self.assertFalse(features.missing_fields("吃饭", fields))

    def test_missing_never_gets_invented_defaults(self):
        fields = self.extract("小明：想出去玩")["fields"]
        self.assertEqual(features.missing_fields("出行", fields), ["origin", "destination", "time"])
        self.assertEqual(fields["budget"], "")
        self.assertIn("预算：未提供", features.summarize_fields("出行", fields))
        self.assertIn("忌口/饮食要求：未提供", features.summarize_fields("吃饭", fields))

    def test_destination_without_origin_remains_partial(self):
        fields = self.extract("小明：明天去上海，预算在600以内")["fields"]
        self.assertEqual(fields["destination"], "上海")
        self.assertEqual(fields["origin"], "")
        self.assertIn("600", fields["budget"])

    def test_latest_explicit_edits(self):
        fields = self.extract(
            "小明：明天从北京到上海，预算500元，坐飞机\n"
            "小明：预算改成300元，换成后天，还是坐高铁"
        )["fields"]
        self.assertEqual(fields["time"], "后天")
        self.assertEqual(fields["transport"], "高铁")
        self.assertIn("300元", fields["budget"])
        self.assertEqual(fields["destination"], "上海")

    def test_nonadjacent_date_and_chinese_clock_are_preserved(self):
        fields = self.extract("小明：下周五从广州去深圳，早上八点出发，两个人，预算每人300")["fields"]
        self.assertEqual(fields["time"], "下周五 早上八点")
        self.assertEqual(fields["origin"], "广州")

    def test_transport_between_places_is_not_part_of_origin(self):
        fields = self.extract("小明：我想明天从北京坐高铁到上海，预算600元")["fields"]
        self.assertEqual(fields["origin"], "北京")
        self.assertEqual(fields["destination"], "上海")

    def test_clock_only_correction_preserves_existing_date(self):
        fields = self.extract("小明：改成晚上八点", previous={"time": "明天下午三点"})["fields"]
        self.assertEqual(fields["time"], "明天 晚上八点")

    def test_later_amount_correction_in_same_message(self):
        fields = self.extract("小明：预算300元不够，改成500元")["fields"]
        self.assertIn("500元", fields["budget"])
        self.assertNotIn("300元", fields["budget"])

    def test_previous_survives_incremental_change(self):
        fields = self.extract("小明：预算改成300", previous={"origin": "北京", "destination": "上海", "time": "明天"})["fields"]
        self.assertEqual(fields["destination"], "上海")
        self.assertIn("300", fields["budget"])

    def test_explicit_budget_unlimited_replaces_prior_limit(self):
        result = self.extract("小明：预算不限", previous={"budget": "300元"})
        self.assertEqual(result["fields"]["budget"], "预算不限")
        self.assertEqual(result["touched_fields"], ["budget"])

    def test_explicit_no_diet_replaces_prior_restriction(self):
        result = self.extract("小明：没有忌口了", "吃饭", {"diet": "不吃辣"})
        self.assertEqual(result["fields"]["diet"], "没有忌口了")
        self.assertNotIn("diet", result["mergeable_fields"])

    def test_new_restriction_replaces_prior_no_diet_statement(self):
        result = self.extract("小明：不吃辣", "吃饭", {"diet": "没有忌口"})
        self.assertEqual(result["fields"]["diet"], "不吃辣")

    def test_metadata_tracks_actual_speaker_and_only_touched_fields(self):
        result = self.extract("甲：从北京出发\n乙：预算500元", previous={"time": "明天"})
        self.assertEqual(result["field_speakers"]["origin"], "甲")
        self.assertEqual(result["field_speakers"]["budget"], "乙")
        self.assertNotIn("time", result["touched_fields"])

    def test_same_value_consensus_is_reported_for_controller(self):
        result = self.extract("小明：大家同意预算300元", previous={"budget": "预算300元"})
        self.assertIn("budget", result["resolved_fields"])
        self.assertIn("budget", result["touched_fields"])

    def test_consensus_is_scoped_to_its_field(self):
        result = self.extract("小明：统一时间明天，预算600元", previous={"budget": "300元"})
        self.assertIn("time", result["resolved_fields"])
        self.assertNotIn("budget", result["resolved_fields"])

    def test_compatible_diet_addition_is_mergeable_across_batches(self):
        result = self.extract("小红：不吃辣", "吃饭", {"diet": "不吃海鲜"})
        self.assertIn("diet", result["mergeable_fields"])
        self.assertEqual(result["fields"]["diet"], "不吃海鲜、不吃辣")

    def test_multi_person_budget_disagreement(self):
        result = self.extract("小明：预算300元\n小红：预算600元")
        self.assertEqual(result["conflicts"][0]["field"], "budget")
        self.assertEqual(len(result["conflicts"][0]["values"]), 2)

    def test_contained_digits_are_different_budgets(self):
        result = self.extract("小明：预算300元\n小红：预算1300元")
        self.assertEqual(result["conflicts"][0]["field"], "budget")

    def test_equivalent_budget_wording_does_not_raise_conflict(self):
        result = self.extract("小明：预算300元\n小红：预算300")
        self.assertEqual(result["conflicts"], [])

    def test_explicit_consensus_clears_conflict(self):
        result = self.extract("小明：预算300元\n小红：预算600元\n小明：统一预算改为500元")
        self.assertEqual(result["conflicts"], [])
        self.assertIn("500元", result["fields"]["budget"])

    def test_unrelated_edit_does_not_suppress_cuisine_conflict(self):
        result = self.extract("小明：想吃川菜\n小红：想吃粤菜，时间改成明天", "吃饭")
        self.assertTrue(any(item["field"] == "cuisine" for item in result["conflicts"]))

    def test_negative_cuisine_clears_prior_preference(self):
        result = self.extract("小明：别吃川菜", "吃饭", {"cuisine": "川菜"})
        self.assertEqual(result["fields"]["cuisine"], "")
        self.assertIn("别吃川菜", result["fields"]["diet"])

    def test_other_person_diet_conflict_is_visible(self):
        result = self.extract("小明：想吃川菜\n小红：我不能吃辣", "吃饭")
        self.assertTrue(any(item["field"] == "cuisine" for item in result["conflicts"]))

    def test_allergy_conflict_blocks_even_single_person_contradiction(self):
        result = self.extract("小明：想吃海鲜，我海鲜过敏", "吃饭")
        self.assertTrue(any(item["field"] == "cuisine" for item in result["conflicts"]))

    def test_allergy_alone_is_not_a_cuisine_preference(self):
        result = self.extract("小明：海鲜过敏", "吃饭")
        self.assertEqual(result["fields"]["cuisine"], "")
        self.assertEqual(result["conflicts"], [])
        self.assertEqual(result["fields"]["diet"], "海鲜过敏")

    def test_negated_dining_location_is_not_a_selection(self):
        result = self.extract("小明：别去望京吃饭", "吃饭")
        self.assertEqual(result["fields"]["location"], "")

    def test_multiple_non_conflicting_restrictions_accumulate(self):
        result = self.extract("小明：不吃海鲜\n小红：不吃辣", "吃饭")
        self.assertFalse(result["conflicts"])
        self.assertIn("不吃海鲜", result["fields"]["diet"])
        self.assertIn("不吃辣", result["fields"]["diet"])

    def test_cannot_eat_spicy_and_light_are_compatible(self):
        result = self.extract("小明：不能吃辣\n小红：清淡", "吃饭")
        self.assertEqual(result["conflicts"], [])

    def test_negated_transport_is_not_positive(self):
        fields = self.extract("小明：不要坐飞机，坐高铁")["fields"]
        self.assertEqual(fields["transport"], "高铁")

    def test_labels_are_not_speaker_names(self):
        fields = self.extract("出发地：北京\n目的地：上海\n时间：明天")["fields"]
        self.assertEqual(fields["origin"], "北京")
        self.assertEqual(fields["destination"], "上海")

    @patch.object(features, "qwen_chat_completion")
    def test_cloud_requires_source_evidence(self, client):
        client.return_value = json.dumps({"updates": [
            {"record": 0, "field": "destination", "value": "巴黎", "evidence": "去巴黎"},
            {"record": 0, "field": "budget", "value": "100元", "evidence": "成都"},
            {"record": 0, "field": "destination", "value": "成都", "evidence": "目的城市想选成都"},
        ]}, ensure_ascii=False)
        result = features.extract_fields("小明：目的城市想选成都", "出行")
        self.assertEqual(result["fields"]["destination"], "成都")
        self.assertEqual(result["fields"]["budget"], "")
        self.assertNotIn("巴黎", str(result))

    @patch.object(features, "qwen_chat_completion")
    def test_cloud_cannot_convert_negation_to_preference(self, client):
        client.return_value = json.dumps({"updates": [
            {"record": 0, "field": "destination", "value": "上海", "evidence": "上海"},
        ]}, ensure_ascii=False)
        result = features.extract_fields("小明：不想去上海", "出行")
        self.assertEqual(result["fields"]["destination"], "")

    @patch.object(features, "qwen_chat_completion", side_effect=QwenClientError("QWEN_TIMEOUT"))
    def test_cloud_failure_preserves_local_values(self, client):
        result = features.extract_fields("明天从北京到上海，预算500元", "出行")
        self.assertEqual(result["fields"]["origin"], "北京")
        self.assertIn("500元", result["fields"]["budget"])
        self.assertTrue(result["warning"])

    @patch.object(features, "qwen_chat_completion")
    def test_offline_never_calls_client(self, client):
        self.extract("明天从北京到上海")
        client.assert_not_called()


class ProposalTests(OfflineFeatureTestCase):
    fields = {"origin": "北京", "destination": "上海", "time": "明天", "budget": "600元", "transport": "高铁"}

    @patch.object(features, "qwen_chat_completion", side_effect=QwenClientError("QWEN_NOT_CONFIGURED"))
    def test_offline_comparison_has_honest_budget_and_sources(self, client):
        result = features.generate_proposals("出行", self.fields)
        self.assertEqual(len(result["proposals"]), 3)
        self.assertEqual(len({item["title"] for item in result["proposals"]}), 3)
        self.assertEqual(result["source"], "本地通用规划")
        for item in result["proposals"]:
            self.assertIn("非实时", item["description"])
            self.assertIn("尚未查询", item["budget_fit"])
            self.assertIn("12306", item["verify"])
            self.assertTrue(item["tradeoff"])

    @patch.object(features, "qwen_chat_completion")
    def test_missing_required_fields_prevents_generation(self, client):
        with self.assertRaises(ValueError):
            features.generate_proposals("出行", {"origin": "北京"})
        client.assert_not_called()

    def sample(self, description="优先比较符合时段的直达方案。"):
        return {"proposals": [
            {"title": title, "description": description, "reason": "符合需求",
             "budget_fit": "声称符合预算", "tradeoff": "可选范围较小", "verify": "需要确认"}
            for title in ("时间优先", "预算优先")
        ]}

    @patch.object(features, "qwen_chat_completion")
    def test_valid_cloud_schema_gets_authoritative_budget_warning(self, client):
        client.return_value = json.dumps(self.sample(), ensure_ascii=False)
        result = features.generate_proposals("出行", self.fields)
        self.assertIn("千问", result["source"])
        self.assertEqual(len(result["proposals"]), 2)
        self.assertNotIn("声称符合预算", str(result))
        self.assertIn("待核验", result["proposals"][0]["budget_fit"])

    @patch.object(features, "qwen_chat_completion")
    def test_unsupported_ticket_prices_force_fallback(self, client):
        client.return_value = json.dumps(self.sample("G123次有票，票价550元。"), ensure_ascii=False)
        result = features.generate_proposals("出行", self.fields)
        self.assertEqual(result["source"], "本地通用规划")
        self.assertNotIn("G123", str(result))
        self.assertNotIn("550元", str(result))

    @patch.object(features, "qwen_chat_completion")
    def test_live_synthetic_proposal_replay_preserves_explicit_user_budget_limit(self, client):
        # Replay the real response from an isolated test, with synthetic inputs.
        payload = {"proposals": [
            {"title": "优先满足时间",
             "description": "筛选三里屯附近支持明晚七点即时预订的川菜馆，避开排队高峰。",
             "reason": "确保三人按时入座，避免等待影响用餐体验。",
             "budget_fit": "价格待核验", "tradeoff": "可能牺牲部分热门餐厅的选择范围。",
             "verify": "实际信息待核验"},
            {"title": "优先满足口味",
             "description": "聚焦评分高、正宗川味且明确标注无花生选项的餐厅，提前确认菜品。",
             "reason": "最大化满足对川菜风味及饮食限制的高要求。",
             "budget_fit": "价格待核验", "tradeoff": "需提前较长时间沟通或接受稍远位置。",
             "verify": "实际信息待核验"},
            {"title": "优先控制预算",
             "description": "筛选人均严格控制在120元内且符合无花生要求的性价比川菜店。",
             "reason": "在满足基本饮食限制前提下，最优化成本效益。",
             "budget_fit": "价格待核验", "tradeoff": "环境或服务标准可能略低于高端选择。",
             "verify": "实际信息待核验"},
        ]}
        client.return_value = json.dumps(payload, ensure_ascii=False)
        fields = {"location": "三里屯", "time": "明晚七点", "people": "三人", "cuisine": "川菜",
                  "diet": "花生过敏", "budget": "人均120元以内"}
        result = features.generate_proposals("吃饭", fields)
        self.assertIn("千问", result["source"])
        self.assertEqual(len(result["proposals"]), 3)
        self.assertIn("控制在120元内", result["proposals"][2]["description"])
        self.assertIn("是否符合预算待核验", result["proposals"][2]["budget_fit"])

    def test_same_budget_actual_quoted_price_still_rejected(self):
        fields = features._valid_fields({**self.fields, "budget": "120元以内"})
        for description in ("餐厅人均120元。", "票价120元。", "实际价格为120元。",
                            "人均120元，预算控制在120元以内。"):
            with self.subTest(description=description), self.assertRaises(ValueError):
                features._validate_proposals(self.sample(description), "出行", fields)

    def test_unconfirmed_amount_cannot_be_disguised_as_budget_bound(self):
        fields = features._valid_fields({**self.fields, "budget": "120元以内"})
        for description in ("控制在150元内。", "预算130元以内。", "不超过1200元以内。",
                            "控制在120元内，接驳费30元。"):
            with self.subTest(description=description), self.assertRaises(ValueError):
                features._validate_proposals(self.sample(description), "出行", fields)

    def test_bare_budget_amount_without_explicit_bound_still_rejected(self):
        fields = features._valid_fields({**self.fields, "budget": "120元以内"})
        with self.assertRaises(ValueError):
            features._validate_proposals(self.sample("价格120元，符合预算。"), "出行", fields)

    @patch.object(features, "qwen_chat_completion")
    def test_two_valid_and_one_unverified_price_keep_only_two_cloud_options(self, client):
        payload = self.sample()
        bad = dict(payload["proposals"][0], title="具体票价", description="票价120元，有票。")
        payload["proposals"].insert(1, bad)
        client.return_value = json.dumps(payload, ensure_ascii=False)
        result = features.generate_proposals("出行", self.fields)
        self.assertIn("千问", result["source"])
        self.assertEqual(len(result["proposals"]), 2)
        self.assertIn("已过滤 1 个", result["warning"])
        self.assertNotIn("120元", str(result["proposals"]))
        self.assertEqual([p["id"] for p in result["proposals"]], ["plan-1", "plan-2"])

    @patch.object(features, "qwen_chat_completion")
    def test_only_one_valid_cloud_option_falls_back(self, client):
        payload = self.sample()
        payload["proposals"][1]["description"] = "票价120元。"
        client.return_value = json.dumps(payload, ensure_ascii=False)
        result = features.generate_proposals("出行", self.fields)
        self.assertEqual(result["source"], "本地通用规划")
        self.assertEqual(len(result["proposals"]), 3)

    @patch.object(features, "qwen_chat_completion")
    def test_duplicate_is_filtered_when_two_distinct_options_remain(self, client):
        payload = self.sample()
        payload["proposals"].append(dict(payload["proposals"][0]))
        client.return_value = json.dumps(payload, ensure_ascii=False)
        result = features.generate_proposals("出行", self.fields)
        self.assertIn("千问", result["source"])
        self.assertEqual(len(result["proposals"]), 2)
        self.assertIn("重复", result["warning"])

    @patch.object(features, "qwen_chat_completion")
    def test_duplicates_cannot_supply_the_second_required_option(self, client):
        proposal = self.sample()["proposals"][0]
        client.return_value = json.dumps({"proposals": [proposal, dict(proposal)]}, ensure_ascii=False)
        result = features.generate_proposals("出行", self.fields)
        self.assertEqual(result["source"], "本地通用规划")

    @patch.object(features, "qwen_chat_completion")
    def test_second_live_response_replay_filters_wrong_budget_arithmetic_only(self, client):
        client.return_value = json.dumps({"proposals": [
            {"title": "优先满足口味",
             "description": "筛选三里屯区域内评分高、主打正宗川菜的餐厅，重点确认无花生菜品或可定制去花生选项。",
             "reason": "用户明确指定川菜且有人不吃花生，此方案确保核心饮食需求被精准匹配。",
             "budget_fit": "价格待核验", "tradeoff": "可能因热门导致排队时间长，需提前规划到达时间。",
             "verify": "实际信息待核验"},
            {"title": "优先满足预算",
             "description": "在120元总预算约束下（人均40元），寻找三里屯周边性价比高的快餐式或平价川菜馆。",
             "reason": "严格贴合不超过120元的硬性预算限制，避免超支风险。",
             "budget_fit": "价格待核验", "tradeoff": "环境与服务可能较基础，选择范围受限于低价区间。",
             "verify": "实际信息待核验"},
            {"title": "平衡体验与合规",
             "description": "结合明晚七点高峰时段，选择支持快速出餐或可预约的中等价位川菜，兼顾口味与预算。",
             "reason": "在时间、地点、预算和特殊饮食限制之间寻求最优解，减少不确定性。",
             "budget_fit": "价格待核验", "tradeoff": "可能需要牺牲部分极致口味或环境舒适度以换取效率。",
             "verify": "实际信息待核验"},
        ]}, ensure_ascii=False)
        result = features.generate_proposals("吃饭", {
            "location": "三里屯", "time": "明晚七点", "people": "三人",
            "budget": "人均120元以内", "diet": "不吃花生", "cuisine": "川菜",
        })
        self.assertIn("千问", result["source"])
        self.assertEqual([p["title"] for p in result["proposals"]], ["优先满足口味", "平衡体验与合规"])
        self.assertIn("已过滤 1 个", result["warning"])
        self.assertNotIn("人均40元", str(result))

    @patch.object(features, "qwen_chat_completion", return_value='{"proposals":[]}')
    def test_invalid_schema_falls_back(self, client):
        result = features.generate_proposals("出行", self.fields)
        self.assertEqual(len(result["proposals"]), 3)
        self.assertTrue(result["warning"])

    @patch.object(features, "qwen_chat_completion", side_effect=QwenClientError("QWEN_TIMEOUT"))
    def test_dining_fallback_retains_restrictions(self, client):
        result = features.generate_proposals("吃饭", {"location": "望京", "time": "明晚", "people": "3个人", "diet": "花生过敏"})
        for item in result["proposals"]:
            self.assertIn("花生过敏", item["description"])
            self.assertIn("预算未提供", item["budget_fit"])


class QuestionTests(OfflineFeatureTestCase):
    @patch.object(features, "qwen_chat_completion", return_value="可先比较直达与换乘方案。")
    def test_explicit_question_has_bounded_structured_context(self, client):
        context = {"task": {"intent": "出行", "fields": {"origin": "北京", "destination": "上海"},
                            "history": "不该发送的旧聊天"}}
        answer = features.answer_question("怎么选更方便？", context)
        self.assertIn("直达", answer["text"])
        sent = client.call_args.args[0]
        self.assertNotIn("不该发送的旧聊天", str(sent))
        self.assertIn("北京", str(sent))
        self.assertIn("未查询实时", answer["warning"])

    @patch.object(features, "qwen_chat_completion", side_effect=QwenClientError("QWEN_NOT_CONFIGURED"))
    def test_no_configuration_does_not_fake_answer(self, client):
        with self.assertRaises(QwenClientError):
            features.answer_question("有什么建议？")

    @patch.object(features, "qwen_chat_completion")
    def test_empty_question_rejected_without_call(self, client):
        with self.assertRaises(ValueError):
            features.answer_question(" ")
        client.assert_not_called()


class FeatureRequestBudgetTests(OfflineFeatureTestCase):
    def test_feature_limits_copy_settings_without_mutating_defaults(self):
        for name, expected in (("extract", 1200), ("proposals", 1600), ("question", 768)):
            config = features._feature_config(name)
            self.assertEqual(config["max_tokens"], expected)
            self.assertEqual(config["timeout"], 45)
            self.assertEqual(config["model"], "test-qwen-model")
            self.assertIsNot(config, self.settings)
        self.assertEqual(self.settings["max_tokens"], 768)

    @patch.object(features, "qwen_chat_completion", return_value='{"updates":[]}')
    def test_extraction_uses_bounded_larger_output_without_retry(self, client):
        features.extract_fields("小明：明天从北京到上海", "出行")
        client.assert_called_once()
        self.assertEqual(client.call_args.kwargs["config"]["max_tokens"], 1200)

    @patch.object(features, "qwen_chat_completion", return_value='{"proposals":[]}')
    def test_proposals_allow_structured_response_without_retrying_invalid_json(self, client):
        result = features.generate_proposals("出行", {"origin": "北京", "destination": "上海", "time": "明天"})
        client.assert_called_once()
        self.assertEqual(client.call_args.kwargs["config"]["max_tokens"], 1600)
        self.assertEqual(result["source"], "本地通用规划")

    @patch.object(features, "qwen_chat_completion", return_value="请先确认时间。")
    def test_question_stays_at_original_short_reply_limit(self, client):
        features.answer_question("应先确认什么？")
        client.assert_called_once()
        self.assertEqual(client.call_args.kwargs["config"]["max_tokens"], 768)


if __name__ == "__main__":
    unittest.main()
