using System.Text;

namespace ChatAssistant.Core;

/// <summary>
/// Accepts only two identical successive observations. It conservatively treats a discontinuity
/// as review-required; a frame without a reliable conversation key can never reuse prior context.
/// Call on one thread, or protect it with the application's dispatcher/lock.
/// </summary>
public sealed class ConversationTracker
{
    public const int MaximumMessages = 20;
    private string _key = "";
    private long _version;
    private long _nextId;
    private List<ObservedMessage> _frontier = [];
    private List<ObservedMessage> _candidate = [];
    private List<ChatMessage> _history = [];
    private string? _candidateFingerprint;
    private int _stableFrames;
    private bool _hasBaseline;
    private bool _requiresReview;
    private bool _frameOnly;

    public const string CurrentFrameKey = "当前画面";

    public ConversationSnapshot Snapshot { get; private set; } =
        new("", 0, Array.Empty<ChatMessage>(), false, "请选择聊天窗口和识别区域");

    public TrackerUpdate Observe(string? key, IEnumerable<ObservedMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        string normalizedKey = NormalizeKey(key);
        var observed = NormalizeMessages(messages);
        if (string.IsNullOrEmpty(normalizedKey))
        {
            if (_key.Length == 0 && Snapshot.Status == "未识别到会话标题，已暂停建议")
                return Unchanged();
            return Reset("未识别到会话标题，已暂停建议");
        }

        bool keyChanged = _frameOnly || normalizedKey != _key;
        if (keyChanged)
        {
            ClearState();
            _key = normalizedKey;
        }

        string fingerprint = Fingerprint(observed);
        if (keyChanged || fingerprint != _candidateFingerprint)
        {
            ++_version; // Invalidate in-flight replies before waiting for the second frame.
            _candidate = observed;
            _candidateFingerprint = fingerprint;
            _stableFrames = 1;
            bool discontinuity = _hasBaseline && Classify(observed).Kind == ChangeKind.Discontinuous;
            var visible = keyChanged || discontinuity ? Array.Empty<ChatMessage>() : _history.ToArray();
            Snapshot = new(_key, _version, visible, false,
                observed.Count == 0 ? "画面没有可确认的消息，已暂停建议" : "正在核对连续两帧，请稍候");
            return new(Snapshot, Array.Empty<ChatMessage>(), true, !_hasBaseline);
        }

        if (_stableFrames >= 2)
            return Unchanged();

        ++_stableFrames;
        if (observed.Count == 0)
        {
            Snapshot = new(_key, _version, Array.Empty<ChatMessage>(), false, "画面没有可确认的消息，已暂停建议");
            return new(Snapshot, Array.Empty<ChatMessage>(), true, !_hasBaseline);
        }

        if (!_hasBaseline)
        {
            _hasBaseline = true;
            _frontier = observed;
            _history = observed.Select(ToMessage).ToList();
            Publish(_history, "已建立当前画面基线，可手动获取建议");
            return new(Snapshot, Array.Empty<ChatMessage>(), true, true);
        }

        if (_requiresReview)
        {
            Snapshot = new(_key, _version, observed.Select(ToMessage).ToArray(), false,
                "画面内容发生跳变，请检查并确认识别内容后继续");
            return new(Snapshot, Array.Empty<ChatMessage>(), true, false);
        }

        var change = Classify(observed);
        if (change.Kind == ChangeKind.Discontinuous)
        {
            _requiresReview = true;
            Snapshot = new(_key, _version, observed.Select(ToMessage).ToArray(), false,
                "画面内容发生跳变，请检查并确认识别内容后继续");
            return new(Snapshot, Array.Empty<ChatMessage>(), true, false);
        }

        if (change.Kind == ChangeKind.Previous)
        {
            // Keep the latest frontier: returning from an older visible subset must not re-add it.
            Publish(_history, "检测到已识别内容，未新增消息");
            return new(Snapshot, Array.Empty<ChatMessage>(), true, false);
        }

        var addedRaw = observed.Skip(change.Offset).ToArray();
        var added = addedRaw.Select(ToMessage).ToArray();
        _frontier = _frontier.Concat(addedRaw).TakeLast(MaximumMessages).ToList();
        _history = _history.Concat(added).TakeLast(MaximumMessages).ToList();
        Publish(_history, $"已识别 {added.Length} 条新消息");
        return new(Snapshot, added, true, false);
    }

