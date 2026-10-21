using ChatAssistant.Core;
using ChatAssistant.Platform;
using ChatAssistant.Recognition;

int passed = 0;
var failures = new List<string>();
void Check(string name, Action test)
{
    try { test(); ++passed; Console.WriteLine($"PASS {name}"); }
    catch (Exception exception) { failures.Add($"{name}: {exception.Message}"); Console.WriteLine($"FAIL {name}: {exception.Message}"); }
}
static void Assert(bool condition, string message = "assertion failed")
{
    if (!condition) throw new InvalidOperationException(message);
}
static ObservedMessage M(string text, string speaker = "other") => new(speaker, text);
static ConversationTracker Established(params ObservedMessage[] messages)
{
    var tracker = new ConversationTracker();
    tracker.Observe("会话甲", messages);
    tracker.Observe("会话甲", messages);
    return tracker;
}
static TrackerUpdate Twice(ConversationTracker tracker, string key, params ObservedMessage[] messages)
{
    tracker.Observe(key, messages);
    return tracker.Observe(key, messages);
}
static ChatMessage C(string text, string speaker = "other") => new(Guid.NewGuid().ToString(), speaker, text);

Check("first frame waits for stability", () =>
{
    var tracker = new ConversationTracker();
    var update = tracker.Observe("会话甲", [M("明天一起吃饭吗？")]);
    Assert(!update.Snapshot.CanSuggest && update.NewMessages.Count == 0);
});
Check("initial stable viewport establishes manual-only baseline", () =>
{
    var tracker = new ConversationTracker();
    var update = Twice(tracker, "会话甲", M("明天一起吃饭吗？"));
    Assert(update.IsBaseline && update.NewMessages.Count == 0 && update.Snapshot.CanSuggest);
});
Check("unchanged frames preserve ids and version", () =>
{
    var tracker = Established(M("明天一起吃饭吗？"));
    var before = tracker.Snapshot;
    var update = tracker.Observe("会话甲", [M("明天一起吃饭吗？")]);
    Assert(!update.Changed && update.Snapshot.Version == before.Version && update.Snapshot.Messages[0].Id == before.Messages[0].Id);
});
Check("first changed frame invalidates a pending reply immediately", () =>
{
    var tracker = Established(M("明天一起吃饭吗？"));
    long requestVersion = tracker.Snapshot.Version;
    var update = tracker.Observe("会话甲", [M("明天一起吃饭吗？"), M("可以，几点？", "self")]);
    Assert(update.Snapshot.Version > requestVersion && !update.Snapshot.CanSuggest && update.NewMessages.Count == 0);
});
Check("stable new tail is emitted exactly once", () =>
{
    var tracker = Established(M("明天一起吃饭吗？"));
    var messages = new[] { M("明天一起吃饭吗？"), M("可以，几点？", "self") };
    var update = Twice(tracker, "会话甲", messages);
    Assert(update.NewMessages.Count == 1 && update.NewMessages[0].Text == "可以，几点？");
    Assert(tracker.Observe("会话甲", messages).NewMessages.Count == 0);
});
Check("partially scrolled viewport appends only unseen tail", () =>
{
    var tracker = Established(M("明天一起吃饭吗？"), M("我们可以晚上见面", "self"), M("那么就在七点见吧"));
    var update = Twice(tracker, "会话甲", M("我们可以晚上见面", "self"), M("那么就在七点见吧"), M("地点还没有确定", "self"));
    Assert(update.NewMessages.Count == 1 && update.Snapshot.Messages.Count == 4);
});
Check("older visible subset is never a new message", () =>
{
    var tracker = Established(M("先商量见面时间吧"), M("明天晚上七点见面", "self"), M("地点选在学校附近"));
    var update = Twice(tracker, "会话甲", M("先商量见面时间吧"), M("明天晚上七点见面", "self"));
    Assert(update.NewMessages.Count == 0 && update.Snapshot.Messages.Count == 3);
});
Check("return from older viewport does not duplicate known messages", () =>
{
    var first = M("先商量见面时间吧"); var second = M("明天晚上七点见面", "self"); var third = M("地点选在学校附近");
    var tracker = Established(first, second, third);
    Twice(tracker, "会话甲", first, second);
    var update = Twice(tracker, "会话甲", second, third, M("那我们再确认一下人数", "self"));
    Assert(update.NewMessages.Count == 1 && update.Snapshot.Messages.Count == 4);
});
Check("older prepended lines do not trigger reminders", () =>
{
    var first = M("先商量见面时间吧"); var second = M("明天晚上七点见面", "self");
    var tracker = Established(first, second);
    var update = Twice(tracker, "会话甲", M("这是更早的一条消息"), first, second);
    Assert(update.NewMessages.Count == 0 && update.Snapshot.Messages.Count == 2);
});
Check("discontinuous viewport invalidates before stability", () =>
{
    var tracker = Established(M("先商量见面时间吧"));
    long previous = tracker.Snapshot.Version;
    var update = tracker.Observe("会话甲", [M("这是一段完全不同的旧消息")]);
    Assert(update.Snapshot.Version > previous && !update.Snapshot.CanSuggest && update.Snapshot.Messages.Count == 0);
});
Check("stable discontinuity requires explicit review", () =>
{
    var tracker = Established(M("先商量见面时间吧"));
    var update = Twice(tracker, "会话甲", M("这是一段完全不同的旧消息"));
    Assert(!update.Snapshot.CanSuggest && update.NewMessages.Count == 0 && update.Snapshot.Messages.Count == 1);
});
Check("review remains required after further scroll", () =>
{
    var tracker = Established(M("先商量见面时间吧"));
    Twice(tracker, "会话甲", M("这是一段完全不同的旧消息"));
    var update = Twice(tracker, "会话甲", M("这是一段完全不同的旧消息"), M("后面又出现一条历史消息"));
    Assert(!update.Snapshot.CanSuggest && update.NewMessages.Count == 0);
});
Check("one noisy frame can recover without accepting its text", () =>
{
    var clean = M("明天晚上七点见面");
    var tracker = Established(clean);
    tracker.Observe("会话甲", [M("明天晚上土点见面")]);
    var update = Twice(tracker, "会话甲", clean);
    Assert(update.Snapshot.CanSuggest && update.NewMessages.Count == 0 && update.Snapshot.Messages[0].Text == clean.Text);
});
Check("alternating OCR jitter cannot become stable", () =>
{
    var tracker = Established(M("明天晚上七点见面"));
    for (int index = 0; index < 8; ++index)
        tracker.Observe("会话甲", [M(index % 2 == 0 ? "明天晚上土点见面" : "明天晚上十点见面")]);
    Assert(!tracker.Snapshot.CanSuggest);
});
Check("contact switch clears old history on first frame", () =>
{
    var tracker = Established(M("这是甲的消息"));
    long previous = tracker.Snapshot.Version;
    var update = tracker.Observe("会话乙", [M("这是乙的消息")]);
    Assert(update.Snapshot.Key == "会话乙" && update.Snapshot.Messages.Count == 0 && update.Snapshot.Version > previous && !update.Snapshot.CanSuggest);
});
Check("new contact starts independent baseline", () =>
{
    var tracker = Established(M("这是甲的消息"));
    var update = Twice(tracker, "会话乙", M("这是乙的消息"));
    Assert(update.IsBaseline && update.NewMessages.Count == 0 && update.Snapshot.Messages.Count == 1 && update.Snapshot.Messages[0].Text == "这是乙的消息");
});
Check("unknown conversation title erases prior context", () =>
{
    var tracker = Established(M("这是甲的消息"));
    var update = tracker.Observe(null, [M("这里不知道是谁")]);
    Assert(update.Snapshot.Key == "" && update.Snapshot.Messages.Count == 0 && !update.Snapshot.CanSuggest);
});
Check("repeated missing title does not generate repeated updates", () =>
{
    var tracker = Established(M("这是甲的消息"));
    tracker.Observe("unknown", [M("这里不知道是谁")]);
    var update = tracker.Observe("unknown", [M("这里不知道是谁")]);
    Assert(!update.Changed);
});
Check("unknown speaker prevents cloud suggestions", () =>
{
    var tracker = Established(M("不能确定是我还是对方", "unknown"));
    Assert(!tracker.Snapshot.CanSuggest);
});
Check("invalid speaker is normalized to unknown", () =>
{
    var tracker = Established(M("不能确定是我还是对方", "left-ish"));
    Assert(!tracker.Snapshot.CanSuggest && tracker.Snapshot.Messages[0].Speaker == "unknown");
});
Check("manual role correction permits suggestions", () =>
{
    var tracker = Established(M("明天要一起吃饭吗", "unknown"));
    var update = tracker.ConfirmEdited("会话甲", [M("明天要一起吃饭吗", "other")]);
    Assert(update.Snapshot.CanSuggest && update.NewMessages.Count == 0);
});
Check("manual confirmation cannot bless an unknown role", () =>
{
    var tracker = Established(M("明天要一起吃饭吗", "unknown"));
    var update = tracker.ConfirmEdited("会话甲", [M("明天要一起吃饭吗", "unknown")]);
    Assert(!update.Snapshot.CanSuggest);
});
Check("unchanged OCR does not overwrite manual correction", () =>
{
    var raw = M("明天晚上土点见面", "unknown");
    var tracker = Established(raw);
    tracker.ConfirmEdited("会话甲", [M("明天晚上七点见面", "self")]);
    tracker.Observe("会话甲", [raw]);
    Assert(tracker.Snapshot.CanSuggest && tracker.Snapshot.Messages[0].Text == "明天晚上七点见面" && tracker.Snapshot.Messages[0].Speaker == "self");
});
Check("new tail retains previously corrected content", () =>
{
    var raw = M("明天晚上土点见面", "unknown");
    var tracker = Established(raw);
    tracker.ConfirmEdited("会话甲", [M("明天晚上七点见面", "self")]);
    var update = Twice(tracker, "会话甲", raw, M("地点就在学校食堂吧"));
    Assert(update.Snapshot.CanSuggest && update.NewMessages.Count == 1 && update.Snapshot.Messages[0].Text == "明天晚上七点见面");
});
Check("explicit reset invalidates all pending suggestions", () =>
{
    var tracker = Established(M("这是一个原来的会话"));
    long previous = tracker.Snapshot.Version;
    tracker.Reset("已暂停");
    Assert(tracker.Snapshot.Version > previous && !tracker.Snapshot.CanSuggest && tracker.Snapshot.Messages.Count == 0);
});
Check("snapshot retains at most twenty recent messages", () =>
{
    var tracker = Established(Enumerable.Range(1, 30).Select(i => M($"第{i}条完整测试消息")).ToArray());
    Assert(tracker.Snapshot.Messages.Count == 20 && tracker.Snapshot.Messages[0].Text == "第11条完整测试消息");
});
Check("rolling twenty-message viewport appends once", () =>
{
    var tracker = Established(Enumerable.Range(1, 20).Select(i => M($"第{i}条完整测试消息")).ToArray());
    var update = Twice(tracker, "会话甲", Enumerable.Range(2, 20).Select(i => M($"第{i}条完整测试消息")).ToArray());
    Assert(update.NewMessages.Count == 1 && update.Snapshot.Messages.Count == 20 && update.Snapshot.Messages[0].Text == "第2条完整测试消息");
});
Check("consecutive equal messages retain distinct ids", () =>
{
    var context = M("这是前面确认过的上下文消息", "self");
    var message = M("好的，我知道了");
    var tracker = Established(context, message);
    var update = Twice(tracker, "会话甲", context, message, message);
    Assert(update.NewMessages.Count == 1 && update.Snapshot.Messages[1].Id != update.Snapshot.Messages[2].Id);
});
Check("identical repeated text without a unique anchor does not auto-trigger", () =>
{
    var message = M("好的，我知道了");
    var tracker = Established(message);
    var update = Twice(tracker, "会话甲", message, message);
    Assert(update.NewMessages.Count == 0);
});
Check("short ambiguous overlap requires review", () =>
{
    var tracker = Established(M("前面是一段较长的消息"), M("好的", "self"));
    var update = Twice(tracker, "会话甲", M("好的", "self"), M("这里可能是另外一段历史消息"));
    Assert(update.NewMessages.Count == 0 && !update.Snapshot.CanSuggest);
});
Check("empty frame immediately disables suggestions", () =>
{
    var tracker = Established(M("这是当前会话内容"));
    var update = tracker.Observe("会话甲", []);
    Assert(!update.Snapshot.CanSuggest && update.NewMessages.Count == 0);
});
Check("blank OCR lines do not enter context", () =>
{
    var tracker = Established(M("  "), M("有意义的消息"));
    Assert(tracker.Snapshot.Messages.Count == 1);
});
Check("OCR whitespace jitter is treated as the same stable text", () =>
{
    var tracker = Established(M("明天 7 点见面"));
    var update = tracker.Observe("会话甲", [M("明天7点见面")]);
    Assert(!update.Changed && update.Snapshot.CanSuggest);
});
Check("ordinary conversation does not create arrangement hints", () =>
{
    Assert(ArrangementHints.Analyze([C("这份材料你看过了吗")]).Count == 0);
});
Check("dining hint identifies missing fields without cloud", () =>
{
    var hints = ArrangementHints.Analyze([C("我们一起吃饭吧")]);
    Assert(hints.Count == 1 && hints[0].Intent == "吃饭" && hints[0].Missing.Contains("时间") && hints[0].Missing.Contains("地点") && hints[0].Missing.Contains("人数"));
});
Check("Windows Chinese OCR spacing still triggers the local dining hint", () =>
{
    var message = C("周 六 晚 上 一 起 吃 饭 ？");
    var hints = ArrangementHints.Analyze([message]);
    Assert(hints.Count == 1 && hints[0].Intent == "吃饭" && !hints[0].Missing.Contains("时间"));
    Assert(message.Text == "周 六 晚 上 一 起 吃 饭 ？", "analysis must not rewrite the original message");
});
Check("complete meal information avoids false missing fields", () =>
{
    var hints = ArrangementHints.Analyze([C("明天晚上七点，我们三个人去学校食堂吃饭")]);
    Assert(hints.Count == 1 && hints[0].Missing.Count == 0);
});
Check("travel hint asks for missing departure location", () =>
{
    var hints = ArrangementHints.Analyze([C("明天去上海出差")]);
    Assert(hints.Count == 1 && hints[0].Intent == "出行" && hints[0].Missing.Contains("出发地") && !hints[0].Missing.Contains("出发时间"));
});
Check("different times produce tentative clarification", () =>
{
    var hints = ArrangementHints.Analyze([C("明天七点吃饭吗"), C("八点怎么样", "self")]);
    Assert(hints.Count == 1 && hints[0].Conflicts.Count == 1);
});
Check("equivalent written times are not a conflict", () =>
{
    var hints = ArrangementHints.Analyze([C("明天七点吃饭吗"), C("7:00吧", "self")]);
    Assert(hints.Count == 1 && hints[0].Conflicts.Count == 0);
});
Check("explicit time correction replaces older option", () =>
{
    var hints = ArrangementHints.Analyze([C("明天七点吃饭吗"), C("改成八点吧", "self")]);
    Assert(hints.Count == 1 && hints[0].Conflicts.Count == 0);
});
Check("cancelled arrangement produces no reminder", () =>
{
    Assert(ArrangementHints.Analyze([C("我们明天一起吃饭吧"), C("算了，先不安排", "self")]).Count == 0);
});
Check("diet preference is not cancellation", () =>
{
    Assert(ArrangementHints.Analyze([C("我们明天一起吃饭吧"), C("我不吃辣", "self")]).Count == 1);
});
Check("unknown speaker suppresses automatic arrangement hints", () =>
{
    Assert(ArrangementHints.Analyze([C("我们明天一起吃饭吧", "unknown")]).Count == 0);
});

