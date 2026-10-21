namespace ChatAssistant.Platform;

/// <summary>
/// A single consumer with one replaceable pending item. Throttling happens before taking
/// the pending item, so a final update is delivered even if no later event wakes the producer.
/// Offer transfers ownership, including when the pump has already stopped.
/// </summary>
internal sealed class LatestItemPump<T> : IDisposable where T : class, IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<T, CancellationToken, Task> _consume;
    private readonly Action<Exception>? _failed;
    private readonly int _intervalMs;
    private T? _pending;
    private bool _disposed;
    private long _nextDue;

    public Task Completion { get; }

    public LatestItemPump(TimeSpan minimumInterval, Func<T, CancellationToken, Task> consume,
        Action<Exception>? failed = null)
    {
        if (minimumInterval < TimeSpan.Zero || minimumInterval.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(minimumInterval));
        _intervalMs = (int)Math.Ceiling(minimumInterval.TotalMilliseconds);
        _consume = consume ?? throw new ArgumentNullException(nameof(consume)); _failed = failed;
        Completion = Task.Run(RunAsync);
    }

    public bool Offer(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate)
        {
            if (_disposed) { item.Dispose(); return false; }
            var previous = _pending; _pending = item; previous?.Dispose();
            if (_signal.CurrentCount == 0) _signal.Release();
            return true;
        }
    }

    private async Task RunAsync()
    {
        Exception? failure = null;
        try
        {
            while (true)
            {
                await _signal.WaitAsync(_stop.Token).ConfigureAwait(false);
                var remaining = _nextDue - Environment.TickCount64;
                if (remaining > 0) await Task.Delay((int)Math.Min(remaining, int.MaxValue), _stop.Token).ConfigureAwait(false);
                T? item;
                lock (_gate)
                {
                    if (_disposed) return;
                    item = _pending; _pending = null;
                    if (item is not null) _nextDue = Environment.TickCount64 + _intervalMs;
                }
                if (item is null) continue;
                using (item) await _consume(item, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { failure = ex; }
        finally
        {
            lock (_gate)
            {
                _disposed = true;
                _pending?.Dispose(); _pending = null;
                _signal.Dispose(); _stop.Dispose();
            }
        }
        // Notify after resources are released and outside locks. Failure of a subscriber
        // must not leave an unobserved exception or a still-running capture worker.
        if (failure is not null) try { _failed?.Invoke(failure); } catch { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pending?.Dispose(); _pending = null;
            _stop.Cancel();
        }
    }
}