    /// <summary>
    /// When the title region cannot identify a conversation, analyze a stable visible viewport
    /// without carrying any previous messages across observations. A changed frame immediately
    /// invalidates replies, even when it overlaps almost all of the previous viewport.
    /// </summary>
    public TrackerUpdate ObserveCurrentFrame(IEnumerable<ObservedMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var observed = NormalizeMessages(messages);
        bool modeChanged = !_frameOnly;
        if (modeChanged)
        {
            ClearState();
            _frameOnly = true;
            _key = CurrentFrameKey;
        }

        string fingerprint = Fingerprint(observed);
        if (modeChanged || fingerprint != _candidateFingerprint)
        {
            ++_version;
            _candidate = observed;
            _candidateFingerprint = fingerprint;
            _stableFrames = 1;
            _history = [];
            _frontier = [];
            Snapshot = new(_key, _version, Array.Empty<ChatMessage>(), false,
                observed.Count == 0 ? "当前画面没有可确认的消息" : "当前画面模式 · 正在核对连续两帧");
            return new(Snapshot, Array.Empty<ChatMessage>(), true, true);
        }
        if (_stableFrames >= 2)
            return Unchanged();

        _stableFrames = 2;
        _hasBaseline = observed.Count > 0;
        _history = observed.Select(ToMessage).ToList();
        _frontier = observed;
        Publish(_history, "当前画面已稳定 · 仅使用此刻可见的消息");
        // Scrolling and contact switches cannot be distinguished without an identity anchor.
        // Never describe any line as a new message or use it to trigger a push reminder.
        return new(Snapshot, Array.Empty<ChatMessage>(), true, true);
    }

    public TrackerUpdate Reset(string reason = "已清空会话")
    {
        ++_version;
        ClearState();
        _key = "";
        Snapshot = new("", _version, Array.Empty<ChatMessage>(), false, reason);
        return new(Snapshot, Array.Empty<ChatMessage>(), true, true);
    }

    /// <summary>
    /// Confirm that the current OCR candidate remained unchanged over an application-observed
    /// quiet interval. This is time stability, not a claim that another physical frame arrived.
    /// The caller must verify capture generation and that no newer OCR work is pending.
    /// </summary>
    public TrackerUpdate ConfirmStableObservation(long expectedVersion)
    {
        if (expectedVersion != _version || _stableFrames != 1 || _candidateFingerprint is null)
            return Unchanged();
        return _frameOnly ? ObserveCurrentFrame(_candidate.ToArray()) : Observe(_key, _candidate.ToArray());
    }

    /// <summary>
    /// Explicit user correction establishes a baseline without triggering an automatic reminder.
    /// Preserve the latest raw OCR anchor so the next unchanged frame cannot undo those corrections.
    /// </summary>
    public TrackerUpdate ConfirmEdited(string key, IEnumerable<ObservedMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var normalizedKey = NormalizeKey(key);
        if (normalizedKey.Length == 0)
            return Reset("请先确认会话标题");

        var edited = NormalizeMessages(messages);
        bool keepFrameOnly = _frameOnly && _key == normalizedKey;
        var rawAnchor = _key == normalizedKey && _candidate.Count > 0 ? _candidate.ToList() : edited.ToList();
        ++_version;
        ClearState();
        _key = normalizedKey;
        _frameOnly = keepFrameOnly;
        _history = edited.Select(ToMessage).ToList();
        _frontier = rawAnchor;
        _candidate = rawAnchor;
        _candidateFingerprint = Fingerprint(rawAnchor);
        _stableFrames = 2;
        _hasBaseline = edited.Count > 0;
        Publish(_history, "已采用你确认的识别内容");
        return new(Snapshot, Array.Empty<ChatMessage>(), true, true);
    }

