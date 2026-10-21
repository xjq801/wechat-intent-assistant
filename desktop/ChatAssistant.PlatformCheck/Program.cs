using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChatAssistant.Platform;
using ChatAssistant.Recognition;
using ChatAssistant;
using ChatAssistant.Core;
using System.Diagnostics;

internal static class Program
{
    private static int _checks;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--scale-benchmark")) RunScaleBenchmark().GetAwaiter().GetResult();
            else RunAsync().GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static async Task RunScaleBenchmark()
    {
        var messages = new[] { new ObservedMessage("self", "今晚一起吃饭吗？"),
            new ObservedMessage("other", "好呀，我们三个人，预算人均一百元。"),
            new ObservedMessage("other", "就在朝阳大悦城，晚上七点半，别吃辣。") };
        var scenes = new[] { "小林", "小周", "测试会话" }
            .Select(name => (Name: name, Frame: SyntheticScene.Render(name, messages))).ToArray();
        foreach (double scale in new[] { 1d, 1.5, 2d })
        {
            var ocr = new LocalOcrService(preferredScale: scale);
            int correctTitles = 0, correctMessages = 0;
            var elapsed = new List<long>();
            foreach (var scene in scenes)
            {
                var watch = Stopwatch.StartNew();
                var heading = await ocr.RecognizeAsync(scene.Frame, SyntheticScene.Header);
                var body = await ocr.RecognizeAsync(scene.Frame, SyntheticScene.Messages);
                var parsed = MessageParser.Parse(scene.Frame, SyntheticScene.Messages, body);
                elapsed.Add(watch.ElapsedMilliseconds);
                string title = Compact(string.Join("", heading.Lines.Select(l => l.Text)));
                if (title == scene.Name) correctTitles++;
                correctMessages += messages.Count(expected => parsed.Any(actual => actual.Speaker == expected.Speaker
                    && Letters(actual.Text) == Letters(expected.Text)));
                Console.WriteLine($"OCR_SCALE_CASE scale={scale} expected={scene.Name} actual={title} " +
                    $"messages={parsed.Count} elapsedMs={watch.ElapsedMilliseconds}");
            }
            Console.WriteLine($"OCR_SCALE_SUMMARY scale={scale} titles={correctTitles}/3 " +
                $"exactMessages={correctMessages}/9 medianMs={elapsed.Order().ElementAt(1)}");
        }
    }

    private static string Letters(string value) => new(value.Where(c => !char.IsWhiteSpace(c)
        && !char.IsPunctuation(c)).ToArray());

