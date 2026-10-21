using ChatAssistant.Core;

internal static class GroupRecommendationChecks
{
    public static void Run(Action<string, Action> check)
    {
        check("group stable text does not require known individual roles", () =>
        {
            var tracker = new ConversationTracker();
            tracker.Observe("群聊", [new("unknown", "周六一起吃饭吧")]);
            var stable = tracker.Observe("群聊", [new("unknown", "周六一起吃饭吧")]).Snapshot;
            Assert(stable.IsStable && !stable.CanSuggest);
            Assert(GroupRecommendationEngine.Analyze(stable)?.Intent == "吃饭");
        });
        check("group pending and reset states are never stable", () =>
        {
            var tracker = new ConversationTracker();
            Assert(!tracker.Observe("群聊", [new("unknown", "周六一起吃饭吧")]).Snapshot.IsStable);
            tracker.ConfirmStableObservation(tracker.Snapshot.Version);
            Assert(!tracker.Reset().Snapshot.IsStable);
        });
        check("time stability confirms one stationary capture safely", () =>
        {
            var tracker = new ConversationTracker();
            var first = tracker.Observe("群聊", [new("unknown", "周六一起吃饭吧")]);
            Assert(!tracker.ConfirmStableObservation(first.Snapshot.Version - 1).Snapshot.IsStable);
            var stable = tracker.ConfirmStableObservation(first.Snapshot.Version);
            Assert(stable.Snapshot.IsStable && stable.Snapshot.Messages.Count == 1);
            Assert(!tracker.ConfirmStableObservation(first.Snapshot.Version).Changed);
        });
        check("obsolete quiet timer cannot accept a newer OCR candidate", () =>
        {
            var tracker = new ConversationTracker();
            var old = tracker.Observe("群聊", [new("unknown", "周六一起吃饭吧")]);
            tracker.Observe("群聊", [new("unknown", "周日一起吃饭吧")]);
            Assert(!tracker.ConfirmStableObservation(old.Snapshot.Version).Snapshot.IsStable);
            Assert(tracker.ConfirmStableObservation(tracker.Snapshot.Version).Snapshot.Messages.Single().Text.Contains("周日"));
        });
        check("time stability keeps frame-only mode and rejects empty view", () =>
        {
            var tracker = new ConversationTracker();
            var first = tracker.ObserveCurrentFrame([new("unknown", "周六一起吃饭吧")]);
            var stable = tracker.ConfirmStableObservation(first.Snapshot.Version).Snapshot;
            Assert(stable.Key == ConversationTracker.CurrentFrameKey && stable.IsStable);
            tracker.ObserveCurrentFrame([]);
            Assert(!tracker.ConfirmStableObservation(tracker.Snapshot.Version).Snapshot.IsStable);
        });
        check("group dining aggregates independent members' constraints", () =>
        {
            var result = Analyze("周六晚上七点一起吃饭吧", "我们四个人", "就在朝阳大悦城，人均100元", "我不能吃辣");
            Assert(result.Intent == "吃饭" && result.Missing.Count == 0 && result.Options.Count >= 2);
            Assert(Fact(result, "人数") == "四个人" && Fact(result, "预算").Contains("100"));
            Assert(result.Evidence.Count == 4 && result.Evidence.Last().Text == "我不能吃辣");
        });
        check("group dining option does not ask to supply already known attendance and time", () =>
        {
            var complete = Analyze("周六晚上一起吃饭吧", "我们四个人", "在朝阳区");
            Assert(!complete.Options[1].Tradeoff.Contains("补齐") && complete.Options[1].Tradeoff.Contains("核实预约时间"));
            var missingPeople = Analyze("周六晚上一起吃饭吧");
            Assert(missingPeople.Options[1].Tradeoff.Contains("先补齐人数") && !missingPeople.Options[1].Tradeoff.Contains("补齐人数和时间"));
        });
        check("group message count never becomes participant count", () =>
        {
            var result = Analyze("周六一起吃饭吧", "好呀", "行", "学校附近", "我不吃辣");
            Assert(result.Missing.Contains("人数") && result.Facts.All(f => f.Label != "人数"));
        });
        check("group budget extraction retains per-person scope before and after budget", () =>
        {
            foreach (string budget in new[] { "人均预算一百元", "预算每人100元", "每位预算100元", "预算控制在人均100元" })
                Assert(Fact(Analyze("周六吃饭吧", budget), "预算") == budget, budget);
        });
        check("group missing constraints still permit tentative options", () =>
        {
            var result = Analyze("我们一起吃饭吧");
            Assert(result.Missing.Contains("地点") && result.Missing.Contains("预算") && result.Options.Count >= 2);
            Assert(result.Options.All(option => !option.Title.Contains("回复")));
        });
        check("group greetings and historical meals never start recommendations", () =>
        {
            foreach (string text in new[] { "吃饭了吗？", "你吃过饭吗", "晚饭吃了吗", "昨天我们一起吃饭了", "我刚吃完火锅", "我已经吃了饭" })
                Assert(GroupRecommendationEngine.Analyze(Snapshot(text)) is null, text);
        });
        check("group recalled travel never starts recommendations", () =>
        {
            foreach (string text in new[] { "去年我们一起旅游", "昨天从北京到上海", "刚坐高铁回来", "我去过上海" })
                Assert(GroupRecommendationEngine.Analyze(Snapshot(text)) is null, text);
        });
        check("group short side conversation preserves plan without importing its time", () =>
        {
            var result = Analyze("周六晚上七点一起吃饭吧", "我们四个人", "报告下午三点交吗", "哈哈", "人均100元");
            Assert(Fact(result, "时间") == "周六晚上七点" && Fact(result, "人数") == "四个人");
            Assert(result.Evidence.All(e => !e.Text.Contains("报告")));
        });
        check("group sustained unrelated discussion retires old suggestions", () =>
        {
            Assert(GroupRecommendationEngine.Analyze(Snapshot("今晚吃饭吧", "报告交了吗", "还没写完", "电脑又坏了", "找人修一下")) is null);
        });
        check("group explicit time correction replaces older proposal", () =>
        {
            var result = Analyze("周六晚上七点一起吃饭吧", "改到周日六点吧");
            Assert(Fact(result, "时间") == "周日六点" && !result.Missing.Contains("时间"));
        });
        check("group unscheduled postponement removes obsolete time", () =>
        {
            var result = Analyze("周六晚上七点一起吃饭吧", "改期吧，时间还没定");
            Assert(result.Missing.Contains("时间") && result.Facts.All(f => f.Label != "时间"));
        });
        check("group multiple unconfirmed time proposals are shown as alternatives", () =>
        {
            var result = Analyze("周六七点吃饭吧", "八点怎么样");
            Assert(Fact(result, "时间").Contains("七点") && Fact(result, "时间").Contains("八点") && result.Summary.Contains("多个说法"));
        });
        check("group dietary correction cannot erase another member's allergy", () =>
        {
            var result = Analyze("周六吃饭吧", "我对海鲜过敏", "还是吃火锅吧");
            Assert(Fact(result, "饮食偏好").Contains("海鲜过敏"));
            Assert(result.Options.Any(o => o.Tradeoff.Contains("过敏") || o.Tradeoff.Contains("忌口")));
        });
        check("group cuisine revision preserves allergies while retiring old cuisine", () =>
        {
            var result = Analyze("周六吃火锅吧", "我对海鲜过敏", "改为粤菜吧");
            Assert(Fact(result, "饮食偏好").Contains("海鲜过敏") && Fact(result, "饮食偏好").Contains("粤菜"));
            Assert(!Fact(result, "饮食偏好").Contains("火锅"));
        });
        check("group rescheduling cannot leave an obsolete date in cuisine facts", () =>
        {
            var result = Analyze("周六晚上吃火锅吧", "改到周日晚上七点");
            Assert(!Fact(result, "饮食偏好").Contains("周六") && Fact(result, "时间").Contains("周日"));
        });
        check("one member's party size is not the group total", () =>
        {
            var result = Analyze("周六聚餐吧", "我带两个人", "我一个人来");
            Assert(result.Missing.Contains("人数"));
        });
        check("group participant decline is not whole-plan cancellation", () =>
        {
            Assert(!Analyze("周六一起吃饭吧", "我不去了").IsCancelled);
            Assert(!Analyze("明天从北京去上海", "我把自己的票取消了").IsCancelled);
        });
        check("group cancellation withdraws the plan and does not revive on acknowledgement", () =>
        {
            var result = Analyze("周六吃饭吧", "聚餐取消吧", "好的");
            Assert(result.IsCancelled && result.Options.Count == 0);
        });
        check("group negated cancellation preserves active plan", () =>
        {
            Assert(!Analyze("周六一起吃饭吧", "不要取消，还是按原计划").IsCancelled);
        });
        check("group explicit negative event statement cancels recommendations", () =>
        {
            Assert(Analyze("周六一起吃饭吧", "我们周六不聚餐了").IsCancelled);
            Assert(!Analyze("周六一起吃饭吧", "我周六不聚餐了").IsCancelled);
        });
        check("group unavailable time is a restriction rather than a new agreed time", () =>
        {
            var result = Analyze("周六一起吃饭吧", "周日我没空");
            Assert(Fact(result, "时间") == "周六" && Fact(result, "时间限制") == "周日我没空");
        });
        check("group count uncertainty remains visible in extracted facts", () =>
        {
            var result = Analyze("周六一起吃饭吧", "至少4个人");
            Assert(Fact(result, "人数") == "至少4个人");
        });
        check("group new invitation after cancellation does not inherit old conditions", () =>
        {
            var result = Analyze("今晚七点我们三个人在学校吃饭", "今晚不去了，改为周六聚餐");
            Assert(!result.IsCancelled && Fact(result, "时间") == "周六" && result.Missing.Contains("人数") && result.Missing.Contains("地点"));
        });
        check("group travel captures route without invented schedules", () =>
        {
            var result = Analyze("明天从北京去上海，我们两个人", "人均预算500元", "想坐高铁，不坐飞机");
            Assert(result.Intent == "出行" && Fact(result, "出发地") == "北京" && Fact(result, "目的地") == "上海");
            Assert(result.Options.Count >= 2 && result.Options.All(o => !o.Title.Contains("航空")));
            Assert(result.Source.Contains("未查询实时"));
        });
        check("group explicit rail or few-transfer preference avoids generic air option", () =>
        {
            foreach (string preference in new[] { "优先高铁，少换乘", "优先坐火车", "少换乘" })
            {
                var result = Analyze("明天从北京去天津，我们两个人", preference);
                Assert(result.Options.Count >= 2 && result.Options.All(o => !o.Title.Contains("航空")), preference);
                Assert(result.Options.Any(o => o.Title.Contains("接驳组合")), preference);
            }
        });
        check("group switching to travel cannot inherit meal slots", () =>
        {
            var result = Analyze("今晚七点三个人去学校吃饭", "另外明天去上海出差");
            Assert(result.Intent == "出行" && result.Missing.Contains("人数") && result.Missing.Contains("出发地") && Fact(result, "目的地") == "上海");
        });
        check("group anonymous roles do not change semantic fingerprint", () =>
        {
            var first = Snapshot("今晚吃饭吧", "四个人，人均100元");
            var second = first with { Version = 9, Messages = first.Messages.Select(m => m with { Id = Guid.NewGuid().ToString(), Speaker = "self" }).ToArray() };
            Assert(GroupRecommendationEngine.Analyze(first)!.Fingerprint == GroupRecommendationEngine.Analyze(second)!.Fingerprint);
        });
        check("group equivalent numeral spelling does not repeat recommendation", () =>
        {
            Assert(Analyze("今晚吃饭吧", "四个人，人均一百元").Fingerprint == Analyze("今晚吃饭吧", "4人，人均100元").Fingerprint);
        });
        check("group unstable snapshot never gets analyzed", () =>
        {
            Assert(GroupRecommendationEngine.Analyze(Snapshot("周六一起吃饭吧") with { IsStable = false }) is null);
            Assert(GroupRecommendationEngine.Analyze(Snapshot("周六一起吃饭吧") with { IsStable = false, CanSuggest = true }) is not null);
        });
        check("recommendation coordinator waits two seconds then publishes once", () =>
        {
            var coordinator = new RecommendationCoordinator(); var now = DateTimeOffset.UnixEpoch;
            coordinator.Observe(Snapshot("周六吃饭吧"), now);
            Assert(coordinator.IsPending && coordinator.Current is null && !coordinator.Tick(now.AddSeconds(1)).Changed);
            Assert(coordinator.Tick(now.AddSeconds(2)).Changed && coordinator.Current is not null);
            Assert(!coordinator.Tick(now.AddSeconds(3)).Changed);
        });
        check("recommendation chatter and same facts do not restart quiet timer", () =>
        {
            var coordinator = new RecommendationCoordinator(); var now = DateTimeOffset.UnixEpoch;
            coordinator.Observe(Snapshot("周六吃饭吧"), now);
            coordinator.Observe(Snapshot("周六吃饭吧", "哈哈") with { Version = 2 }, now.AddSeconds(1));
            Assert(coordinator.Tick(now.AddSeconds(2)).Changed);
            Assert(!coordinator.Observe(Snapshot("周六吃饭吧", "哈哈") with { Version = 3 }, now.AddSeconds(3)).Changed);
            Assert(coordinator.Current?.Version == 3 && coordinator.StableSnapshot?.Version == 3);
        });
        check("recommendation changed conditions reset quiet time", () =>
        {
            var coordinator = new RecommendationCoordinator(); var now = DateTimeOffset.UnixEpoch;
            coordinator.Observe(Snapshot("周六吃饭吧"), now);
            coordinator.Observe(Snapshot("周六吃饭吧", "我们三个人") with { Version = 2 }, now.AddSeconds(1));
            Assert(!coordinator.Tick(now.AddSeconds(2)).Changed && coordinator.Tick(now.AddSeconds(3)).Changed);
        });
        check("recommendation continuous discussion publishes within eight stable seconds", () =>
        {
            var coordinator = new RecommendationCoordinator(); var now = DateTimeOffset.UnixEpoch;
            for (int second = 0; second <= 8; ++second)
                coordinator.Observe(Snapshot("周六吃饭吧", $"预算人均{100 + second}元") with { Version = second + 1 }, now.AddSeconds(second));
            Assert(coordinator.Tick(now.AddSeconds(8)).Changed && coordinator.Current!.Facts.Any(f => f.Value.Contains("108")));
        });
        check("recommendation unstable observations block publication and cloud context", () =>
        {
            var coordinator = new RecommendationCoordinator(); var now = DateTimeOffset.UnixEpoch;
            coordinator.Observe(Snapshot("周六吃饭吧"), now);
            coordinator.Observe(Snapshot("周六吃饭吧") with { Version = 2, IsStable = false }, now.AddSeconds(1));
            Assert(coordinator.StableSnapshot is null && coordinator.IsPending && !coordinator.Tick(now.AddSeconds(20)).Changed);
        });
        check("recommendation new group clears card and pending request immediately", () =>
        {
            var coordinator = new RecommendationCoordinator(); var now = DateTimeOffset.UnixEpoch;
            coordinator.Refresh(Snapshot("周六吃饭吧"), now);
            var update = coordinator.Observe(Snapshot("报告交了吗") with { Key = "另一群", IsStable = false }, now);
            Assert(update.Changed && update.Recommendation is null && coordinator.StableSnapshot is null);
        });
        check("recommendation cancellation withdraws without waiting for debounce", () =>
        {
            var coordinator = new RecommendationCoordinator(); var now = DateTimeOffset.UnixEpoch;
            coordinator.Refresh(Snapshot("周六吃饭吧"), now);
            var update = coordinator.Observe(Snapshot("周六吃饭吧", "聚餐取消了") with { Version = 2 }, now);
            Assert(update.Changed && update.Recommendation is null && !coordinator.IsPending && update.Status.Contains("取消"));
        });
        check("recommendation ignore suppresses only the ignored conditions", () =>
        {
            var coordinator = new RecommendationCoordinator(); var now = DateTimeOffset.UnixEpoch;
            var snapshot = Snapshot("周六吃饭吧"); coordinator.Refresh(snapshot, now);
            Assert(coordinator.IgnoreCurrent().Changed && coordinator.Current is null);
            coordinator.Observe(snapshot with { Version = 2 }, now.AddSeconds(3));
            Assert(!coordinator.IsPending && !coordinator.Tick(now.AddSeconds(5)).Changed);
            coordinator.Observe(Snapshot("周六吃饭吧", "人均100元") with { Version = 3 }, now.AddSeconds(6));
            Assert(coordinator.Tick(now.AddSeconds(8)).Changed);
        });
        check("recommendation explicit refresh overrides ignore but not stability", () =>
        {
            var coordinator = new RecommendationCoordinator(); var now = DateTimeOffset.UnixEpoch;
            var snapshot = Snapshot("周六吃饭吧"); coordinator.Refresh(snapshot, now); coordinator.IgnoreCurrent();
            Assert(!coordinator.Refresh(snapshot with { IsStable = false }, now).Changed && coordinator.Current is null);
            Assert(coordinator.Refresh(snapshot, now).Changed && coordinator.Current is not null);
        });
        check("recommendation refresh cancellation reports removal exactly once", () =>
        {
            var coordinator = new RecommendationCoordinator(); var now = DateTimeOffset.UnixEpoch;
            coordinator.Refresh(Snapshot("周六吃饭吧"), now);
            var cancelled = Snapshot("周六吃饭吧", "聚餐取消了") with { Version = 2 };
            var update = coordinator.Refresh(cancelled, now);
            Assert(update.Changed && update.Recommendation is null && coordinator.Current is null && !coordinator.IsPending);
            Assert(!coordinator.Refresh(cancelled, now).Changed);
        });
        check("recommendation refresh unrelated chat reports removal exactly once", () =>
        {
            var coordinator = new RecommendationCoordinator(); var now = DateTimeOffset.UnixEpoch;
            coordinator.Refresh(Snapshot("周六吃饭吧"), now);
            var ordinary = Snapshot("报告交了吗") with { Version = 2 };
            var update = coordinator.Refresh(ordinary, now);
            Assert(update.Changed && update.Recommendation is null && coordinator.Current is null && !coordinator.IsPending);
            Assert(!coordinator.Refresh(ordinary, now).Changed);
        });
        check("recommendation reset prevents delayed old group publication", () =>
        {
            var coordinator = new RecommendationCoordinator(); var now = DateTimeOffset.UnixEpoch;
            coordinator.Observe(Snapshot("周六吃饭吧"), now); coordinator.Reset();
            Assert(!coordinator.Tick(now.AddSeconds(20)).Changed && coordinator.Current is null && coordinator.StableSnapshot is null);
        });
    }
    private static void Assert(bool condition, string message = "assertion failed") { if (!condition) throw new InvalidOperationException(message); }
    private static ConversationSnapshot Snapshot(params string[] text) => new("群聊", 1, text.Select((line, index) => new ChatMessage($"m{index}", "unknown", line)).ToArray(), false, "已稳定") { IsStable = true };
    private static GroupRecommendation Analyze(params string[] text) => GroupRecommendationEngine.Analyze(Snapshot(text)) ?? throw new InvalidOperationException("expected recommendation");
    private static string Fact(GroupRecommendation recommendation, string label) => recommendation.Facts.FirstOrDefault(f => f.Label == label)?.Value ?? "";
}
