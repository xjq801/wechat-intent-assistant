using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ChatAssistant.Core;
using ChatAssistant.Platform;
using ChatAssistant.Services;

namespace ChatAssistant;

public partial class MainWindow
{
    readonly RecommendationCoordinator recommendations = new();
    readonly DispatcherTimer recommendationClock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly HashSet<string> attemptedAutomatic = new(StringComparer.Ordinal);
    CaptureFrame? latestProcessedFrame;
    long candidateVersion = -1, displayedVersion = -1;
    DateTimeOffset candidateSince, lastAutomaticCall = DateTimeOffset.MinValue;
    string displayedKey = "", activeAutomaticKey = "", lastObservedKey = "";
    bool cloudInFlight;
    IReadOnlyList<RecommendationOption> displayedOptions = [];
    bool FrameWorkPending => running && calibrated && (processing || queuedOcrFrame is not null ||
        (lastFrame is not null && !ReferenceEquals(lastFrame, latestProcessedFrame)));

    void RecommendationTick()
    {
        if (closed || editDirty) return;
        if (FrameWorkPending) return;
        var now = DateTimeOffset.UtcNow;
        // A static WGC source may emit only one frame. Confirm an unchanged observation after
        // a quiet interval, without relabelling the same image as a second captured frame.
        if (running && !processing && latestProcessedFrame is not null &&
            ReferenceEquals(latestProcessedFrame, lastFrame) && candidateVersion >= 0 &&
            now - candidateSince >= TimeSpan.FromMilliseconds(600))
        {
            var update = tracker.ConfirmStableObservation(candidateVersion);
            candidateVersion = -1;
            if (update.Changed) RenderUpdate(update);
        }
        if (AutoCheck.IsChecked != true) return;
        ApplyRecommendationUpdate(recommendations.Tick(now));
        TryAutomaticCloud();
    }

