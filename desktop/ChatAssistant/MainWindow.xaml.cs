using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using ChatAssistant.Core;
using ChatAssistant.Platform;
using ChatAssistant.Recognition;
using ChatAssistant.Services;
using Microsoft.Win32;

namespace ChatAssistant;

public partial class MainWindow : Window
{
    readonly WindowCaptureService capture = new();
    readonly LocalOcrService ocr = new();
    readonly ConversationTracker tracker = new();
    readonly SessionIdentityResolver identities = new();
    readonly ObservableCollection<EditableMessage> rows = [];
    readonly DispatcherTimer demoTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer watchdog = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly string[] args;
    AssistantService? assistant;
    CaptureFrame? lastFrame;
    (CaptureFrame Frame, int Epoch)? queuedOcrFrame;
    TaskCompletionSource<CaptureFrame>? firstFrame;
    NormalizedRegion headerRegion = SyntheticScene.Header, messageRegion = SyntheticScene.Messages;
    CancellationTokenSource frameCts = new(), aiCts = new();
    int epoch, aiSerial;
    long shownVersion = -1;
    bool running, calibrated, demo, processing, updatingRows, editDirty, closed;
    bool forceFrameOnly, currentFrameOnly, demoMissingTitle, waitingForFrames;
    string sessionDisplay = "当前群聊", isolatedSessionKey = "", editingKey = "";
    string demoName = "周末聚餐群";
    readonly List<ObservedMessage> demoMessages = [];
    DateTimeOffset lastFrameAt = DateTimeOffset.UtcNow;

    public MainWindow(string[] args)
    {
        this.args = args;
        InitializeComponent();
        MessageGrid.ItemsSource = rows;
        RoleColumn.ItemsSource = new[] { "我", "群友", "未标注" };
        capture.FrameAvailable += OnFrame;
        capture.SourceClosed += () => PostCaptureStop("聊天窗口已关闭，请重新选择。");
        capture.SourceResized += () => PostCaptureStop("窗口尺寸变化，请重新选择窗口并确认识别范围。");
        capture.CaptureFailed += message => PostCaptureStop("采集失败：" + message);
        demoTimer.Tick += (_, _) => { if (running && demo) OnFrame(DemoFrame()); };
        watchdog.Tick += (_, _) =>
        {
            if (running && calibrated && !waitingForFrames && (DateTimeOffset.UtcNow - lastFrameAt).TotalSeconds > 15)
            {
                waitingForFrames = true;
                StatusText.Text = "画面暂未更新；继续观察中，当前推荐依据表格内的文字。";
            }
        };
        SystemEvents.SessionSwitch += OnSessionSwitch;
        ConfigureAssistant();
        recommendationClock.Tick += (_, _) => RecommendationTick();
    }

    void PostCaptureStop(string reason)
    {
        var current = epoch;
        Dispatcher.BeginInvoke(() =>
        {
            if (closed || current != epoch) return;
            calibrated = false;
            RegionButton.IsEnabled = false;
            StopAndReset(reason, clearFrame: true);
        });
    }

