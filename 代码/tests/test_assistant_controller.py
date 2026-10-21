"""End-to-end task transitions with deterministic timers and no cloud/model."""
from contextlib import ExitStack
import unittest
from unittest.mock import patch
import bot
from assistant_features import extract_fields
from test_bot_scheduler import FakeTimer


class AssistantControllerTests(unittest.TestCase):
    def setUp(self):
        bot.shutdown()
        self.stack = ExitStack()
        self.addCleanup(self.stack.close)
        self.addCleanup(bot.shutdown)
        self.stack.enter_context(patch.object(bot.threading, "Timer", FakeTimer))
        self.stack.enter_context(patch.object(bot, "ASSISTANT_CALLBACK"))
        self.stack.enter_context(patch.object(bot, "STATUS_CALLBACK"))
        self.reply = self.stack.enter_context(patch.object(bot, "REPLY_CALLBACK"))
        self.ask = self.stack.enter_context(patch.object(bot, "ask_f", return_value=("出行", "北京到上海", "继续行动")))
        self.stack.enter_context(patch.object(bot, "extract_fields", side_effect=lambda d, i, previous=None:
                                             extract_fields(d, i, previous, use_cloud=False)))
        self.generate = self.stack.enter_context(patch.object(bot, "generate_proposals"))
        self.answer = self.stack.enter_context(patch.object(bot, "answer_question", return_value={
            "text": "测试回答", "source": "mock", "warning": ""}))

    def state(self):
        return bot.get_assistant_state("test")

    def send(self, text="明天上午从北京坐高铁去上海，预算600元", uid="a", nick="甲", fire=True, **extra):
        bot.custom_bot_reply({"room": "test", "msg": text, "msg_type": "text",
                             "user_id": uid, "user_nick": nick, **extra})
        timer = bot._ROOMS["test"].predict_timer
        if fire and timer:
            timer.fire()
        return timer

    def action(self, action, **extra):
        state = self.state()
        return bot.handle_assistant_action("test", "a", "甲", {
            "action": action, "version": state["version"],
            "task_id": (state["task"] or {}).get("id"), **extra})

    def test_required_details_and_time_budget_survive_and_no_auto_recommendation(self):
        self.send()
        task = self.state()["task"]
        self.assertEqual(task["status"], "ready")
        self.assertIn("明天", task["fields"]["time"])
        self.assertIn("600", task["fields"]["budget"])
        self.generate.assert_not_called()
        self.reply.assert_not_called()

    def test_partial_card_edit_and_optimistic_revision(self):
        self.send("去上海")
        self.assertFalse(self.action("confirm")["ok"])
        old = self.state()
        self.assertTrue(self.action("edit", fields={"origin": "北京", "time": "明天"})["ok"])
        self.assertEqual(self.state()["task"]["fields"]["destination"], "上海")
        self.assertEqual(self.state()["task"]["status"], "ready")
        stale = bot.handle_assistant_action("test", "a", "甲", {
            "action": "edit", "version": old["version"], "task_id": old["task"]["id"],
            "fields": {"budget": "900元"}})
        self.assertFalse(stale["ok"])
        self.assertEqual(self.state()["task"]["fields"]["budget"], "")

    def test_multi_turn_edits_keep_other_details_and_id(self):
        self.send()
        original = self.state()["task"]["id"]
        self.send("预算改成300元，换成后天上午")
        task = self.state()["task"]
        self.assertEqual(task["id"], original)
        self.assertEqual(task["fields"]["origin"], "北京")
        self.assertIn("300", task["fields"]["budget"])
        self.assertIn("后天", task["fields"]["time"])
        self.ask.assert_called_once()

    def test_group_conflict_and_same_value_consensus(self):
        self.send()
        self.send("预算300元", "b", "乙")
        self.assertTrue(self.state()["task"]["conflicts"])
        self.assertFalse(self.action("confirm")["ok"])
        self.send("统一预算600元", "b", "乙")
        self.assertEqual(self.state()["task"]["conflicts"], [])

    def test_compatible_diets_merge_across_people(self):
        self.ask.return_value = ("吃饭", "聚餐", "继续行动")
        self.send("明晚三个人在北京吃饭，不吃海鲜")
        self.send("不吃辣", "b", "乙")
        self.assertEqual(self.state()["task"]["conflicts"], [])
        diet = self.state()["task"]["fields"]["diet"]
        self.assertIn("海鲜", diet)
        self.assertIn("辣", diet)

    def test_cross_person_clear_preference_becomes_conflict(self):
        self.ask.return_value = ("吃饭", "川菜", "继续行动")
        self.send("今晚三个人在北京吃川菜")
        self.send("别吃川菜", "b", "乙")
        self.assertTrue(any(c["field"] == "cuisine" for c in self.state()["task"]["conflicts"]))
        self.action("edit", fields={"cuisine": ""})
        self.assertFalse(any(c["field"] == "cuisine" for c in self.state()["task"]["conflicts"]))

    def test_multi_person_batch_keeps_field_author(self):
        self.send("明天从北京出发", fire=False)
        self.send("去上海，坐高铁", "b", "乙")
        self.assertEqual(self.state()["task"]["field_owners"]["origin"], "a")
        self.send("出发地改成天津")
        self.assertFalse(any(c["field"] == "origin" for c in self.state()["task"]["conflicts"]))

    def test_auto_off_still_allows_explicit_chinese_mention(self):
        self.action("set_auto", auto_detect=False)
        self.assertIsNone(self.send("今天天气如何"))
        self.send("@AI帮我解释高铁换乘")
        self.ask.assert_not_called()
        self.answer.assert_called_once()
        self.assertEqual(self.answer.call_args.args[0], "帮我解释高铁换乘")
        self.reply.assert_called_once()

    def test_explicit_question_preserves_task_and_cancels_pending_recommendation(self):
        self.send()
        original = self.state()["task"]["id"]
        self.action("confirm")
        old = bot._ROOMS["test"].predict_timer
        self.send("预算应该怎么选", ask_ai=True)
        old.fire()
        self.generate.assert_not_called()
        self.assertEqual(self.state()["task"]["id"], original)
        self.assertEqual(self.state()["task"]["status"], "ready")
        self.reply.assert_called_once()

    def test_negated_cancellation_and_topic_switch(self):
        self.ask.return_value = ("吃饭", "聚餐", "继续行动")
        self.send("今晚三个人在北京吃饭")
        self.send("不要取消，继续原计划")
        self.assertNotEqual(self.state()["task"]["status"], "cancelled")
        self.ask.return_value = ("出行", "北京到上海", "继续行动")
        self.send("明天从北京坐高铁去上海")
        self.assertEqual(self.ask.call_count, 2)
        self.assertEqual(self.state()["task"]["intent"], "出行")
        self.assertFalse(bot._is_cancel("取消按钮在哪里？"))

    def test_cancel_complete_and_new_end_context(self):
        self.send()
        self.send("取消这次出行")
        self.assertEqual(self.state()["task"]["status"], "cancelled")
        self.send("后天从天津去广州")
        self.assertEqual(self.state()["task"]["fields"]["origin"], "天津")
        self.assertEqual(self.state()["task"]["fields"]["budget"], "")
        self.action("complete")
        self.assertEqual(self.state()["task"]["status"], "completed")
        self.action("new")
        self.assertIsNone(self.state()["task"])

    def test_restore_is_idempotent_and_never_resumes_cloud_job(self):
        self.send()
        self.action("confirm")
        persisted = self.state()
        bot.shutdown()
        bot.restore_assistant_state("test", persisted)
        restored = self.state()
        self.assertEqual(restored["task"]["status"], "ready")
        self.assertIsNone(bot._ROOMS["test"].predict_timer)
        bot.restore_assistant_state("test", {**persisted, "version": 0})
        self.assertEqual(self.state(), restored)

    def test_snapshot_detached_and_trace_bounded(self):
        self.send()
        snapshot = self.state()
        snapshot["task"]["fields"]["budget"] = "corruption"
        self.assertNotEqual(self.state()["task"]["fields"]["budget"], "corruption")
        for i in range(30):
            self.action("set_auto", auto_detect=i % 2 == 0)
        self.assertLessEqual(len(self.state()["trace"]), 24)

    def test_pending_chat_change_cannot_confirm_old_fields_and_neutral_restores(self):
        self.send()
        self.send("预算改成300元", fire=False)
        self.assertEqual(self.state()["task"]["status"], "draft")
        self.assertFalse(self.action("confirm")["ok"])
        bot._ROOMS["test"].predict_timer.fire()
        self.assertIn("300", self.state()["task"]["fields"]["budget"])
        self.ask.return_value = ("无意图", "无", "无")
        self.send("今天的作业交了吗")
        self.assertEqual(self.state()["task"]["status"], "ready")


if __name__ == "__main__":
    unittest.main()
