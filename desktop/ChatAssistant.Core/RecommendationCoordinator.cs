namespace ChatAssistant.Core;

public sealed record RecommendationUpdate(GroupRecommendation? Recommendation, bool Changed, string Status);

/// <summary>In-memory, caller-clock driven debounce. It never schedules work or makes network calls.</summary>
public sealed class RecommendationCoordinator
{
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(8);
    private string _key = "";
    private string _publishedFingerprint = "";
    private readonly HashSet<string> _ignored = new(StringComparer.Ordinal);
    private GroupRecommendation? _pending;
    private DateTimeOffset _firstChange, _lastChange;
    private bool _blocked;
    private string _status = "观察中 · 等待群聊里的聚餐或出行需求";
    public GroupRecommendation? Current { get; private set; }
    public ConversationSnapshot? StableSnapshot { get; private set; }
    public bool IsPending => _blocked || _pending is not null;

    public RecommendationUpdate Observe(ConversationSnapshot snapshot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        bool changed = false;
        if (snapshot.Key != _key)
        {
            changed = Current is not null;
            Reset(); _key = snapshot.Key;
        }
        if (string.IsNullOrWhiteSpace(snapshot.Key)) return Update(changed);
        if (!snapshot.IsStable && !snapshot.CanSuggest)
        {
            StableSnapshot = null; _blocked = true;
            _status = "观察中 · 正在核对新画面，暂停更新推荐";
            return Update(changed);
        }
        _blocked = false; StableSnapshot = snapshot;
        var analysis = GroupRecommendationEngine.Analyze(snapshot);
        if (analysis is null || analysis.IsCancelled)
        {
            changed |= Current is not null;
            Current = null; _pending = null; _publishedFingerprint = "";
            _status = analysis?.IsCancelled == true ? "安排已取消 · 已撤下之前的推荐" : "观察中 · 暂未发现需要推荐的聚餐或出行安排";
            return Update(changed);
        }
        if (_ignored.Contains(analysis.Fingerprint))
        {
            changed |= Current is not null;
            Current = null; _pending = null;
            _status = "已忽略这组需求 · 有新条件时再更新";
            return Update(changed);
        }
        if (analysis.Fingerprint == _publishedFingerprint)
        {
            Current = analysis; _pending = null;
            _status = "推荐已更新到当前讨论 · 相同需求不重复提醒";
            return Update(changed);
        }
        if (_pending is null)
        {
            _firstChange = _lastChange = now;
        }
        else if (_pending.Fingerprint != analysis.Fingerprint) _lastChange = now;
        _pending = analysis;
        _status = "正在汇总群聊需求 · 稍等片刻自动展示方案";
        return Update(changed);
    }

    public RecommendationUpdate Tick(DateTimeOffset now)
    {
        if (_blocked || _pending is null || StableSnapshot is null) return Update(false);
        if (now - _lastChange < QuietPeriod && now - _firstChange < MaximumWait) return Update(false);
        Current = _pending; _pending = null; _publishedFingerprint = Current.Fingerprint;
        _status = "已根据当前群聊生成方案方向";
        return Update(true);
    }

    public RecommendationUpdate IgnoreCurrent()
    {
        var candidate = Current ?? _pending;
        if (candidate is not null) _ignored.Add(candidate.Fingerprint);
        bool changed = Current is not null;
        Current = null; _pending = null;
        _status = "已忽略这组需求 · 有新条件时再更新";
        return Update(changed);
    }

    /// <summary>Explicit user analysis overrides dismissal and debounce only for a stable snapshot.</summary>
    public RecommendationUpdate Refresh(ConversationSnapshot snapshot, DateTimeOffset now)
    {
        var update = Observe(snapshot, now);
        if (!snapshot.IsStable && !snapshot.CanSuggest) return update;
        var analysis = GroupRecommendationEngine.Analyze(snapshot);
        // Observe may already have removed the previous card; preserve its Changed flag.
        if (analysis is null || analysis.IsCancelled) return update;
        _ignored.Remove(analysis.Fingerprint);
        Current = analysis; _pending = null; _publishedFingerprint = analysis.Fingerprint;
        _status = "已按当前群聊重新分析方案";
        return Update(true);
    }

    public RecommendationUpdate Reset()
    {
        bool changed = Current is not null;
        Current = null; StableSnapshot = null; _pending = null; _blocked = false;
        _key = ""; _publishedFingerprint = ""; _ignored.Clear();
        _status = "观察中 · 等待群聊里的聚餐或出行需求";
        return Update(changed);
    }
    private RecommendationUpdate Update(bool changed) => new(Current, changed, _status);
}