    private void Publish(IReadOnlyList<ChatMessage> messages, string status)
    {
        bool allowed = messages.Count > 0 && messages.All(m => m.Speaker is "self" or "other");
        Snapshot = new(_key, _version, messages.ToArray(), allowed,
            messages.Count == 0 ? "当前没有可确认的消息" :
            allowed ? status : "有消息无法确定说话人，请编辑确认后再获取建议")
            { IsStable = messages.Count > 0 };
    }

    private void ClearState()
    {
        _frontier = [];
        _candidate = [];
        _history = [];
        _candidateFingerprint = null;
        _stableFrames = 0;
        _hasBaseline = false;
        _requiresReview = false;
        _frameOnly = false;
    }

    private TrackerUpdate Unchanged() => new(Snapshot, Array.Empty<ChatMessage>(), false, false);

    private ChatMessage ToMessage(ObservedMessage message) =>
        new($"m{++_nextId}", message.Speaker, message.Text);

    private (ChangeKind Kind, int Offset) Classify(IReadOnlyList<ObservedMessage> current)
    {
        if (current.Count == 0 || _frontier.Count == 0)
            return (ChangeKind.Discontinuous, 0);

        // A subset of the known conversation is a scroll/reflow, never a new tail.
        if (ContainsSequence(_frontier, current) || EndsWith(current, _frontier))
            return (ChangeKind.Previous, current.Count);

        int maximum = Math.Min(_frontier.Count, current.Count);
        for (int length = maximum; length >= 1; --length)
        {
            bool match = true;
            for (int index = 0; index < length; ++index)
                if (!Same(_frontier[_frontier.Count - length + index], current[index]))
                {
                    match = false;
                    break;
                }
            if (!match) continue;

            // A lone short repeated acknowledgement is an ambiguous anchor after scrolling.
            if (length == 1 && _frontier.Count > 1 && CanonicalText(current[0].Text).Length <= 3)
                return (ChangeKind.Discontinuous, 0);
            return (ChangeKind.Append, length);
        }
        return (ChangeKind.Discontinuous, 0);
    }

    private static bool ContainsSequence(IReadOnlyList<ObservedMessage> source, IReadOnlyList<ObservedMessage> needle)
    {
        for (int start = 0; start <= source.Count - needle.Count; ++start)
        {
            bool match = true;
            for (int index = 0; index < needle.Count; ++index)
                if (!Same(source[start + index], needle[index])) { match = false; break; }
            if (match) return true;
        }
        return false;
    }

    private static bool EndsWith(IReadOnlyList<ObservedMessage> source, IReadOnlyList<ObservedMessage> suffix)
    {
        if (source.Count < suffix.Count) return false;
        for (int index = 1; index <= suffix.Count; ++index)
            if (!Same(source[^index], suffix[^index])) return false;
        return true;
    }

    private static bool Same(ObservedMessage left, ObservedMessage right) =>
        left.Speaker == right.Speaker && CanonicalText(left.Text) == CanonicalText(right.Text);

    private static string Fingerprint(IEnumerable<ObservedMessage> messages) =>
        string.Join("\u001e", messages.Select(m => $"{m.Speaker}\u001f{CanonicalText(m.Text)}"));

    private static string CanonicalText(string text) =>
        new(text.Normalize(NormalizationForm.FormKC).Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)).ToArray());

    private static List<ObservedMessage> NormalizeMessages(IEnumerable<ObservedMessage> messages) =>
        messages.Where(m => m is not null && !string.IsNullOrWhiteSpace(m.Text))
            .Select(m => new ObservedMessage(m.Speaker is "self" or "other" ? m.Speaker : "unknown", m.Text.Trim()))
            .TakeLast(MaximumMessages).ToList();

    private static string NormalizeKey(string? key)
    {
        string value = key?.Trim().Normalize(NormalizationForm.FormKC) ?? "";
        return value is "unknown" or "未识别" or "未知会话" or "不确定" or "未选择窗口" ? "" : value;
    }

    private enum ChangeKind { Previous, Append, Discontinuous }
}
