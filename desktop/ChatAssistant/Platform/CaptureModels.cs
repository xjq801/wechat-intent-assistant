namespace ChatAssistant.Platform;

/// <summary>A tightly packed BGRA8 frame held only in process memory. CapturedAt records source
/// frame arrival, and must not be refreshed when a frame is reprocessed after a debounce.</summary>
public sealed record CaptureFrame(int Width, int Height, byte[] Bgra, string SourceName,
    DateTimeOffset CapturedAt);

/// <summary>A rectangle expressed as fractions of the selected window's frame.</summary>
public sealed record NormalizedRegion(double X, double Y, double Width, double Height)
{
    public static NormalizedRegion Full { get; } = new(0, 0, 1, 1);

    public (int X, int Y, int Width, int Height) ToPixels(int frameWidth, int frameHeight)
    {
        if (frameWidth <= 0 || frameHeight <= 0 || !double.IsFinite(X) || !double.IsFinite(Y)
            || !double.IsFinite(Width) || !double.IsFinite(Height) || X < 0 || Y < 0
            || Width <= 0 || Height <= 0 || X + Width > 1.000001 || Y + Height > 1.000001)
            throw new ArgumentOutOfRangeException(nameof(NormalizedRegion), "请选择窗口范围内的有效区域。");
        int left = Math.Clamp((int)Math.Floor(X * frameWidth), 0, frameWidth - 1);
        int top = Math.Clamp((int)Math.Floor(Y * frameHeight), 0, frameHeight - 1);
        int right = Math.Clamp((int)Math.Ceiling((X + Width) * frameWidth), left + 1, frameWidth);
        int bottom = Math.Clamp((int)Math.Ceiling((Y + Height) * frameHeight), top + 1, frameHeight);
        return (left, top, right - left, bottom - top);
    }
}

/// <summary>OCR coordinates refer to the original cropped ROI, in pixels.</summary>
public sealed record OcrLine(string Text, double X, double Y, double Width, double Height);
public sealed record OcrSnapshot(IReadOnlyList<OcrLine> Lines, string Language, int Width, int Height);