Check("frame-only mode works without a recognized title", () =>
{
    var tracker = new ConversationTracker();
    var first = tracker.ObserveCurrentFrame([M("今晚吃饭吗？")]);
    Assert(!first.Snapshot.CanSuggest);
    var second = tracker.ObserveCurrentFrame([M("今晚吃饭吗？")]);
    Assert(second.Snapshot.CanSuggest && second.Snapshot.Messages.Count == 1 && second.NewMessages.Count == 0);
});
Check("frame-only entry erases history from a recognized contact", () =>
{
    var tracker = Established(M("甲的专属内容"));
    var previous = tracker.Snapshot.Version;
    var first = tracker.ObserveCurrentFrame([M("乙的当前消息")]);
    Assert(first.Snapshot.Version > previous && first.Snapshot.Messages.Count == 0 && !first.Snapshot.CanSuggest);
    var second = tracker.ObserveCurrentFrame([M("乙的当前消息")]);
    Assert(second.Snapshot.Messages.Count == 1 && second.Snapshot.Messages[0].Text == "乙的当前消息");
});
Check("frame-only changed viewport replaces rather than appends context", () =>
{
    var tracker = new ConversationTracker();
    tracker.ObserveCurrentFrame([M("旧画面独有消息"), M("这是共有消息")]);
    tracker.ObserveCurrentFrame([M("旧画面独有消息"), M("这是共有消息")]);
    long previous = tracker.Snapshot.Version;
    var first = tracker.ObserveCurrentFrame([M("这是共有消息"), M("新画面独有消息")]);
    Assert(first.Snapshot.Version > previous && !first.Snapshot.CanSuggest && first.Snapshot.Messages.Count == 0);
    var second = tracker.ObserveCurrentFrame([M("这是共有消息"), M("新画面独有消息")]);
    Assert(second.Snapshot.CanSuggest && second.Snapshot.Messages.Count == 2
        && second.Snapshot.Messages.All(m => m.Text != "旧画面独有消息") && second.NewMessages.Count == 0);
});
Check("frame-only contact jump becomes a new bounded view without review deadlock", () =>
{
    var tracker = new ConversationTracker();
    tracker.ObserveCurrentFrame([M("甲打算明天去上海")]);
    tracker.ObserveCurrentFrame([M("甲打算明天去上海")]);
    tracker.ObserveCurrentFrame([M("乙询问报告写完了吗")]);
    var update = tracker.ObserveCurrentFrame([M("乙询问报告写完了吗")]);
    Assert(update.Snapshot.CanSuggest && update.Snapshot.Messages.Count == 1
        && update.Snapshot.Messages[0].Text == "乙询问报告写完了吗");
});
Check("frame-only unknown roles still require correction", () =>
{
    var tracker = new ConversationTracker();
    tracker.ObserveCurrentFrame([M("这句话是谁说的", "unknown")]);
    tracker.ObserveCurrentFrame([M("这句话是谁说的", "unknown")]);
    Assert(!tracker.Snapshot.CanSuggest);
});
Check("frame-only manual correction survives unchanged raw OCR", () =>
{
    var tracker = new ConversationTracker();
    var raw = M("今晚士点见面", "unknown");
    tracker.ObserveCurrentFrame([raw]); tracker.ObserveCurrentFrame([raw]);
    tracker.ConfirmEdited(tracker.Snapshot.Key, [M("今晚七点见面", "self")]);
    var version = tracker.Snapshot.Version;
    var update = tracker.ObserveCurrentFrame([raw]);
    Assert(update.Snapshot.CanSuggest && update.Snapshot.Version == version
        && update.Snapshot.Messages[0].Speaker == "self" && update.Snapshot.Messages[0].Text == "今晚七点见面");
});
Check("frame-only manual corrections are not carried into a changed view", () =>
{
    var tracker = new ConversationTracker();
    var raw = M("今晚士点见面", "unknown");
    tracker.ObserveCurrentFrame([raw]); tracker.ObserveCurrentFrame([raw]);
    tracker.ConfirmEdited(tracker.Snapshot.Key, [M("今晚七点见面", "self")]);
    tracker.ObserveCurrentFrame([M("完全不同的当前聊天")]);
    var update = tracker.ObserveCurrentFrame([M("完全不同的当前聊天")]);
    Assert(update.Snapshot.Messages.Count == 1 && update.Snapshot.Messages[0].Text == "完全不同的当前聊天");
});
Check("frame-only unchanged view preserves version and ids", () =>
{
    var tracker = new ConversationTracker();
    tracker.ObserveCurrentFrame([M("这是可见消息")]); tracker.ObserveCurrentFrame([M("这是可见消息")]);
    var previous = tracker.Snapshot;
    var update = tracker.ObserveCurrentFrame([M("这是可见消息")]);
    Assert(!update.Changed && update.Snapshot.Version == previous.Version && update.Snapshot.Messages[0].Id == previous.Messages[0].Id);
});
Check("frame-only empty view clears context and suggestions immediately", () =>
{
    var tracker = new ConversationTracker();
    tracker.ObserveCurrentFrame([M("这是可见消息")]); tracker.ObserveCurrentFrame([M("这是可见消息")]);
    var update = tracker.ObserveCurrentFrame([]);
    Assert(!update.Snapshot.CanSuggest && update.Snapshot.Messages.Count == 0);
});
Check("return to an identified contact does not inherit frame-only history", () =>
{
    var tracker = new ConversationTracker();
    tracker.ObserveCurrentFrame([M("来历不明的画面")]); tracker.ObserveCurrentFrame([M("来历不明的画面")]);
    var update = Twice(tracker, "visual-session-3", M("恢复标题后的当前消息"));
    Assert(update.Snapshot.CanSuggest && update.Snapshot.Messages.Count == 1
        && update.Snapshot.Messages[0].Text == "恢复标题后的当前消息");
});

