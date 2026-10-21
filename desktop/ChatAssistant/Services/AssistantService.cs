using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatAssistant.Core;

namespace ChatAssistant.Services;

public sealed record AssistantOptions(string PythonPath, string WebRoot, string BridgePath);
public sealed record AssistantResult(long Version, string Intent, string Summary,
    IReadOnlyList<string> Missing, IReadOnlyList<string> Replies, string Source, long DurationMs, string Warning);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GroupAiOption(string Title, string Description, string TradeOff);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GroupAiRecommendationResult(long Version, string Summary,
    IReadOnlyList<GroupAiOption> Options, IReadOnlyList<string> Missing, string Source, long DurationMs, string Warning);

/// <summary>A private, serial JSONL child process. Never binds a network port.</summary>
public sealed class AssistantService : IDisposable
{
    private readonly AssistantOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _processLock = new();
    private Process? _process;
    private bool _disposed;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public AssistantService(AssistantOptions options) => _options = options;

    /// <summary>Show exactly this bounded context before the explicit named cloud action.</summary>
    public static IReadOnlyList<ChatMessage> PrepareMessages(IReadOnlyList<ChatMessage> messages)
    {
        var result = new List<ChatMessage>();
        int remaining = 8000;
        foreach (var item in messages.TakeLast(20).Reverse())
        {
            if (remaining == 0) break;
            var runes = item.Text.Trim().EnumerateRunes().Take(Math.Min(2000, remaining)).ToArray();
            var text = string.Concat(runes.Select(x => x.ToString()));
            if (text.Length == 0) continue;
            result.Add(item with { Text = text });
            remaining -= runes.Length;
        }
        result.Reverse();
        return result;
    }

    public Task<AssistantResult> AnalyzeAsync(ConversationSnapshot snapshot, bool useLora, CancellationToken ct) =>
        ExecuteAsync(snapshot, "analyze", useLora, Failure, x => Valid(x, snapshot.Version), ct);

    /// <summary>Call only for the explicit cloud button, with the current bounded visible context and named endpoint.</summary>
    public Task<AssistantResult> ReplyAsync(ConversationSnapshot snapshot, CancellationToken ct) =>
        ExecuteAsync(snapshot, "reply", false, Failure, x => Valid(x, snapshot.Version), ct);

    /// <summary>Call only for the explicit cloud action or an opted-in automatic group recommendation on a stable snapshot.
    /// Unknown speakers are permitted: group recommendations do not require personal reply attribution.</summary>
    public Task<GroupAiRecommendationResult> RecommendAsync(ConversationSnapshot snapshot, CancellationToken ct) =>
        ExecuteAsync(snapshot, "recommend", false, RecommendationFailure, x => Valid(x, snapshot.Version), ct);

    private async Task<TResult> ExecuteAsync<TResult>(ConversationSnapshot snapshot, string operation, bool useLora,
        Func<long, string, TResult> failure, Func<TResult, bool> valid, CancellationToken ct) where TResult : class
    {
        ct.ThrowIfCancellationRequested();
        bool isGroup = operation == "recommend";
        if (isGroup ? !(snapshot.IsStable || snapshot.CanSuggest) : !snapshot.CanSuggest)
            return failure(snapshot.Version, "请先校对识别结果并确认当前会话。");
        var messages = PrepareMessages(snapshot.Messages);
        if (messages.Count == 0 || messages.Any(x => x.Speaker is not ("self" or "other") && !(isGroup && x.Speaker == "unknown")))
            return failure(snapshot.Version, "没有可分析的聊天文字，请先校对内容。");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        Process? worker = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            worker = await GetWorkerAsync(ct).ConfigureAwait(false);
            var currentWorker = worker;
            // Cancelling discards pending bytes and terminates only this app's worker.
            using var cancellation = ct.Register(() => StopWorker(currentWorker));
            ct.ThrowIfCancellationRequested();
            var requestId = Guid.NewGuid().ToString("N");
            var request = JsonSerializer.Serialize(new
            {
                requestId, op = operation, version = snapshot.Version, useLora,
                cloudConsent = operation is "reply" or "recommend",
                messages = messages.Select(x => new { speaker = x.Speaker, text = x.Text })
            });
            await worker.StandardInput.WriteLineAsync(request.AsMemory(), ct).ConfigureAwait(false);
            await worker.StandardInput.FlushAsync(ct).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(operation == "analyze" && useLora ? TimeSpan.FromSeconds(180) : TimeSpan.FromSeconds(50));
            string? line;
            try { line = await worker.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                StopWorker(worker);
                return failure(snapshot.Version, "助手处理超时，本次结果已丢弃，可重试。");
            }
            ct.ThrowIfCancellationRequested();
            if (line is null || line.Length > 30000) throw new InvalidDataException();
            using var envelope = JsonDocument.Parse(line);
            if (!envelope.RootElement.TryGetProperty("requestId", out var id) || id.GetString() != requestId
                || !envelope.RootElement.TryGetProperty("result", out var body)) throw new InvalidDataException();
            var result = body.Deserialize<TResult>(Json) ?? throw new InvalidDataException();
            if (!valid(result)) throw new InvalidDataException();
            ct.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (Exception)
        {
            if (worker is not null) StopWorker(worker);
            return failure(snapshot.Version, "AI 服务暂不可用。请检查 Python、网页项目和 bridge 路径；本地 OCR 仍可使用。");
        }
        finally { _gate.Release(); }
    }