    private static async Task RunAsync()
    {
        // All text is synthetic. No window, screen, clipboard or user file is read.
        var frame = RenderSyntheticFrame();
        var bubbles = RenderBubbleFrame();
        var sceneMessages = new[] { new ObservedMessage("self", "今晚一起吃饭吗？"),
            new ObservedMessage("other", "好呀，我们三个人，预算人均一百元。"),
            new ObservedMessage("other", "就在朝阳大悦城，晚上七点半，别吃辣。") };
        var appScene = SyntheticScene.Render("小林", sceneMessages);
        var compactScene = RenderCompactBubbles();
        var groupScene = RenderGroupBubbles();
        TestRecommendedRegions();
        await RunPumpChecks();
        Check(LocalOcrService.AvailableLanguages.Any(x => x.StartsWith("zh")), "Chinese OCR pack available");
        var ocr = new LocalOcrService();
        var full = await ocr.RecognizeAsync(frame, NormalizedRegion.Full);
        var text = Compact(string.Join("\n", full.Lines.Select(l => l.Text)));
        Check(text.Contains("周六晚上一起吃饭"), "Recognize incoming Chinese message");
        Check(text.Contains("八点以后才有空"), "Recognize outgoing Chinese message");
        Check(text.Contains("七点在老地方见"), "Recognize follow-up Chinese message");
        Check(full.Lines.All(l => l.X >= 0 && l.Y >= 0 && l.X + l.Width <= full.Width + 1
            && l.Y + l.Height <= full.Height + 1), "OCR coordinates stay inside ROI");

        var right = await ocr.RecognizeAsync(frame, new NormalizedRegion(.5, 0, .5, 1));
        string rightText = Compact(string.Join("\n", right.Lines.Select(l => l.Text)));
        Check(rightText.Contains("八点以后才有空") && !rightText.Contains("周六晚上一起吃饭"),
            "ROI cropping excludes other conversation region");
        Check(right.Width == frame.Width / 2 && right.Height == frame.Height, "ROI size is preserved");
        Check(right.Lines.All(l => l.X < right.Width), "ROI-relative coordinates");

        var bubbleRegion = new NormalizedRegion(.1, .1, .8, .8);
        var bubbleOcr = await ocr.RecognizeAsync(bubbles, bubbleRegion);
        var parsed = MessageParser.Parse(bubbles, bubbleRegion, bubbleOcr);
        Check(parsed.Any(m => m.Speaker == "other" && Compact(m.Text).Contains("周六晚上一起吃饭")),
            "Parser identifies left white bubble after ROI offset");
        Check(parsed.Any(m => m.Speaker == "self" && Compact(m.Text).Contains("我八点以后才有空")),
            "Parser identifies right green bubble after ROI offset");
        Check(parsed.Any(m => m.Speaker == "other" && Compact(m.Text).Contains("明天晚上在三里屯")
            && Compact(m.Text).Contains("我们一共三个人")), "Parser groups multiline bubble");
        Check(parsed.Any(m => m.Speaker == "unknown" && Compact(m.Text).Contains("系统提")),
            "Parser keeps centered text role unknown");
        Check(parsed.Any(m => m.Speaker == "unknown" && Compact(m.Text).Contains("深色模式需要人工确认")),
            "Parser keeps unsupported dark bubble role unknown");
        if (parsed.Count != 5) Console.WriteLine("Synthetic parser diagnostic: " +
            string.Join(" | ", parsed.Select(m => m.Speaker + ":" + m.Text)));
        Check(parsed.Count == 5, "Parser separates five visually distinct messages");
        Check(parsed.All(m => !Compact(m.Text).Contains("12:30")), "Parser excludes timestamp text");
        Check(parsed.Any(m => m.Text.Contains("吃饭")), "Parser normalizes OCR spaces between Chinese characters");
        var hints = ChatAssistant.Core.ArrangementHints.Analyze(parsed.Where(m => m.Speaker != "unknown")
            .Select((m, i) => new ChatAssistant.Core.ChatMessage(i.ToString(), m.Speaker, m.Text)).ToArray());
        Check(hints.Any(h => h.Intent == "吃饭"), "Actual OCR messages reach local arrangement hints");

        var appOcr = await ocr.RecognizeAsync(appScene, SyntheticScene.Messages);
        var appParsed = MessageParser.Parse(appScene, SyntheticScene.Messages, appOcr);
        foreach (var expected in sceneMessages)
        {
            var actual = appParsed.FirstOrDefault(m => Letters(m.Text) == Letters(expected.Text));
            Check(actual is not null, "Small UI message survives actual OCR: " + expected.Text);
            Check(actual?.Speaker == expected.Speaker, "Small UI message keeps its speaker after enlargement");
        }
        var appPixels = SyntheticScene.Messages.ToPixels(appScene.Width, appScene.Height);
        Check(appOcr.Width == appPixels.Width && appOcr.Height == appPixels.Height
            && appOcr.Lines.All(l => l.X >= 0 && l.Y >= 0 && l.X + l.Width <= appPixels.Width + 1
                && l.Y + l.Height <= appPixels.Height + 1), "Enlarged OCR maps back to original ROI coordinates");

        var compactWatch = Stopwatch.StartNew();
        var compactOcr = await ocr.RecognizeAsync(compactScene, NormalizedRegion.Full);
        var compactMessages = MessageParser.Parse(compactScene, NormalizedRegion.Full, compactOcr);
        Console.WriteLine("Synthetic small-bubble diagnostic: " + string.Join(" | ", compactMessages.Select(m => m.Speaker + ":" + m.Text)));
        Check(compactMessages.Any(m => m.Speaker == "other" && Compact(m.Text).Contains("明天七点吃饭")),
            "Small font incoming bubble is recognized");
        Check(compactMessages.Any(m => m.Speaker == "self" && Compact(m.Text).Contains("好的")),
            "Narrow outgoing bubble is recognized");
        Check(compactMessages.Any(m => m.Speaker == "self" && Compact(m.Text).Contains("这条比较长的消息")),
            "Wide outgoing bubble keeps self role even when text begins left of center");
        Check(compactMessages.Any(m => m.Speaker == "other" && Compact(m.Text).Replace('：', ':').Contains("12:30")),
            "Meeting time inside bubble is preserved");
        Check(!compactMessages.Any(m => Compact(m.Text).Contains("以下为新消息")), "Known centered system separator is removed");
        Check(compactMessages.Any(m => m.Speaker == "other" && Compact(m.Text).Contains("收到"))
            && compactMessages.Any(m => m.Speaker == "other" && Compact(m.Text).Contains("到时见")),
            "Adjacent separate incoming bubbles remain separate messages");
        Check(compactWatch.ElapsedMilliseconds < 5000, "Small-text OCR finishes within bounded recovery budget");
        Check(MessageParser.NormalizeText("明 天 7 点， A I test") == "明天7点， A I test", "CJK spacing normalization preserves English word spaces");
        var groupOcr = await ocr.RecognizeAsync(groupScene, NormalizedRegion.Full);
        var groupMessages = MessageParser.Parse(groupScene, NormalizedRegion.Full, groupOcr);
        Console.WriteLine("Synthetic group diagnostic: " + string.Join(" | ", groupMessages.Select(m => m.Speaker + ":" + m.Text)));
        Check(groupMessages.Count == 4 && groupMessages.Count(m => m.Speaker == "unknown") == 1
            && !groupMessages.Any(m => Compact(m.Text).Contains("周末吃饭") || Compact(m.Text).Contains("出行计划")),
            "Small gray group nicknames with bubble geometry are excluded from messages");
        Check(groupMessages.Any(m => m.Speaker == "other" && Compact(m.Text).Contains("我周末有空"))
            && groupMessages.Any(m => m.Speaker == "other" && Compact(m.Text).Contains("我也有空")),
            "Both group members keep incoming roles after nickname removal");
        Check(groupMessages.Count(m => m.Speaker == "other"
            && (Compact(m.Text).Contains("我周末有空") || Compact(m.Text).Contains("我也有空"))) == 2,
            "Two consecutive members remain two separate message records");
        Check(groupMessages.Any(m => m.Speaker == "unknown" && Compact(m.Text).Contains("灰色说明")),
            "Ordinary gray text without nickname geometry is preserved as unknown");
        Check(groupMessages.Any(m => m.Speaker == "other" && Compact(m.Text).StartsWith("小字消息")),
            "Small gray real bubble text is not deleted as a nickname");
        var canonicalGroupOcr = groupOcr with { Lines = groupOcr.Lines.Select(line => line.Y < 35
            ? line with { Text = "周末吃饭团" } : line.Y is > 105 and < 125 ? line with { Text = "出行计划" } : line).ToArray() };
        Check(MessageParser.Parse(groupScene, NormalizedRegion.Full, canonicalGroupOcr)
            .All(m => m.Text != "周末吃饭团" && m.Text != "出行计划"),
            "Nickname filter is based on geometry even when nickname text contains arrangement keywords");

        await RunRecommendationPipelineChecks(ocr);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await ExpectAsync<OperationCanceledException>(() => ocr.RecognizeAsync(frame, NormalizedRegion.Full,
            cancelled.Token), "Cancelled OCR never publishes result");
        await ExpectAsync<ArgumentOutOfRangeException>(() => ocr.RecognizeAsync(frame,
            new NormalizedRegion(-.1, 0, 1, 1)), "Reject ROI outside source");
        await ExpectAsync<ArgumentException>(() => ocr.RecognizeAsync(frame with { Bgra = new byte[4] },
            NormalizedRegion.Full), "Reject malformed pixel buffer");
        using (var device = Direct3DDeviceFactory.Create()) Check(device is not null, "Create D3D11 device without screen capture");
        using (var capture = new WindowCaptureService())
        {
            Check(!capture.IsRunning && capture.SourceName == "", "Capture is stopped by default");
            bool rejected = false;
            try { capture.Start(); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Capture cannot start without user picker");
            capture.Stop(); capture.Stop();
            Check(!capture.IsRunning, "Stop is idempotent");
        }
        Console.WriteLine($"PLATFORM_CHECK_PASS checks={_checks} language={full.Language} lines={full.Lines.Count}");
    }

    private static async Task RunRecommendationPipelineChecks(LocalOcrService ocr)
    {
        async Task<GroupRecommendation> AnalyzeScene(string title, params string[] messages)
        {
            var frame = SyntheticScene.Render(title, messages.Select(text => new ObservedMessage("other", text)).ToArray());
            var read = await ocr.RecognizeAsync(frame, SyntheticScene.Messages);
            var parsed = MessageParser.Parse(frame, SyntheticScene.Messages, read);
            var tracker = new ConversationTracker();
            var pending = tracker.ObserveCurrentFrame(parsed);
            Check(!pending.Snapshot.IsStable && GroupRecommendationEngine.Analyze(pending.Snapshot) is null,
                title + ": first OCR observation waits for stabilization");
            var stable = tracker.ConfirmStableObservation(pending.Snapshot.Version).Snapshot;
            var result = GroupRecommendationEngine.Analyze(stable);
            Console.WriteLine($"Synthetic recommendation pipeline {title}: " +
                string.Join(" | ", parsed.Select(message => message.Text)));
            Check(stable.IsStable && result is not null && !result.IsCancelled,
                title + ": stable OCR viewport produces a recommendation");
            return result!;
        }

        var dining = await AnalyzeScene("周末聚餐群", "周六晚上一起聚餐吧，我们四个人。",
            "想在朝阳区找一家适合聊天的餐厅。", "人均预算一百元，有人不能吃辣。");
        Check(dining.Intent == "吃饭", "Dining pipeline identifies shared dining intent");
        Check(dining.Facts.Any(f => f.Label == "预算" && Regex.IsMatch(f.Value, "一百|100")),
            "Dining pipeline preserves the stated budget");
        Check(dining.Facts.Any(f => f.Label == "饮食偏好" && f.Value.Contains("不能吃辣"))
            && dining.Facts.Any(f => f.Label == "地点" && f.Value.Contains("朝阳区")),
            "Dining pipeline preserves location and negative dietary constraint");
        Check(dining.Missing.Count == 0 && dining.Options.Count is >= 2 and <= 3
            && dining.Options.Any(option => option.Title.Contains("忌口")),
            "Dining pipeline has complete fixture conditions and comparable restriction-aware options");

        var travel = await AnalyzeScene("周末出行群", "我们四个人明天从北京去天津玩。",
            "预算每人三百元，优先高铁，尽量少换乘。");
        Check(travel.Intent == "出行", "Travel pipeline identifies shared travel intent");
        Check(travel.Facts.Any(f => f.Label == "预算" && Regex.IsMatch(f.Value, "三百|300")),
            "Travel pipeline preserves the stated per-person budget");
        Check(travel.Facts.Any(f => f.Label == "出发地" && f.Value == "北京")
            && travel.Facts.Any(f => f.Label == "目的地" && f.Value == "天津")
            && travel.Facts.Any(f => f.Label == "交通偏好" && f.Value.Contains("高铁") && f.Value.Contains("少换乘")),
            "Travel pipeline preserves route and transfer preference");
        Check(travel.Missing.Count == 0 && travel.Options.Count is >= 2 and <= 3
            && travel.Options.Any(option => option.Title.Contains("直达")),
            "Travel pipeline has complete fixture conditions and comparable direct-route options");
    }

    private static async Task RunPumpChecks()
    {
        // No graphics devices, windows or real user data: fake disposable frames only.
        var delivered = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var times = new System.Collections.Concurrent.ConcurrentQueue<long>();
        var timestamps = new System.Collections.Concurrent.ConcurrentQueue<DateTimeOffset>();
        var firstSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lastSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new FakeItem(1); var replaced = new FakeItem(2); var last = new FakeItem(3);
        using (var pump = new LatestItemPump<FakeItem>(TimeSpan.FromMilliseconds(140), (item, _) =>
        {
            delivered.Enqueue(item.Id); times.Enqueue(Environment.TickCount64); timestamps.Enqueue(item.CapturedAt);
            if (item.Id == 1) firstSeen.TrySetResult(); if (item.Id == 3) lastSeen.TrySetResult();
            return Task.CompletedTask;
        }))
        {
            pump.Offer(first); await firstSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
            pump.Offer(replaced); await Task.Delay(10); pump.Offer(last);
            await lastSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
            pump.Dispose(); await pump.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Check(delivered.SequenceEqual(new[] { 1, 3 }), "Pump delivers trailing latest frame without another arrival");
        Check(replaced.Disposals == 1 && first.Disposals == 1 && last.Disposals == 1,
            "Pump releases replaced and consumed frames exactly once");
        Check(times.Last() - times.First() >= 110, "Pump coalesces bursts within copy-rate limit");
        Check(timestamps.Last() == last.CapturedAt, "Delayed frame retains original source timestamp");

        int pendingDelivered = 0;
        firstSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new FakeItem(5);
        using (var pump = new LatestItemPump<FakeItem>(TimeSpan.FromSeconds(10), (item, _) =>
        {
            if (item.Id == 4) firstSeen.TrySetResult(); else Interlocked.Increment(ref pendingDelivered);
            return Task.CompletedTask;
        }))
        {
            pump.Offer(new FakeItem(4)); await firstSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
            pump.Offer(pending); pump.Dispose(); await pump.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            var rejected = new FakeItem(6);
            Check(!pump.Offer(rejected) && rejected.Disposals == 1, "Stopped pump rejects and releases newly offered frame");
        }
        Check(pending.Disposals == 1 && pendingDelivered == 0, "Stop cancels throttle and releases pending final frame");

        var busy = new FakeItem(7);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int cancelErrors = 0;
        using (var pump = new LatestItemPump<FakeItem>(TimeSpan.Zero, async (_, token) =>
        {
            started.TrySetResult(); await Task.Delay(TimeSpan.FromSeconds(10), token);
        }, _ => Interlocked.Increment(ref cancelErrors)))
        {
            pump.Offer(busy); await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            pump.Dispose(); await pump.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Check(busy.Disposals == 1 && cancelErrors == 0, "Stop cancels cooperative in-flight work without failure notification");

        started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failedItem = new FakeItem(8); var afterFailure = new FakeItem(9); int errors = 0;
        using (var pump = new LatestItemPump<FakeItem>(TimeSpan.Zero, async (_, _) =>
        {
            started.TrySetResult(); await releaseFailure.Task; throw new InvalidOperationException("Synthetic copy failure");
        }, _ => Interlocked.Increment(ref errors)))
        {
            pump.Offer(failedItem); await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            pump.Offer(afterFailure); releaseFailure.TrySetResult();
            await pump.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            Check(!pump.Offer(new FakeItem(10)), "Failed pump exits and rejects further frames");
        }
        Check(errors == 1 && failedItem.Disposals == 1 && afterFailure.Disposals == 1,
            "Copy error reports once and releases in-flight plus pending frames");
    }

    private sealed class FakeItem(int id) : IDisposable
    {
        public int Id { get; } = id;
        public DateTimeOffset CapturedAt { get; } = DateTimeOffset.UnixEpoch.AddSeconds(id);
        private int _disposals;
        public int Disposals => Volatile.Read(ref _disposals);
        public void Dispose() => Interlocked.Increment(ref _disposals);
    }

    private static void TestRecommendedRegions()
    {
        foreach (var (width, height, sidebar, header, input) in new[] {
            (1000, 700, 300, 76, 560), (860, 680, 280, 70, 510), (1400, 900, 360, 96, 720) })
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(245, 245, 245)), null, new Rect(0, 0, width, height));
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(225, 229, 226)), null, new Rect(0, 0, sidebar, height));
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(233, 233, 233)), null, new Rect(sidebar, 0, width - sidebar, header));
                dc.DrawRectangle(Brushes.White, null, new Rect(sidebar, input, width - sidebar, height - input));
                DrawSmall(dc, "测试会话", sidebar + 25, 28, 22);
                dc.DrawRoundedRectangle(Brushes.White, null, new Rect(sidebar + 40, header + 40, 260, 45), 6, 6);
                DrawSmall(dc, "今晚几点吃饭？", sidebar + 50, header + 50, 19);
            }
            var frame = RenderFrame(visual, width, height);
            var layout = ChatLayoutEstimator.Estimate(frame);
            var body = layout.Messages.ToPixels(width, height);
            var heading = layout.Header.ToPixels(width, height);
            Check(layout.HasDetectedStructure, $"Recommend layout from continuous sidebar boundary ({width}x{height})");
            Check(body.X >= sidebar && body.Y >= header && body.Y + body.Height < input,
                "Recommended message ROI excludes contacts, heading and input");
            Check(heading.X >= sidebar && heading.Y < 28 && heading.Y + heading.Height >= 50,
                "Recommended title ROI contains synthetic conversation title");
        }
    }

    private static CaptureFrame RenderCompactBubbles()
    {
        const int w = 820, h = 460;
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(245, 245, 245)), null, new Rect(0, 0, w, h));
            Bubble("明天七点吃饭", 30, 30, 210, false, 16);
            Bubble("好的", 700, 30, 72, true, 16);
            Bubble("这条比较长的消息仍然是我发送的。", 180, 95, 590, true, 18);
            Bubble("12:30", 30, 170, 100, false, 18);
            DrawSmall(dc, "以下为新消息", 340, 232, 16);
            Bubble("收到", 30, 285, 90, false, 16);
            Bubble("到时见", 30, 330, 100, false, 16);
            void Bubble(string text, int x, int y, int width, bool self, int font)
            {
                dc.DrawRoundedRectangle(self ? new SolidColorBrush(Color.FromRgb(149, 236, 105)) : Brushes.White,
                    null, new Rect(x, y, width, 39), 5, 5);
                DrawSmall(dc, text, x + 12, y + 8, font);
            }
        }
        return RenderFrame(v, w, h);
    }
    private static CaptureFrame RenderGroupBubbles()
    {
        const int w = 820, h = 400;
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(245, 245, 245)), null, new Rect(0, 0, w, h));
            Gray("周末吃饭团", 48, 20);
            dc.DrawRoundedRectangle(Brushes.White, null, new Rect(30, 38, 290, 45), 5, 5);
            DrawSmall(dc, "我周末有空。", 48, 45, 22);
            Gray("出行计划", 48, 110);
            dc.DrawRoundedRectangle(Brushes.White, null, new Rect(30, 128, 340, 45), 5, 5);
            DrawSmall(dc, "我也有空，一起商量。", 48, 135, 22);
            Gray("这是一段灰色说明。", 360, 210);
            dc.DrawRoundedRectangle(Brushes.White, null, new Rect(30, 258, 290, 42), 5, 5);
            Gray("小字消息也要保留。", 48, 270);
            void Gray(string value, double x, double y)
                => dc.DrawText(new FormattedText(value, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                    new Typeface("Microsoft YaHei UI"), 13, Brushes.DimGray, 1), new Point(x, y));
        }
        return RenderFrame(v, w, h);
    }
    private static CaptureFrame RenderFrame(DrawingVisual visual, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
        return new CaptureFrame(width, height, pixels, "Synthetic regression fixture", DateTimeOffset.UtcNow);
    }
    private static void DrawSmall(DrawingContext dc, string text, double x, double y, int size)
        => dc.DrawText(new FormattedText(text, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"), size, Brushes.Black, 1), new Point(x, y));

    private static CaptureFrame RenderSyntheticFrame()
    {
        const int width = 1600, height = 600;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            Draw(dc, "周六晚上一起吃饭？", 45, 80);
            Draw(dc, "八点以后才有空。", 920, 250);
            Draw(dc, "七点在老地方见？", 45, 420);
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return new CaptureFrame(width, height, pixels, "Synthetic Chinese OCR fixture", DateTimeOffset.UtcNow);
    }

    private static CaptureFrame RenderBubbleFrame()
    {
        const int width = 2000, height = 1200;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(232, 232, 232)), null,
                new Rect(0, 0, width, height));
            dc.PushTransform(new TranslateTransform(200, 120));
            dc.DrawRoundedRectangle(Brushes.White, null, new Rect(40, 50, 700, 90), 8, 8);
            DrawBubble(dc, "周六晚上一起吃饭？", 65, 70, Brushes.Black);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(149, 236, 105)), null,
                new Rect(900, 190, 650, 90), 8, 8);
            DrawBubble(dc, "我八点以后才有空。", 925, 210, Brushes.Black);
            dc.DrawRoundedRectangle(Brushes.White, null, new Rect(40, 330, 720, 145), 8, 8);
            DrawBubble(dc, "明天晚上在三里屯。", 65, 350, Brushes.Black);
            DrawBubble(dc, "我们一共三个人。", 65, 405, Brushes.Black);
            DrawBubble(dc, "系统提示", 730, 540, Brushes.DimGray);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(35, 35, 35)), null,
                new Rect(40, 640, 720, 100), 8, 8);
            DrawBubble(dc, "深色模式需要人工确认。", 65, 660, Brushes.White);
            DrawBubble(dc, "12:30", 730, 820, Brushes.DimGray);
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return new CaptureFrame(width, height, pixels, "Synthetic bubbles with offset ROI", DateTimeOffset.UtcNow);
    }

    private static void DrawBubble(DrawingContext dc, string text, double x, double y, Brush brush)
    {
        var formatted = new FormattedText(text, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"), 40, brush, 1);
        dc.DrawText(formatted, new Point(x, y));
    }

    private static void Draw(DrawingContext dc, string text, double x, double y)
    {
        var formatted = new FormattedText(text, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"), 48, Brushes.Black, 1);
        dc.DrawText(formatted, new Point(x, y));
    }

    private static string Compact(string value) => Regex.Replace(value, @"\s+", "");
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        _checks++; Console.WriteLine("PASS: " + name);
    }
    private static async Task ExpectAsync<T>(Func<Task<OcrSnapshot>> operation, string name) where T : Exception
    {
        try { await operation(); }
        catch (T) { Check(true, name); return; }
        throw new InvalidOperationException("FAIL: " + name);
    }
}