static CaptureFrame TitleFrame(int pattern = 0, int noise = 0, bool dark = false, bool blank = false)
{
    const int width = 320, height = 80;
    byte[] pixels = new byte[width * height * 4];
    for (int y = 0; y < height; ++y)
        for (int x = 0; x < width; ++x)
        {
            bool foreground = !blank && y >= 20 && y < 60 && x >= 30 && x < 280
                && (x % 30 < 3 || y is >= 24 and < 27 || y is >= 48 and < 51);
            if (!blank && pattern == 1 && x >= 200 && x < 212 && y >= 30 && y < 45) foreground = true;
            int luminance = (foreground ? 30 : 242);
            if (dark) luminance = 255 - luminance;
            luminance += noise == 0 ? 0 : ((x * 17 + y * 13) % 7 - 3) * noise;
            int index = (y * width + x) * 4;
            pixels[index] = pixels[index + 1] = pixels[index + 2] = (byte)Math.Clamp(luminance, 0, 255);
            pixels[index + 3] = 255;
        }
    return new(width, height, pixels, "synthetic title", DateTimeOffset.UtcNow);
}

Check("visible title image remains identified when OCR is empty", () =>
{
    var resolver = new SessionIdentityResolver();
    var identity = resolver.Resolve(TitleFrame(), NormalizedRegion.Full, "");
    Assert(!identity.FrameOnly && identity.Key.Length > 0 && identity.DisplayName.Length > 0);
});
Check("unreadable OCR label does not block a visible title image", () =>
{
    var resolver = new SessionIdentityResolver();
    var identity = resolver.Resolve(TitleFrame(), NormalizedRegion.Full, "□ ? ■");
    Assert(!identity.FrameOnly && identity.DisplayName == "当前会话（名称未识别）");
});
Check("OCR label jitter does not change image identity", () =>
{
    var resolver = new SessionIdentityResolver();
    var first = resolver.Resolve(TitleFrame(), NormalizedRegion.Full, "小林");
    var second = resolver.Resolve(TitleFrame(), NormalizedRegion.Full, "小木");
    var third = resolver.Resolve(TitleFrame(), NormalizedRegion.Full, "");
    Assert(first.Key == second.Key && second.Key == third.Key && !second.Changed && !third.Changed);
});
Check("minor pixel brightness noise does not reset conversation", () =>
{
    var resolver = new SessionIdentityResolver();
    var first = resolver.Resolve(TitleFrame(), NormalizedRegion.Full, "小林");
    var noisy = resolver.Resolve(TitleFrame(noise: 2), NormalizedRegion.Full, "小林");
    Assert(first.Key == noisy.Key && !noisy.Changed);
});
Check("different title image invalidates even when OCR names match", () =>
{
    var resolver = new SessionIdentityResolver();
    var first = resolver.Resolve(TitleFrame(), NormalizedRegion.Full, "小林");
    var second = resolver.Resolve(TitleFrame(pattern: 1), NormalizedRegion.Full, "小林");
    Assert(first.Key != second.Key && second.Changed && !second.FrameOnly);
});
Check("blank title region uses current-frame mode despite hallucinated OCR", () =>
{
    var resolver = new SessionIdentityResolver();
    var identity = resolver.Resolve(TitleFrame(blank: true), NormalizedRegion.Full, "小林");
    Assert(identity.FrameOnly && identity.Key.Length == 0);
});
Check("missing or invalid title data uses current-frame mode", () =>
{
    var resolver = new SessionIdentityResolver();
    Assert(resolver.Resolve(null, NormalizedRegion.Full, "小林").FrameOnly);
    Assert(resolver.Resolve(TitleFrame(), null, "小林").FrameOnly);
    Assert(resolver.Resolve(TitleFrame(), new(-1, 0, 1, 1), "小林").FrameOnly);
    Assert(resolver.Resolve(new(320, 80, [], "broken", DateTimeOffset.UtcNow), NormalizedRegion.Full, "小林").FrameOnly);
});
Check("restoring title after loss creates fresh session instead of reusing history", () =>
{
    var resolver = new SessionIdentityResolver();
    var first = resolver.Resolve(TitleFrame(), NormalizedRegion.Full, "小林");
    var lost = resolver.Resolve(TitleFrame(blank: true), NormalizedRegion.Full, "");
    var restored = resolver.Resolve(TitleFrame(), NormalizedRegion.Full, "小林");
    Assert(lost.FrameOnly && lost.Changed && restored.Key != first.Key && restored.Changed);
});
Check("dark theme title also survives empty OCR and brightness noise", () =>
{
    var resolver = new SessionIdentityResolver();
    var first = resolver.Resolve(TitleFrame(dark: true), NormalizedRegion.Full, "");
    var second = resolver.Resolve(TitleFrame(dark: true, noise: 1), NormalizedRegion.Full, "");
    Assert(!first.FrameOnly && first.Key == second.Key);
});
Check("resolver reset cannot reuse a previous session key", () =>
{
    var resolver = new SessionIdentityResolver();
    var first = resolver.Resolve(TitleFrame(), NormalizedRegion.Full, "小林");
    resolver.Reset();
    var second = resolver.Resolve(TitleFrame(), NormalizedRegion.Full, "小林");
    Assert(first.Key != second.Key);
});

