using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ChatAssistant.Platform;

/// <summary>Capture starts only after the user has selected a source with the Windows picker.</summary>
public sealed class WindowCaptureService : IDisposable
{
    private readonly object _gate = new();
    private GraphicsCaptureItem? _item;
    private CaptureRun? _run;
    private bool _disposed;
    private long _selectionVersion;

    public event Action<CaptureFrame>? FrameAvailable;
    public event Action? SourceClosed;
    public event Action? SourceResized;
    public event Action<string>? CaptureFailed;

    public static bool IsSupported => GraphicsCaptureSession.IsSupported();
    public string SourceName { get { lock (_gate) return _item?.DisplayName ?? ""; } }
    public bool IsRunning { get { lock (_gate) return _run is not null; } }

    public async Task<bool> SelectSourceAsync(IntPtr owner)
    {
        if (owner == IntPtr.Zero) throw new ArgumentException("请选择器指定助手窗口。", nameof(owner));
        if (!IsSupported) throw new PlatformNotSupportedException("此 Windows 环境不支持窗口采集。");
        long version;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopCore();
            ClearItem();
            version = ++_selectionVersion;
        }
        var picker = new GraphicsCapturePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, owner);
        var item = await picker.PickSingleItemAsync();
        lock (_gate)
        {
            if (_disposed || version != _selectionVersion || item is null) return false;
            _item = item;
            item.Closed += OnItemClosed;
            return true;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_run is not null) return;
            if (_item is null) throw new InvalidOperationException("请先通过 Windows 选择聊天窗口。");
            if (_item.Size.Width <= 0 || _item.Size.Height <= 0)
                throw new InvalidOperationException("所选窗口当前没有可采集的画面。");
            var run = new CaptureRun(_item, Direct3DDeviceFactory.Create(), CopyFrameAsync,
                (failedRun, _) => HandleFailure(failedRun));
            _run = run;
            run.Handler = (sender, _) => OnFrameArrived(run, sender);
            run.Pool.FrameArrived += run.Handler;
            try { run.Session.StartCapture(); }
            catch { _run = null; run.Retire(); throw; }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            ++_selectionVersion;
            StopCore();
        }
    }

    private void StopCore()
    {
        var previous = _run;
        _run = null;
        previous?.Retire();
    }

    private void OnFrameArrived(CaptureRun run, Direct3D11CaptureFramePool sender)
    {
        if (!run.Enter()) return;
        Direct3D11CaptureFrame? frame = null;
        try
        {
            frame = sender.TryGetNextFrame();
            if (frame is null) return;
            if (frame.ContentSize.Width != run.Width || frame.ContentSize.Height != run.Height)
            {
                lock (_gate)
                {
                    if (_run != run) return;
                    StopCore();
                    SourceResized?.Invoke();
                }
                return;
            }
            // Ownership moves to the pump. Replaced pending GPU frames are released promptly;
            // the last pending frame is still copied after the throttle even with no new event.
            run.Pump.Offer(new PendingFrame(frame, DateTimeOffset.UtcNow));
            frame = null;
        }
        catch (Exception) { HandleFailure(run); }
        finally { frame?.Dispose(); run.Exit(); }
    }

    private async Task CopyFrameAsync(CaptureRun run, PendingFrame pending, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsCurrent(run)) return;
        // Keep both the GPU surface and its device alive until the native copy completes.
        using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(pending.Frame.Surface,
            BitmapAlphaMode.Premultiplied);
        token.ThrowIfCancellationRequested();
        if (!IsCurrent(run)) return;
        uint size = checked((uint)(bitmap.PixelWidth * bitmap.PixelHeight * 4));
        var buffer = new Windows.Storage.Streams.Buffer(size);
        bitmap.CopyToBuffer(buffer);
        var pixels = new byte[size];
        using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
        var captured = new CaptureFrame(bitmap.PixelWidth, bitmap.PixelHeight, pixels,
            run.Name, pending.ArrivedAt);
        lock (_gate)
        {
            if (_run == run && !_disposed) FrameAvailable?.Invoke(captured);
            else Array.Clear(pixels);
        }
    }

    private void HandleFailure(CaptureRun run)
    {
        lock (_gate)
        {
            if (_run == run)
            {
                StopCore();
                CaptureFailed?.Invoke("窗口采集已暂停，请重新选择窗口。关闭、锁屏或显示设备变化可能导致采集中断。");
            }
        }
    }

    private bool IsCurrent(CaptureRun run) { lock (_gate) return _run == run && !_disposed; }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _item)) return;
            ++_selectionVersion;
            StopCore();
            ClearItem();
            SourceClosed?.Invoke();
        }
    }

    private void ClearItem()
    {
        if (_item is not null) _item.Closed -= OnItemClosed;
        _item = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ++_selectionVersion;
            StopCore();
            ClearItem();
        }
    }

    private sealed record PendingFrame(Direct3D11CaptureFrame Frame, DateTimeOffset ArrivedAt) : IDisposable
    {
        public void Dispose() => Frame.Dispose();
    }

    private sealed class CaptureRun
    {
        private readonly object _life = new();
        private readonly IDirect3DDevice _device;
        private int _readers;
        private bool _retired, _released;
        public Direct3D11CaptureFramePool Pool { get; }
        public GraphicsCaptureSession Session { get; }
        public int Width { get; }
        public int Height { get; }
        public string Name { get; }
        public LatestItemPump<PendingFrame> Pump { get; }
        public Windows.Foundation.TypedEventHandler<Direct3D11CaptureFramePool, object>? Handler;

        public CaptureRun(GraphicsCaptureItem item, IDirect3DDevice device,
            Func<CaptureRun, PendingFrame, CancellationToken, Task> consume,
            Action<CaptureRun, Exception> failed)
        {
            _device = device;
            Width = item.Size.Width;
            Height = item.Size.Height;
            Name = item.DisplayName;
            try
            {
                Pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
                Session = Pool.CreateCaptureSession(item);
                Session.IsCursorCaptureEnabled = false;
                // Retain the system capture indicator; never request borderless capture.
                Pump = new LatestItemPump<PendingFrame>(TimeSpan.FromSeconds(1),
                    (frame, token) => consume(this, frame, token), ex => failed(this, ex));
            }
            catch { Pool?.Dispose(); device.Dispose(); throw; }
        }

        public bool Enter() { lock (_life) { if (_retired) return false; _readers++; return true; } }
        public void Exit() { lock (_life) { _readers--; ReleaseIfReady(); } }
        public void Retire()
        {
            lock (_life)
            {
                _retired = true;
                if (Handler is not null) Pool.FrameArrived -= Handler;
                Pump.Dispose();
                _ = Pump.Completion.ContinueWith(_ =>
                {
                    lock (_life) ReleaseIfReady();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                ReleaseIfReady();
            }
        }
        private void ReleaseIfReady()
        {
            if (!_retired || _released || _readers != 0 || !Pump.Completion.IsCompleted) return;
            _released = true;
            Session.Dispose();
            Pool.Dispose();
            _device.Dispose();
        }
    }
}