    void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock) PostCaptureStop("屏幕已锁定，采集已停止。");
    }

    string? Arg(string key)
    {
        var i = Array.IndexOf(args, key);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    void ConfigureAssistant()
    {
        var repo = "";
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "代码"))) { repo = d.FullName; break; }
        Dictionary<string, string> local = new();
        try
        {
            var path = Path.Combine(repo, ".local", "desktop-settings.json");
            if (repo.Length > 0 && File.Exists(path))
                local = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new();
        }
        catch { }
        var web = Arg("--web-root") ?? local.GetValueOrDefault("web_root") ?? (repo.Length > 0 ? Path.Combine(repo, "代码") : "");
        var python = Arg("--python") ?? local.GetValueOrDefault("python") ?? Path.Combine(web, ".venv", "Scripts", "python.exe");
        var bridge = Arg("--bridge-path") ?? local.GetValueOrDefault("bridge") ??
            (repo.Length > 0 ? Path.Combine(repo, "desktop", "bridge", "assistant_bridge.py") : Path.Combine(AppContext.BaseDirectory, "bridge", "assistant_bridge.py"));
        if (File.Exists(python) && Directory.Exists(web) && File.Exists(bridge))
        {
            assistant = new(new(python, web, bridge));
            TraceText.Text = "本地群聊推荐已就绪。千问仅在点击细化方案或开启自动千问后发送上方文字。";
        }
        else
        {
            TraceText.Text = "本地群聊推荐可用。千问服务未配置，请使用提供的启动脚本。";
            AutoCloudCheck.IsEnabled = false;
            ReplyButton.IsEnabled = false;
            ReplyButton.ToolTip = "千问服务未配置；本地群聊推荐仍可使用。";
        }
    }

    void WindowLoaded(object sender, RoutedEventArgs e)
    {
        var area = SystemParameters.WorkArea;
        Height = Math.Min(930, area.Height - 30);
        Left = area.Right - Width - 18;
        Top = area.Top + 15;
        watchdog.Start();
        recommendationClock.Start();
        if (args.Contains("--demo")) StartDemo(this, e);
    }

    void PinChanged(object sender, RoutedEventArgs e) => Topmost = PinCheck.IsChecked == true;



    void OnFrame(CaptureFrame frame)
    {
        var current = epoch;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (closed || current != epoch || !running) return;
            lastFrame = frame;
            lastFrameAt = frame.CapturedAt;
            waitingForFrames = false;
            firstFrame?.TrySetResult(frame);
            if (!calibrated || editDirty) return;
            queuedOcrFrame = (frame, current);
            // Reception only replaces the pending slot. Reprocessing never republishes an old
            // frame through the receive queue or changes its capture time.
            ProcessPendingFrame();
        });
    }

    async void ProcessPendingFrame()
    {
        if (processing || queuedOcrFrame is not { } pending) return;
        queuedOcrFrame = null;
        var frame = pending.Frame;
        var current = pending.Epoch;
        if (closed || current != epoch || !running || !calibrated || editDirty) return;
        processing = true;
        var token = frameCts.Token;
        try
        {
            var watch = Stopwatch.StartNew();
            var title = "";
            if (!forceFrameOnly)
            {
                var heading = await ocr.RecognizeAsync(frame, headerRegion, token);
                title = string.Join(" ", heading.Lines.OrderBy(l => l.Y).ThenBy(l => l.X).Select(l => l.Text));
                title = Regex.Replace(title, @"(?<=\p{IsCJKUnifiedIdeographs})\s+(?=\p{IsCJKUnifiedIdeographs})", "").Trim();
            }
            var body = await ocr.RecognizeAsync(frame, messageRegion, token);
            if (closed || current != epoch || token.IsCancellationRequested || editDirty) return;
            var identity = identities.Resolve(forceFrameOnly ? null : frame, forceFrameOnly ? null : headerRegion, title);
            if (identity.Changed) isolatedSessionKey = "";
            currentFrameOnly = identity.FrameOnly || (isolatedSessionKey.Length > 0 && isolatedSessionKey == identity.Key);
            sessionDisplay = currentFrameOnly ? "当前画面 · 不累计历史" : identity.DisplayName;
            var observed = MessageParser.Parse(frame, messageRegion, body);
            var update = currentFrameOnly ? tracker.ObserveCurrentFrame(observed) : tracker.Observe(identity.Key, observed);
            if (!update.Snapshot.CanSuggest && update.Snapshot.Status.Contains("跳变", StringComparison.Ordinal))
            {
                isolatedSessionKey = identity.Key;
                currentFrameOnly = true;
                sessionDisplay = "当前画面 · 不累计历史";
                update = tracker.ObserveCurrentFrame(observed);
            }
            latestProcessedFrame = frame;
            RenderUpdate(update);
            RecognitionText.Text = $"{observed.Count} 条可见消息 · 本地识字 {watch.ElapsedMilliseconds} ms" +
                (currentFrameOnly ? "\n仅使用当前画面；群名未识别也能汇总需求。" : "\n归属由气泡推断；未标注消息也可参与需求汇总。");
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (current == epoch)
            {
                StopAndReset("这次没有读到文字。可调整识别区域，或用“粘贴群聊”继续。");
                TraceText.Text = "本地识字：" + error.GetType().Name;
            }
        }
        finally
        {
            processing = false;
            // Called on the dispatcher: consume the latest slot directly, including a frame
            // from a new epoch that arrived while the old OCR operation was being cancelled.
            if (!closed && running && !editDirty) ProcessPendingFrame();
        }
    }


    void RenderUpdate(TrackerUpdate update)
    {
        var snapshot = update.Snapshot;
        if (shownVersion != snapshot.Version)
        {
            shownVersion = snapshot.Version;
            CancelAi();
            displayedVersion = -1;
        }
        var bounded = snapshot with { Messages = AssistantService.PrepareMessages(snapshot.Messages) };
        bool stable = snapshot.IsStable || snapshot.CanSuggest;
        ConversationText.Text = sessionDisplay;
        StatusText.Text = stable
            ? (demo ? "模拟群聊 · " : "") + (running ? "观察中 · " : "文字已就绪 · ") +
                (currentFrameOnly ? "根据当前可见内容汇总需求" : $"{bounded.Messages.Count} 条群聊消息")
            : snapshot.Status;
        if (!editDirty && update.Changed)
        {
            bool waiting = !stable && snapshot.Status.Contains("连续两帧", StringComparison.Ordinal);
            SetRows(waiting ? [] : bounded.Messages);
        }
        if (stable) candidateVersion = -1;
        else if (candidateVersion != snapshot.Version)
        {
            candidateVersion = snapshot.Version;
            candidateSince = DateTimeOffset.UtcNow;
        }
        var recommendationUpdate = recommendations.Observe(bounded, DateTimeOffset.UtcNow);
        if (AutoCheck.IsChecked == true) ApplyRecommendationUpdate(recommendationUpdate);
        else if (update.Changed) ClearRecommendation("自动推荐已暂停，可点击“立即分析当前讨论”。");
        ApplyButton.Visibility = editDirty ? Visibility.Visible : Visibility.Collapsed;
    }

    void SetRows(IReadOnlyList<ChatMessage> messages)
    {
        updatingRows = true;
        try
        {
            rows.Clear();
            foreach (var message in messages)
            {
                var row = new EditableMessage(message);
                row.Edited += MessageEdited;
                rows.Add(row);
            }
        }
        finally { updatingRows = false; }
    }

    async void SelectWindow(object sender, RoutedEventArgs e)
    {
        StopAndReset("正在选择窗口……", clearFrame: true);
        demo = false;
        demoTimer.Stop();
        DemoPanel.Visibility = Visibility.Collapsed;
        calibrated = false;
        RegionButton.IsEnabled = false;
        var selectedEpoch = epoch;
        try
        {
            var selected = await capture.SelectSourceAsync(new WindowInteropHelper(this).Handle);
            if (closed || selectedEpoch != epoch) return;
            if (!selected) { StatusText.Text = "已取消选择；也可以用“粘贴群聊”。"; return; }
            firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
            running = true;
            lastFrameAt = DateTimeOffset.UtcNow;
            capture.Start();
            PauseButton.IsEnabled = true;
            PauseButton.Content = "暂停观察";
            StatusText.Text = "已选择 " + capture.SourceName + "，正在准备识别范围……";
            var first = firstFrame.Task;
            var completed = await Task.WhenAny(first, Task.Delay(10000));
            if (closed || selectedEpoch != epoch) return;
            if (completed != first) { StopAndReset("未收到窗口画面，请确认窗口可见后重试。"); return; }
            await first;
            firstFrame = null;
            RegionButton.IsEnabled = true;
            Calibrate();
        }
        catch (Exception error)
        {
            if (selectedEpoch == epoch)
            {
                StopAndReset("无法读取所选窗口。可以重试，或粘贴聊天文字。");
                TraceText.Text = error.GetType().Name;
            }
        }
    }

    void SelectRegions(object sender, RoutedEventArgs e) { if (lastFrame != null && !demo) Calibrate(); }

    void Calibrate()
    {
        if (lastFrame == null) return;
        calibrated = false;
        InvalidateFrames();
        CancelAi();
        editDirty = false;
        editingKey = "";
        identities.Reset();
        isolatedSessionKey = "";
        RenderUpdate(tracker.Reset("请确认推荐的识别范围"));
        var selectedEpoch = epoch;
        var selection = new RegionDialog(lastFrame) { Owner = this };
        var confirmed = selection.ShowDialog() == true;
        if (closed || selectedEpoch != epoch) return;
        if (!confirmed) { StopAndReset("已取消范围确认，采集已暂停。"); return; }
        headerRegion = selection.HeaderRegion;
        messageRegion = selection.MessageRegion;
        forceFrameOnly = selection.CurrentFrameOnly;
        calibrated = true;
        running = true;
        try { if (!capture.IsRunning) capture.Start(); }
        catch { calibrated = false; StopAndReset("所选窗口已失效，请重新选择。", clearFrame: true); return; }
        lastFrameAt = DateTimeOffset.UtcNow;
        PauseButton.Content = "暂停观察";
        PauseButton.IsEnabled = true;
        StatusText.Text = "正在识字 · 文字稳定后自动汇总群聊需求";
        // The picker preview may be the only frame emitted by a static window.
        queuedOcrFrame = (lastFrame, epoch);
        ProcessPendingFrame();
    }

    void TogglePause(object sender, RoutedEventArgs e)
    {
        if (running) { StopAndReset("采集已暂停，旧上下文和建议已清空。"); return; }
        if (!calibrated) { StatusText.Text = "请先选择聊天窗口，或粘贴聊天。"; return; }
        if (editDirty && !ApplyCorrections()) return;
        InvalidateFrames();
        running = true;
        lastFrameAt = DateTimeOffset.UtcNow;
        if (demo) demoTimer.Start();
        else
        {
            try { capture.Start(); }
            catch { StopAndReset("窗口已失效，请重新选择。"); return; }
        }
        PauseButton.Content = "暂停观察";
        StatusText.Text = "已继续 · 等待稳定画面";
    }

    void InvalidateFrames()
    {
        epoch++;
        queuedOcrFrame = null;
        latestProcessedFrame = null;
        candidateVersion = -1;
        frameCts.Cancel(); frameCts.Dispose(); frameCts = new();
    }

    void StopAndReset(string reason, bool clearFrame = false)
    {
        running = false;
        demoTimer.Stop();
        capture.Stop();
        InvalidateFrames();
        CancelAi();
        identities.Reset();
        isolatedSessionKey = "";
        editingKey = "";
        editDirty = false;
        currentFrameOnly = false;
        sessionDisplay = "当前群聊";
        RenderUpdate(tracker.Reset(reason));
        if (clearFrame) lastFrame = null;
        candidateVersion = -1;
        latestProcessedFrame = null;
        StatusText.Text = reason;
        PauseButton.Content = "继续观察";
    }

    void Rebaseline(object sender, RoutedEventArgs e)
    {
        InvalidateFrames();
        CancelAi();
        editDirty = false;
        editingKey = "";
        identities.Reset();
        isolatedSessionKey = "";
        RenderUpdate(tracker.Reset("上下文已清空"));
        StatusText.Text = running ? "请把聊天滚到最新消息，正在重新识别。" : "已清空 · 可以继续采集，或粘贴新文字。";
    }

    void MessageEdited()
    {
        if (updatingRows || !IsLoaded) return;
        if (!editDirty)
        {
            editingKey = tracker.Snapshot.Key;
            running = false;
            demoTimer.Stop();
            capture.Stop();
            InvalidateFrames();
            PauseButton.Content = "继续观察";
        }
        editDirty = true;
        CancelAi();
        recommendations.Reset();
        ClearRecommendation("正在校对群聊文字");
        ApplyButton.Visibility = Visibility.Visible;
        StatusText.Text = "正在校对 · 采集已暂停；改完后可直接点击下方建议按钮。";
    }

    bool ApplyCorrections()
    {
        MessageGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        MessageGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var list = rows.Where(r => !string.IsNullOrWhiteSpace(r.Text))
            .Select(r => new ObservedMessage(r.Speaker, r.Text)).ToArray();
        if (list.Length == 0) { StatusText.Text = "请先粘贴或识别聊天文字。"; return false; }
        var key = editDirty ? editingKey : tracker.Snapshot.Key;
        if (string.IsNullOrWhiteSpace(key)) key = "手动文字-" + Guid.NewGuid().ToString("N");
        running = false;
        demoTimer.Stop();
        capture.Stop();
        InvalidateFrames();
        CancelAi();
        editDirty = false;
        RenderUpdate(tracker.ConfirmEdited(key, list));
        PauseButton.Content = "继续观察";
        StatusText.Text = "校对已采用 · 正在汇总群聊需求；也可点击立即分析。";
        return true;
    }

    void ConfirmTranscript(object sender, RoutedEventArgs e) => ApplyCorrections();

    void PasteChat(object sender, RoutedEventArgs e)
    {
        // Reading the clipboard happens only in response to this explicitly named user action.
        var text = "";
        try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); } catch { }
        var dialog = new PasteChatDialog(text) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        StopAndReset("正在使用粘贴文字", clearFrame: true);
        demo = false;
        calibrated = false;
        RegionButton.IsEnabled = false;
        PauseButton.IsEnabled = false;
        DemoPanel.Visibility = Visibility.Collapsed;
        sessionDisplay = "粘贴的群聊记录";
        currentFrameOnly = true;
        RenderUpdate(tracker.ConfirmEdited("paste-" + Guid.NewGuid().ToString("N"), dialog.Messages));
        RecognitionText.Text = "仅使用这段群聊；可直接校对文字，未标注归属不影响推荐。";
    }



    void StartDemo(object sender, RoutedEventArgs e)
    {
        StopAndReset("准备演示");
        demo = true;
        demoName = "周末聚餐群";
        demoMissingTitle = false;
        forceFrameOnly = false;
        demoMessages.Clear();
        demoMessages.Add(new("other", "周六晚上一起聚餐吧，我们四个人。"));
        demoMessages.Add(new("other", "想在朝阳区找一家适合聊天的餐厅。"));
        headerRegion = SyntheticScene.Header;
        messageRegion = SyntheticScene.Messages;
        calibrated = true;
        running = true;
        PauseButton.IsEnabled = true;
        PauseButton.Content = "暂停观察";
        RegionButton.IsEnabled = false;
        DemoPanel.Visibility = Visibility.Visible;
        demoTimer.Start();
        OnFrame(DemoFrame());
    }

    CaptureFrame DemoFrame()
    {
        var frame = SyntheticScene.Render(demoName, demoMessages);
        if (!demoMissingTitle) return frame;
        var roi = SyntheticScene.Header.ToPixels(frame.Width, frame.Height);
        for (int y = roi.Y; y < roi.Y + roi.Height; y++)
            for (int x = roi.X; x < roi.X + roi.Width; x++)
            {
                int i = (y * frame.Width + x) * 4;
                frame.Bgra[i] = frame.Bgra[i + 1] = frame.Bgra[i + 2] = 245;
                frame.Bgra[i + 3] = 255;
            }
        return frame;
    }

    void NextDemo(object sender, RoutedEventArgs e)
    {
        if (!demo || !running) return;
        demoMessages.Add(new("other", "人均预算一百元，有人不能吃辣。"));
        OnFrame(DemoFrame());
    }

    void SwitchDemo(object sender, RoutedEventArgs e)
    {
        if (!demo || !running) return;
        demoName = "周末出行群";
        demoMessages.Clear();
        demoMessages.Add(new("other", "我们四个人明天从北京去天津玩。"));
        demoMessages.Add(new("other", "预算每人三百元，优先高铁，尽量少换乘。"));
        OnFrame(DemoFrame());
    }

    void CancelDemo(object sender, RoutedEventArgs e)
    {
        if (!demo || !running) return;
        demoMessages.Add(new("other", "这次安排取消了，先不去了。"));
        OnFrame(DemoFrame());
    }

    void PreviewDemoRegions(object sender, RoutedEventArgs e)
    {
        if (!demo) return;
        new RegionDialog(DemoFrame()) { Owner = this }.ShowDialog();
    }

    void ToggleDemoTitle(object sender, RoutedEventArgs e)
    {
        if (!demo || !running) return;
        demoMissingTitle = !demoMissingTitle;
        OnFrame(DemoFrame());
    }

    void WindowClosed(object? sender, EventArgs e)
    {
        closed = true;
        running = false;
        demoTimer.Stop();
        watchdog.Stop();
        recommendationClock.Stop();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        capture.Dispose();
        InvalidateFrames();
        CancelAi();
        assistant?.Dispose();
        frameCts.Dispose();
        aiCts.Dispose();
        lastFrame = null;
    }

    public void PauseAfterError() { if (!closed) StopAndReset("操作异常，已暂停。", clearFrame: true); }
}