Check("meal greetings do not create an appointment", () =>
{
    foreach (var text in new[] { "你吃饭了吗？", "吃饭了没", "吃饭没？", "你吃了没？", "你吃过饭吗？", "晚饭吃了吗？" })
        Assert(ArrangementHints.Analyze([C(text)]).Count == 0, text);
});
Check("meal recollections do not create an appointment", () =>
{
    foreach (var text in new[] { "我刚吃完火锅", "我已经吃了饭", "昨天我们一起吃饭了", "刚刚去那家餐厅吃过了" })
        Assert(ArrangementHints.Analyze([C(text)]).Count == 0, text);
});
Check("a future invitation is distinguished from a meal greeting", () =>
{
    foreach (var text in new[] { "今晚吃饭吗？", "晚上七点一起吃饭", "我们周六去吃火锅吧" })
        Assert(ArrangementHints.Analyze([C(text)]).Single().Intent == "吃饭", text);
});
Check("a completed meal does not negate a later invitation in the same message", () =>
{
    var hints = ArrangementHints.Analyze([C("吃过了，晚上7点一起吃饭")]);
    Assert(hints.Count == 1 && hints[0].Intent == "吃饭" && !hints[0].Missing.Contains("时间"));
});
Check("meal greeting after an old arrangement changes the immediate topic", () =>
{
    Assert(ArrangementHints.Analyze([C("明天一起吃饭吧"), C("你今天吃饭了吗？")]).Count == 0);
});
Check("an unrelated latest question cannot inherit old dining slots", () =>
{
    var context = new[] { C("明天一起吃饭吗？"), C("先看看时间", "self"), C("报告什么时候交？") };
    Assert(ArrangementHints.Analyze(context).Count == 0);
});
Check("a new ordinary question with a clock time does not become a meal follow-up", () =>
{
    Assert(ArrangementHints.Analyze([C("今晚吃饭吗？"), C("报告下午三点交吗？")]).Count == 0);
});
Check("an explicit new invitation after unrelated discussion starts fresh", () =>
{
    var context = new[] { C("明天七点，我们三个人去学校食堂吃饭"), C("报告什么时候交？"), C("周六一起吃饭吧") };
    var hint = ArrangementHints.Analyze(context).Single();
    Assert(hint.Missing.Contains("地点") && hint.Missing.Contains("人数") && !hint.Missing.Contains("时间"));
});
Check("cancellation does not revive after a later acknowledgement", () =>
{
    Assert(ArrangementHints.Analyze([C("明天一起吃饭"), C("算了，不去了"), C("好的", "self")]).Count == 0);
});
Check("negated cancellation keeps the active plan", () =>
{
    var hints = ArrangementHints.Analyze([C("明天一起吃饭吧"), C("不要取消，还是按原计划", "self")]);
    Assert(hints.Count == 1 && hints[0].Intent == "吃饭");
});
Check("a later actual cancellation wins over a negated cancellation", () =>
{
    Assert(ArrangementHints.Analyze([C("今晚吃饭吗"), C("不要取消明天的活动，今晚的饭局取消吧")]).Count == 0);
});
Check("new invitation after cancellation starts a new plan in the same message", () =>
{
    var hints = ArrangementHints.Analyze([C("今晚七点在学校三个人吃饭"), C("今晚不去了，改为周六聚餐")]);
    Assert(hints.Count == 1 && hints[0].Missing.Contains("地点") && hints[0].Missing.Contains("人数"));
});
Check("a requested reschedule without a replacement time reopens the time field", () =>
{
    var hint = ArrangementHints.Analyze([C("明天晚上七点一起吃饭"), C("改期吧，时间还没定")]).Single();
    Assert(hint.Missing.Contains("时间") && hint.Conflicts.Count == 0);
});
Check("a specific reschedule replaces old proposed times", () =>
{
    var hint = ArrangementHints.Analyze([C("明天晚上七点吃饭"), C("八点怎么样", "self"), C("改到周六八点吧")]).Single();
    Assert(!hint.Missing.Contains("时间") && hint.Conflicts.Count == 0);
});
Check("meal constraints continue an active plan without becoming cancellation", () =>
{
    var hint = ArrangementHints.Analyze([C("今晚一起吃饭吧"), C("我不能吃辣", "self")]).Single();
    Assert(hint.Intent == "吃饭");
});
Check("slot values preceding the invitation in one utterance are retained", () =>
{
    var hint = ArrangementHints.Analyze([C("明天晚上七点，我们三个人去学校食堂吃饭")]).Single();
    Assert(hint.Missing.Count == 0);
});
Check("switching from meal to travel drops meal-specific assumptions", () =>
{
    var hints = ArrangementHints.Analyze([C("今晚七点三个人去学校食堂吃饭"), C("另外明天去上海出差")]);
    Assert(hints.Count == 1 && hints[0].Intent == "出行" && hints[0].Missing.Contains("出发地"));
});
Check("recalled travel does not revive a cancelled itinerary", () =>
{
    Assert(ArrangementHints.Analyze([C("明天去上海出差"), C("行程取消了"), C("昨天坐高铁回来了")]).Count == 0);
});

Check("a meal location and dietary follow-up retain the same arrangement", () =>
{
    var context = new[] { C("今晚一起吃饭吗？", "self"), C("好呀，我们三个人，预算人均一百元。"), C("就在朝阳大悦城，晚上七点半，别吃辣。") };
    var hint = ArrangementHints.Analyze(context).Single();
    Assert(hint.Intent == "吃饭" && hint.Missing.Count == 0);
});
Check("a transport question continues the active route", () =>
{
    var hint = ArrangementHints.Analyze([C("明天从北京去上海，我们两个人。"), C("好的，想坐高铁还是飞机？", "self")]).Single();
    Assert(hint.Intent == "出行" && hint.Missing.Count == 0);
});

GroupRecommendationChecks.Run(Check);
Console.WriteLine($"RESULT passed={passed} failed={failures.Count}");
Environment.ExitCode = failures.Count == 0 ? 0 : 1;