    void AutoChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        CancelAi();
        if (AutoCheck.IsChecked != true)
        {
            recommendations.Reset();
            ClearRecommendation("自动推荐已暂停，可点击“立即分析当前讨论”。");
            return;
        }
        if (editDirty || !Stable(tracker.Snapshot)) return;
        var snapshot = BoundedSnapshot();
        ApplyRecommendationUpdate(recommendations.Refresh(snapshot, DateTimeOffset.UtcNow));
        TryAutomaticCloud();
    }

    void AutoCloudChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        CancelAi();
        if (AutoCloudCheck.IsChecked == true)
        {
            attemptedAutomatic.Clear();
            TraceText.Text = "已开启自动千问：只发送表格内群聊文字，新需求稳定后细化方案；同一组条件不重复调用。";
            TryAutomaticCloud();
        }
        else
        {
            TraceText.Text = "自动千问已关闭。本地自动推荐仍可使用。";
            if (recommendations.Current is { } current && !recommendations.IsPending) RenderLocalRecommendation(current);
        }
    }

    static bool Stable(ConversationSnapshot snapshot) => snapshot.IsStable || snapshot.CanSuggest;
    ConversationSnapshot BoundedSnapshot() => tracker.Snapshot with
        { Messages = AssistantService.PrepareMessages(tracker.Snapshot.Messages) };

    void ApplyRecommendationUpdate(RecommendationUpdate update)
    {
        if (lastObservedKey != tracker.Snapshot.Key)
        {
            lastObservedKey = tracker.Snapshot.Key;
            attemptedAutomatic.Clear();
            displayedVersion = -1;
            ClearRecommendation("正在读取新的群聊，旧群的方案已清空。");
        }
        var current = update.Recommendation;
        if (recommendations.IsPending)
        {
            RecommendationStatus.Text = update.Status;
            CopyAllButton.IsEnabled = false;
            OptionList.IsEnabled = false;
            IgnoreButton.IsEnabled = false;
            if (current == null && displayedOptions.Count == 0)
                RecommendationSummary.Text = "正在合并时间、地点、预算和偏好，避免每句话都打断讨论。";
            return;
        }
        if (current == null)
        {
            if (update.Changed || displayedVersion < 0 || displayedKey != tracker.Snapshot.Key)
                ClearRecommendation(update.Status);
            return;
        }
        if (update.Changed || displayedVersion != current.Version || displayedKey != current.ConversationKey)
            RenderLocalRecommendation(current);
    }

    void ClearRecommendation(string status)
    {
        displayedOptions = [];
        displayedVersion = tracker.Snapshot.Version;
        displayedKey = tracker.Snapshot.Key;
        RecommendationTitle.Text = "观察群聊，等待合适的推荐时机";
        RecommendationStatus.Text = status;
        RecommendationSummary.Text = "出现聚餐或出行需求后，会汇总大家提到的条件，给出可比较的方案。";
        FactList.ItemsSource = null;
        OptionList.ItemsSource = null;
        EvidenceList.ItemsSource = null;
        MissingText.Text = "";
        RecommendationSource.Text = "";
        CopyAllButton.IsEnabled = false;
        IgnoreButton.IsEnabled = false;
    }

    void RenderLocalRecommendation(GroupRecommendation recommendation)
    {
        displayedKey = recommendation.ConversationKey;
        displayedVersion = recommendation.Version;
        displayedOptions = recommendation.Options;
        RecommendationTitle.Text = recommendation.Title;
        RecommendationStatus.Text = "已汇总当前讨论 · 方案方向，尚未查询实时商家或票务";
        RecommendationSummary.Text = recommendation.Summary;
        FactList.ItemsSource = recommendation.Facts;
        MissingText.Text = recommendation.Missing.Count > 0
            ? "还可补充：" + string.Join("、", recommendation.Missing) : "主要条件已出现，仍需确认群内是否达成一致。";
        OptionList.ItemsSource = displayedOptions;
        OptionList.IsEnabled = true;
        EvidenceList.ItemsSource = recommendation.Evidence;
        RecommendationSource.Text = recommendation.Source + "。以下是形成推荐的原文线索，提及的条件不代表所有群成员已同意。";
        CopyAllButton.IsEnabled = displayedOptions.Count > 0;
        IgnoreButton.IsEnabled = true;
    }

    bool ReadyForRecommendations()
    {
        MessageGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        MessageGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (editDirty && !ApplyCorrections()) return false;
        if (FrameWorkPending) { StatusText.Text = "正在读取最新群聊画面，请稍候。"; return false; }
        if (Stable(tracker.Snapshot) && tracker.Snapshot.Messages.Count > 0) return true;
        StatusText.Text = "请先识别或粘贴群聊文字，画面稳定后即可分析。";
        MessageGrid.BringIntoView();
        return false;
    }

    void AnalyzeNow(object sender, RoutedEventArgs e)
    {
        if (!ReadyForRecommendations()) return;
        CancelAi();
        ApplyRecommendationUpdate(recommendations.Refresh(BoundedSnapshot(), DateTimeOffset.UtcNow));
        RecommendationCard.BringIntoView();
        TryAutomaticCloud();
    }

    async void RequestRecommendation(object sender, RoutedEventArgs e)
    {
        if (!ReadyForRecommendations()) return;
        ApplyRecommendationUpdate(recommendations.Refresh(BoundedSnapshot(), DateTimeOffset.UtcNow));
        await GenerateCloudRecommendation(false);
    }

    void TryAutomaticCloud()
    {
        if (AutoCheck.IsChecked != true || AutoCloudCheck.IsChecked != true || assistant == null ||
            cloudInFlight || editDirty || FrameWorkPending || recommendations.IsPending) return;
        if (recommendations.Current is not { } current || recommendations.StableSnapshot is not { } snapshot ||
            !Stable(snapshot) || snapshot.Version != tracker.Snapshot.Version) return;
        string key = current.ConversationKey + "|" + current.Fingerprint;
        if (attemptedAutomatic.Contains(key) || DateTimeOffset.UtcNow - lastAutomaticCall < TimeSpan.FromSeconds(30)) return;
        _ = GenerateCloudRecommendation(true);
    }

    async Task GenerateCloudRecommendation(bool automatic)
    {
        if (assistant == null) { TraceText.Text = "千问服务未配置，已保留本地推荐。"; return; }
        if (closed || editDirty || FrameWorkPending || !Stable(tracker.Snapshot)) return;
        var snapshot = BoundedSnapshot();
        var current = recommendations.Current;
        if (automatic && (recommendations.IsPending || current == null ||
            recommendations.StableSnapshot?.Version != snapshot.Version)) return;
        CancelAi();
        var serial = aiSerial;
        var token = aiCts.Token;
        // Manual requests also consume this condition's automatic slot. Cancellation does not
        // promise the remote provider stopped processing, so only an explicit click retries it.
        if (current != null)
        {
            activeAutomaticKey = current.ConversationKey + "|" + current.Fingerprint;
            attemptedAutomatic.Add(activeAutomaticKey);
            lastAutomaticCall = DateTimeOffset.UtcNow;
        }
        cloudInFlight = true;
        ReplyButton.IsEnabled = false;
        RecommendationStatus.Text = "千问正在根据群聊细化方案；本地方案仍可查看。";
        if (!automatic) RecommendationCard.BringIntoView();
        try
        {
            var result = await assistant.RecommendAsync(snapshot, token);
            // Let an already-arrived frame update the conversation identity before accepting
            // a cloud response. OCR usually finishes quickly; never display the old group meanwhile.
            var waitStarted = DateTimeOffset.UtcNow;
            while (FrameWorkPending && Current(snapshot, serial) && DateTimeOffset.UtcNow - waitStarted < TimeSpan.FromSeconds(5))
                await Task.Delay(50, token);
            if (!Current(snapshot, serial)) return;
            if (FrameWorkPending)
            {
                RecommendationStatus.Text = "最新画面还在识别，本次千问结果未采用。";
                return;
            }
            if (result.Warning.Length > 0)
            {
                RecommendationStatus.Text = "千问本次未完成，已保留本地方案。";
                TraceText.Text = result.Warning;
                return;
            }
            if (result.Options.Count == 0)
            {
                recommendations.IgnoreCurrent();
                ClearRecommendation("千问判断当前不需要新增推荐");
                RecommendationSummary.Text = result.Summary;
                RecommendationSource.Text = result.Source;
                return;
            }
            displayedKey = snapshot.Key;
            displayedVersion = snapshot.Version;
            displayedOptions = result.Options.Select(o => new RecommendationOption(o.Title, o.Description, o.TradeOff)).ToArray();
            RecommendationTitle.Text = current?.Title ?? "群聊方案建议";
            RecommendationSummary.Text = result.Summary;
            RecommendationStatus.Text = "千问已细化 · 具体商家、班次及价格仍需核实";
            MissingText.Text = result.Missing.Count > 0 ? "还需确认：" + string.Join("、", result.Missing) : "";
            FactList.ItemsSource = current?.Facts;
            OptionList.ItemsSource = displayedOptions;
            OptionList.IsEnabled = true;
            EvidenceList.ItemsSource = current?.Evidence ?? snapshot.Messages.TakeLast(8).Select(m => new RecommendationEvidence(m.Id, m.Text)).ToArray();
            RecommendationSource.Text = result.Source + "。群成员可能有不同意见；方案不代表全员同意。";
            CopyAllButton.IsEnabled = true;
            IgnoreButton.IsEnabled = current != null;
            TraceText.Text = $"{result.Source} · {result.DurationMs} ms";
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (Current(snapshot, serial))
            {
                RecommendationStatus.Text = "千问暂时不可用，已保留本地方案。";
                TraceText.Text = "请求未完成：" + error.GetType().Name;
            }
        }
        finally
        {
            if (serial == aiSerial)
            {
                activeAutomaticKey = "";
                cloudInFlight = false;
                ReplyButton.IsEnabled = assistant != null;
            }
        }
    }

    bool Current(ConversationSnapshot snapshot, int serial) =>
        !closed && !editDirty && serial == aiSerial && snapshot.Version == tracker.Snapshot.Version &&
        snapshot.Key == tracker.Snapshot.Key && Stable(tracker.Snapshot);

    void CancelAi()
    {
        activeAutomaticKey = "";
        cloudInFlight = false;
        aiSerial++;
        aiCts.Cancel();
        aiCts.Dispose();
        aiCts = new();
        if (ReplyButton != null) ReplyButton.IsEnabled = assistant != null;
    }

    bool CanCopyRecommendation()
    {
        if (!editDirty && !FrameWorkPending && Stable(tracker.Snapshot) && !recommendations.IsPending &&
            displayedVersion == tracker.Snapshot.Version && displayedKey == tracker.Snapshot.Key &&
            displayedOptions.Count > 0) return true;
        StatusText.Text = "讨论条件正在变化，请等待推荐更新后再复制。";
        return false;
    }

    static string OptionText(RecommendationOption option) =>
        option.Title + "\n" + option.Reason + "\n取舍：" + option.Tradeoff;

    void CopyOption(object sender, RoutedEventArgs e)
    {
        if (!CanCopyRecommendation() || sender is not Button { Tag: RecommendationOption option } ||
            !displayedOptions.Contains(option)) return;
        CopyText(OptionText(option) + "\n（方案方向，具体商家、班次与价格需核实。）");
    }

    void CopyRecommendation(object sender, RoutedEventArgs e)
    {
        if (!CanCopyRecommendation()) return;
        CopyText(RecommendationTitle.Text + "\n" + RecommendationSummary.Text + "\n\n" +
            string.Join("\n\n", displayedOptions.Select(OptionText)) + "\n" + MissingText.Text +
            "\n（方案方向，具体商家、班次与价格需核实。）");
    }

    void CopyText(string text)
    {
        try { Clipboard.SetText(text); StatusText.Text = "推荐已复制，可以自行分享到群里。"; }
        catch { StatusText.Text = "剪贴板暂不可用，请稍后再试。"; }
    }

    void DismissRecommendation(object sender, RoutedEventArgs e)
    {
        CancelAi();
        ApplyRecommendationUpdate(recommendations.IgnoreCurrent());
    }
}