    private async Task<Process> GetWorkerAsync(CancellationToken ct)
    {
        lock (_processLock)
        {
            if (_process is not null && !_process.HasExited) return _process;
            _process?.Dispose();
            _process = null;
        }
        if (!File.Exists(_options.PythonPath) || !File.Exists(_options.BridgePath)
            || !File.Exists(Path.Combine(_options.WebRoot, "assistant_features.py"))) throw new FileNotFoundException();
        var info = new ProcessStartInfo(_options.PythonPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Path.GetDirectoryName(_options.BridgePath)!
        };
        info.ArgumentList.Add("-X"); info.ArgumentList.Add("utf8"); info.ArgumentList.Add("-u");
        info.ArgumentList.Add(_options.BridgePath); info.ArgumentList.Add("--web-root"); info.ArgumentList.Add(_options.WebRoot);
        info.Environment["PYTHONUTF8"] = "1";
        info.Environment["HF_HUB_OFFLINE"] = "1";
        info.Environment["TRANSFORMERS_OFFLINE"] = "1";
        info.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        var worker = Process.Start(info) ?? throw new IOException();
        lock (_processLock)
        {
            if (_disposed) { worker.Kill(true); worker.Dispose(); throw new ObjectDisposedException(nameof(AssistantService)); }
            _process = worker;
        }
        _ = DrainErrorsAsync(worker.StandardError);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var line = await worker.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            using var ready = JsonDocument.Parse(line ?? "{}");
            if (!ready.RootElement.TryGetProperty("ready", out var status) || !status.GetBoolean()
                || ready.RootElement.GetProperty("protocol").GetInt32() != 1) throw new InvalidDataException();
            return worker;
        }
        catch { StopWorker(worker); throw; }
    }

    private static async Task DrainErrorsAsync(StreamReader reader)
    {
        try { var buffer = new char[4096]; while (await reader.ReadAsync(buffer).ConfigureAwait(false) > 0) { } }
        catch (Exception) { /* Diagnostics are deliberately not persisted with private conversation data. */ }
    }

    private void StopWorker(Process worker)
    {
        lock (_processLock)
        {
            try { if (!worker.HasExited) worker.Kill(entireProcessTree: true); }
            catch (Exception) { }
            if (ReferenceEquals(_process, worker)) _process = null;
            try { worker.Dispose(); } catch (Exception) { }
        }
    }

    private static AssistantResult Failure(long version, string warning) =>
        new(version, "无意图", "", Array.Empty<string>(), Array.Empty<string>(), "未生成", 0, warning);

    private static GroupAiRecommendationResult RecommendationFailure(long version, string warning) =>
        new(version, "", Array.Empty<GroupAiOption>(), Array.Empty<string>(), "未生成", 0, warning);

    private static bool Valid(AssistantResult value, long version) => value.Version == version
        && value.Replies is not null && value.Missing is not null && value.Replies.Count <= 2
        && value.Source is not null && value.Warning is not null && value.Summary is not null && value.Intent is not null;

    private static bool Valid(GroupAiRecommendationResult value, long version) => value.Version == version
        && value.Options is not null && value.Options.Count is 0 or 2 or 3
        && value.Options.All(x => x is not null && TextWithin(x.Title, 60) && TextWithin(x.Description, 240) && TextWithin(x.TradeOff, 200))
        && value.Options.Select(x => x.Title.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() == value.Options.Count
        && value.Missing is not null && value.Missing.Count <= 6 && value.Missing.All(x => TextWithin(x, 80))
        && TextWithin(value.Source, 128) && value.Warning is not null && value.Warning.Length <= 1000
        && value.Summary is not null && value.Summary.Length <= 300 && (value.Options.Count == 0 || value.Summary.Trim().Length > 0)
        && value.DurationMs is >= 0 and <= 300000;

    private static bool TextWithin(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;

    public void Dispose()
    {
        lock (_processLock)
        {
            if (_disposed) return;
            _disposed = true;
            if (_process is not null) StopWorker(_process);
        }
        // The semaphore can still be released by a cancelled in-flight operation.
    }
}
