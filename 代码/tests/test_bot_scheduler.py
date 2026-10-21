"""Deterministic scheduler races without timers, models, APIs or user storage."""

from contextlib import ExitStack
import unittest
from unittest.mock import patch

import bot
from assistant_features import extract_fields


class FakeTimer:
    """A cancelled Timer may already be executing; fire() deliberately allows it."""

    def __init__(self, interval, function, args=None, kwargs=None):
        self.interval = interval
        self.function = function
        self.args = () if args is None else args
        self.kwargs = {} if kwargs is None else kwargs
        self.daemon = False
        self.started = False
        self.cancelled = False
        self.fire_count = 0

    def start(self):
        self.started = True

    def cancel(self):
        self.cancelled = True

    def fire(self):
        self.fire_count += 1
        return self.function(*self.args, **self.kwargs)


class BotSchedulerTests(unittest.TestCase):
    def setUp(self):
        bot.shutdown()
        self.patches = ExitStack()
        self.addCleanup(self.patches.close)
        self.addCleanup(bot.shutdown)
        self.replies = []
        self.statuses = []
        self.snapshots = []
        self.patches.enter_context(patch.object(bot.threading, "Timer", FakeTimer))
        self.ask = self.patches.enter_context(
            patch.object(bot, "ask_f", return_value=("出行", "北京到上海", "继续行动"))
        )
        self.generate = self.patches.enter_context(
            patch.object(bot, "generate_proposals", return_value=self.proposals("模拟建议"))
        )
        self.patches.enter_context(
            patch.object(bot, "extract_fields",
                         side_effect=lambda dialogue, intent, previous=None:
                         extract_fields(dialogue, intent, previous=previous, use_cloud=False))
        )
        self.patches.enter_context(
            patch.object(bot, "REPLY_CALLBACK", side_effect=lambda *args: self.replies.append(args))
        )
        self.patches.enter_context(
            patch.object(bot, "STATUS_CALLBACK", side_effect=lambda *args: self.statuses.append(args))
        )
        self.patches.enter_context(
            patch.object(bot, "ASSISTANT_CALLBACK", side_effect=lambda *args: self.snapshots.append(args))
        )
        # Guard accidental real media/network use in future scheduler changes.
        for name in ("ask_audio", "ask_image", "ask_video", "qwen_chat_completion"):
            self.patches.enter_context(patch.object(bot, name, side_effect=AssertionError("external call")))
        self.patches.enter_context(patch(
            "assistant_features.qwen_chat_completion", side_effect=AssertionError("external call")
        ))

    @staticmethod
    def proposals(description):
        return {
            "proposals": [
                {"id": f"plan-{index}", "title": f"测试方案 {index}",
                 "description": description, "reason": "测试匹配理由",
                 "budget_fit": "价格待核验", "tradeoff": "测试取舍", "verify": "非实时建议"}
                for index in (1, 2)
            ],
            "source": "测试规划", "warning": "测试内容",
        }

    def confirm(self, room="test-room", nick="甲"):
        """Use the public edit/confirm transition before firing any worker."""
        snapshot = bot.get_assistant_state(room)
        task = snapshot["task"]
        self.assertIsNotNone(task)
        self.assertEqual(task["intent"], "出行")
        defaults = {"origin": "北京", "destination": "上海", "time": "明天上午"}
        edits = {key: defaults[key] for key in task["missing"]}
        if edits:
            edited = bot.handle_assistant_action(room, nick, nick, {
                "action": "edit", "version": snapshot["version"], "task_id": task["id"],
                "fields": edits,
            })
            self.assertTrue(edited["ok"])
            snapshot = bot.get_assistant_state(room)
            task = snapshot["task"]
        result = bot.handle_assistant_action(room, nick, nick, {
            "action": "confirm", "version": snapshot["version"], "task_id": task["id"],
        })
        self.assertTrue(result["ok"])
        self.assertEqual(bot.get_assistant_state(room)["task"]["status"], "generating")
        return bot._ROOMS[room].predict_timer

    def queue(self, text, room="test-room", nick="甲"):
        result = bot.custom_bot_reply({
            "room": room,
            "user_nick": nick,
            "msg_type": "text",
            "msg": text,
        })
        self.assertEqual(result, (False, ""))
        return bot._ROOMS[room].predict_timer

    def test_burst_only_latest_timer_runs_model_and_cloud_once_after_confirmation(self):
        first = self.queue("明天去上海")
        second = self.queue("从北京出发")
        latest = self.queue("预算六百元")
        self.assertTrue(first.cancelled)
        self.assertTrue(second.cancelled)
        self.ask.assert_not_called()
        self.generate.assert_not_called()
        # Model a timer that was cancelled after its callback had been queued.
        first.fire()
        second.fire()
        self.ask.assert_not_called()
        latest.fire()
        self.ask.assert_called_once_with("甲:明天去上海\n甲:从北京出发\n甲:预算六百元")
        self.generate.assert_not_called()
        self.assertEqual(self.replies, [])
        self.assertIn(bot.get_assistant_state("test-room")["task"]["status"], {"ready", "needs_info"})
        self.confirm().fire()
        self.generate.assert_called_once()
        intent, fields = self.generate.call_args.args
        self.assertEqual(intent, "出行")
        self.assertEqual(fields["destination"], "上海")
        self.assertIn("六百", fields["budget"])
        self.assertEqual(len(self.replies), 1)
        self.assertEqual((self.replies[0][0], self.replies[0][2]), ("test-room", "甲"))
        self.assertIn("模拟建议", self.replies[0][1])
        self.assertEqual(self.statuses[-1][1], "done")

    def test_cancellation_arrives_during_inference_old_result_never_calls_cloud(self):
        old_timer = self.queue("明天去上海")
        cancellation_timers = []

        def infer_with_new_message(dialogue):
            if "取消" not in dialogue:
                cancellation_timers.append(self.queue("取消这次出行"))
                return "出行", "北京到上海", "继续行动"
            return "出行", "北京到上海", "取消行动"

        self.ask.side_effect = infer_with_new_message
        old_timer.fire()
        self.generate.assert_not_called()
        self.assertEqual(self.replies, [])
        newest = cancellation_timers[0]
        self.assertIs(bot._ROOMS["test-room"].predict_timer, newest)
        self.assertFalse(newest.cancelled)
        newest.fire()
        self.assertEqual(self.ask.call_count, 2)
        self.generate.assert_not_called()
        self.assertEqual(self.statuses[-1][1], "cancelled")
        self.assertEqual(self.replies, [])

    def test_stop_during_cloud_generation_discards_completed_result(self):
        timer = self.queue("推荐去上海的方案")
        timer.fire()
        self.generate.assert_not_called()
        proposal_timer = self.confirm()

        def stop_before_return(*args):
            bot.cancel_pending("test-room")
            return self.proposals("已经生成但过时的建议")

        self.generate.side_effect = stop_before_return
        proposal_timer.fire()
        self.generate.assert_called_once()
        self.assertEqual(self.replies, [])
        self.assertEqual(self.statuses[-1][1], "cancelled")
        self.assertEqual(list(bot._ROOMS["test-room"].history), [])

    def test_new_message_during_cloud_generation_keeps_new_timer(self):
        first = self.queue("明天去上海")
        first.fire()
        proposal_timer = self.confirm()
        latest_timers = []

        def add_message_before_return(*args):
            latest_timers.append(self.queue("改成明天去杭州"))
            return self.proposals("过时的上海建议")

        self.generate.side_effect = add_message_before_return
        proposal_timer.fire()
        latest = latest_timers[0]
        self.assertEqual(self.replies, [])
        self.assertIs(bot._ROOMS["test-room"].predict_timer, latest)
        self.assertFalse(latest.cancelled)
        self.generate.side_effect = None
        self.generate.return_value = self.proposals("新的杭州建议")
        latest.fire()
        self.assertEqual(self.replies, [])
        self.assertEqual(self.generate.call_count, 1)
        self.assertEqual(bot.get_assistant_state("test-room")["task"]["fields"]["destination"], "杭州")
        self.confirm().fire()
        self.assertEqual(self.generate.call_count, 2)
        self.assertEqual(len(self.replies), 1)
        self.assertEqual((self.replies[0][0], self.replies[0][2]), ("test-room", "甲"))
        self.assertIn("新的杭州建议", self.replies[0][1])
        self.assertNotIn("过时的上海建议", self.replies[0][1])

    def test_stop_one_room_does_not_affect_other_room(self):
        first = self.queue("去上海", room="room-a")
        second = self.queue("去北京", room="room-b", nick="乙")
        bot.cancel_pending("room-a")
        self.assertTrue(first.cancelled)
        self.assertFalse(second.cancelled)
        first.fire()
        self.ask.assert_not_called()
        second.fire()
        self.ask.assert_called_once_with("乙:去北京")
        self.assertEqual(self.replies, [])
        self.generate.assert_not_called()
        self.confirm(room="room-b", nick="乙").fire()
        self.assertEqual(len(self.replies), 1)
        self.assertEqual((self.replies[0][0], self.replies[0][2]), ("room-b", "乙"))
        self.assertIn("模拟建议", self.replies[0][1])

    def test_stale_clear_timer_cannot_clear_new_history_or_cancel_new_timer(self):
        self.queue("去上海")
        state = bot._ROOMS["test-room"]
        stale_clear = state.clear_timer
        latest = self.queue("取消")
        current_clear = state.clear_timer
        current_token = state.token
        self.assertTrue(stale_clear.cancelled)
        stale_clear.fire()
        self.assertEqual([record["text"] for record in state.history], ["去上海", "取消"])
        self.assertIs(state.token, current_token)
        self.assertIs(state.predict_timer, latest)
        self.assertIs(state.clear_timer, current_clear)
        self.assertFalse(latest.cancelled)
        self.assertFalse(current_clear.cancelled)

    def test_current_clear_invalidates_pending_prediction(self):
        timer = self.queue("去上海")
        state = bot._ROOMS["test-room"]
        state.clear_timer.fire()
        self.assertEqual(list(state.history), [])
        self.assertTrue(timer.cancelled)
        timer.fire()
        self.ask.assert_not_called()
        self.generate.assert_not_called()
        self.assertEqual(self.replies, [])

    def test_inference_error_is_visible_and_does_not_generate_recommendation(self):
        self.ask.side_effect = ValueError("private invalid output")
        timer = self.queue("取消出行")
        timer.fire()
        self.generate.assert_not_called()
        self.assertEqual(self.replies, [])
        self.assertEqual(self.statuses[-1][1], "error")
        self.assertIn("重新描述", self.statuses[-1][2])
        self.assertNotIn("private invalid output", self.statuses[-1][2])
        self.assertIsNone(bot._ROOMS["test-room"].predict_timer)

    def test_invalid_label_combination_is_error_instead_of_recommendation(self):
        self.ask.return_value = ("无意图", "无", "取消行动")
        self.queue("取消出行").fire()
        self.generate.assert_not_called()
        self.assertEqual(self.statuses[-1][1], "error")
        self.assertEqual(self.replies, [])

    def test_valid_cancellation_is_visible_without_recommendation(self):
        self.ask.return_value = ("出行", "北京到上海", "取消行动")
        self.queue("取消这次出行").fire()
        self.generate.assert_not_called()
        self.assertEqual(self.replies, [])
        self.assertEqual(self.statuses[-1][1], "cancelled")
        self.assertIn("取消", self.statuses[-1][2])

    def test_neutral_chat_is_visible_as_idle_and_does_not_recommend(self):
        self.ask.return_value = ("无意图", "无", "无")
        self.queue("今天作业完成了吗").fire()
        self.generate.assert_not_called()
        self.assertEqual(self.statuses[-1][1], "idle")
        self.assertEqual(self.replies, [])

    def test_format_record_preserves_pasted_dialogue_without_second_prefix(self):
        pasted = "  小明：明天去上海。 \n\n 小红:取消出行。 "
        self.assertEqual(bot._format_record("外层昵称", pasted), "小明：明天去上海。\n小红:取消出行。")
        self.assertEqual(bot._format_record("小明", "今晚吃火锅"), "小明:今晚吃火锅")
        self.assertEqual(bot._format_record("小明", "第一行\n第二行"), "小明:第一行\n第二行")

    def test_shutdown_invalidates_all_pending_timers(self):
        prediction = self.queue("去上海")
        clear = bot._ROOMS["test-room"].clear_timer
        bot.shutdown()
        self.assertEqual(bot._ROOMS, {})
        self.assertEqual(bot.ROOM_TIMERS, {})
        self.assertTrue(prediction.cancelled)
        self.assertTrue(clear.cancelled)
        prediction.fire()
        clear.fire()
        self.ask.assert_not_called()
        self.assertEqual(self.replies, [])


if __name__ == "__main__":
    unittest.main()
