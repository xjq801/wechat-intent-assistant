using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using System.Diagnostics;

namespace ChatAssistant.Platform;

/// <summary>Uses the installed Windows OCR language pack. Never uploads or saves images.</summary>
public sealed class LocalOcrService
{
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly OcrEngine _engine;
    private readonly double _preferredScale;

    public static IReadOnlyList<string> AvailableLanguages =>
        OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToArray();

    public string Language => _engine.RecognizerLanguage.LanguageTag;

    public LocalOcrService(string language = "zh-Hans-CN", double preferredScale = 1.5)
    {
        if (!double.IsFinite(preferredScale) || preferredScale < 1 || preferredScale > 2)
            throw new ArgumentOutOfRangeException(nameof(preferredScale));
        _preferredScale = preferredScale;
        _engine = OcrEngine.TryCreateFromLanguage(new Language(language))
            ?? throw new InvalidOperationException(
                $"Windows 未安装 {language} OCR。请在 Windows 设置的语言选项中添加对应语言的文字识别功能。");
    }

    public async Task<OcrSnapshot> RecognizeAsync(CaptureFrame frame, NormalizedRegion region,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(region);
        cancellationToken.ThrowIfCancellationRequested();
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Width > 32768 || frame.Height > 32768
            || frame.Bgra.LongLength != (long)frame.Width * frame.Height * 4)
            throw new ArgumentException("帧尺寸和像素缓冲区不匹配。", nameof(frame));
        var roi = region.ToPixels(frame.Width, frame.Height);
        bool entered = false;
        try
        {
            await _serial.WaitAsync(cancellationToken);
            entered = true;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(4));
            var watch = Stopwatch.StartNew();
            var first = await RecognizePass(frame, roi, _preferredScale, false, budget.Token);
            // At most one local recovery pass. Sparse/very small text gets a contrast pass;
            // normal message frames use only the first pass. There is no name dictionary.
            int letters = first.Lines.Sum(l => l.Text.Count(char.IsLetterOrDigit));
            bool verySmall = first.Lines.Count > 0 && first.Lines.OrderBy(l => l.Height)
                .ElementAt(first.Lines.Count / 2).Height < 14;
            if ((letters < 2 || verySmall) && watch.ElapsedMilliseconds < 1000)
            {
                try
                {
                    using var recovery = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                    recovery.CancelAfter(TimeSpan.FromMilliseconds(1200));
                    var second = await RecognizePass(frame, roi, 2, true, recovery.Token);
                    if (second.Lines.Sum(l => l.Text.Count(char.IsLetterOrDigit)) > letters) first = second;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { /* A recovery timeout must not discard the usable first result. */ }
            }
            // Whole-region OCR can omit a very short isolated bubble. Retry only visually
            // distinct bubbles with no overlapping recognized line, never the entire screen.
            foreach (var bubble in BubbleRegions.FindMissing(frame, roi, first.Lines))
            {
                if (watch.ElapsedMilliseconds > 2200) break;
                try
                {
                    using var recovery = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                    recovery.CancelAfter(TimeSpan.FromMilliseconds(900));
                    var extra = await RecognizePass(frame, bubble, bubble.Height < 60 ? 3 : 2, false, recovery.Token);
                    var additions = extra.Lines.Where(l => l.Text.Any(char.IsLetterOrDigit))
                        .Select(l => l with { X = l.X + bubble.X - roi.X, Y = l.Y + bubble.Y - roi.Y });
                    first = first with { Lines = first.Lines.Concat(additions).OrderBy(l => l.Y).ThenBy(l => l.X).ToArray() };
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { break; }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return first;
        }
        finally
        {
            if (entered) _serial.Release();
        }
    }

    private async Task<OcrSnapshot> RecognizePass(CaptureFrame frame,
        (int X, int Y, int Width, int Height) roi, double scale, bool contrast,
        CancellationToken token)
    {
        var pixels = CropAndScale(frame, roi, (int)OcrEngine.MaxImageDimension, scale,
            out int ocrWidth, out int ocrHeight);
        int contentWidth = ocrWidth, contentHeight = ocrHeight, padX = 0, padY = 0;
        try
        {
            int dark = 0, sampled = 0;
            for (int i = 0; i < pixels.Length; i += Math.Max(4, pixels.Length / 600 / 4 * 4))
            { sampled++; if (pixels[i] + pixels[i + 1] + pixels[i + 2] < 300) dark++; }
            bool invert = sampled > 0 && dark > sampled * .60;
            if (contrast || invert)
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    int luma = (pixels[i] * 11 + pixels[i + 1] * 59 + pixels[i + 2] * 30) / 100;
                    if (invert) luma = 255 - luma;
                    byte value = (byte)Math.Clamp(contrast ? (luma - 128) * 1.25 + 128 : luma, 0, 255);
                    pixels[i] = pixels[i + 1] = pixels[i + 2] = value; pixels[i + 3] = 255;
                }
            // The Windows recognizer often misses an isolated two-character bubble when the
            // bitmap is almost the same size as the glyphs. A plain margin gives it line context.
            if (ocrWidth < 320 || ocrHeight < 96)
            {
                int paddedWidth = Math.Max(320, ocrWidth + 24), paddedHeight = Math.Max(96, ocrHeight + 24);
                padX = (paddedWidth - ocrWidth) / 2; padY = (paddedHeight - ocrHeight) / 2;
                var padded = new byte[paddedWidth * paddedHeight * 4]; Array.Fill(padded, (byte)255);
                for (int y = 0; y < ocrHeight; y++) System.Buffer.BlockCopy(pixels, y * ocrWidth * 4,
                    padded, ((y + padY) * paddedWidth + padX) * 4, ocrWidth * 4);
                Array.Clear(pixels); pixels = padded; ocrWidth = paddedWidth; ocrHeight = paddedHeight;
            }
            token.ThrowIfCancellationRequested();
            using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, ocrWidth, ocrHeight,
                BitmapAlphaMode.Premultiplied);
            using var writer = new DataWriter(); writer.WriteBytes(pixels); bitmap.CopyFromBuffer(writer.DetachBuffer());
            var result = await _engine.RecognizeAsync(bitmap).AsTask(token);
            token.ThrowIfCancellationRequested();
            double sx = (double)roi.Width / contentWidth, sy = (double)roi.Height / contentHeight;
            var lines = new List<OcrLine>();
            foreach (var line in result.Lines)
            {
                var group = new List<Windows.Media.Ocr.OcrWord>();
                foreach (var word in line.Words.OrderBy(w => w.BoundingRect.X))
                {
                    // OCR sometimes combines two distant bubbles on the same baseline.
                    if (group.Count > 0 && word.BoundingRect.X - group[^1].BoundingRect.Right
                        > Math.Max(32, Math.Max(word.BoundingRect.Height, group[^1].BoundingRect.Height) * 1.7))
                    { AddGroup(group); group.Clear(); }
                    group.Add(word);
                }
                AddGroup(group);
            }
            return new OcrSnapshot(lines, Language, roi.Width, roi.Height);

            void AddGroup(List<Windows.Media.Ocr.OcrWord> words)
            {
                if (words.Count == 0) return;
                double left = Math.Max(0, words.Min(w => w.BoundingRect.X) - padX), top = Math.Max(0, words.Min(w => w.BoundingRect.Y) - padY);
                double right = Math.Min(contentWidth, words.Max(w => w.BoundingRect.Right) - padX), bottom = Math.Min(contentHeight, words.Max(w => w.BoundingRect.Bottom) - padY);
                if (right <= left || bottom <= top) return;
                lines.Add(new OcrLine(string.Join(" ", words.Select(w => w.Text)), left * sx, top * sy,
                    (right - left) * sx, (bottom - top) * sy));
            }
        }
        finally { Array.Clear(pixels); }
    }

    private static byte[] CropAndScale(CaptureFrame frame,
        (int X, int Y, int Width, int Height) roi, int maximum, double preferredScale,
        out int width, out int height)
    {
        // A bounded enlargement improves small desktop message text; large ROIs keep their size.
        // Scaling is not a correction dictionary and cannot guarantee names are recognized correctly.
        int longestSide = Math.Max(roi.Width, roi.Height);
        double scale = Math.Min(longestSide <= 1200 ? preferredScale : 1, (double)maximum / longestSide);
        width = Math.Max(1, (int)Math.Floor(roi.Width * scale));
        height = Math.Max(1, (int)Math.Floor(roi.Height * scale));
        var pixels = new byte[checked(width * height * 4)];
        if (width == roi.Width && height == roi.Height)
        {
            for (int y = 0; y < height; y++)
                System.Buffer.BlockCopy(frame.Bgra, ((roi.Y + y) * frame.Width + roi.X) * 4,
                    pixels, y * width * 4, width * 4);
        }
        else if (scale > 1)
        {
            // Bilinear interpolation avoids block edges when enlarging small UI text.
            double xRatio = (double)roi.Width / width, yRatio = (double)roi.Height / height;
            for (int y = 0; y < height; y++)
            {
                double sourceY = Math.Clamp((y + .5) * yRatio - .5, 0, roi.Height - 1);
                int y0 = (int)sourceY, y1 = Math.Min(roi.Height - 1, y0 + 1);
                double wy = sourceY - y0;
                for (int x = 0; x < width; x++)
                {
                    double sourceX = Math.Clamp((x + .5) * xRatio - .5, 0, roi.Width - 1);
                    int x0 = (int)sourceX, x1 = Math.Min(roi.Width - 1, x0 + 1);
                    double wx = sourceX - x0;
                    int a = ((roi.Y + y0) * frame.Width + roi.X + x0) * 4;
                    int b = ((roi.Y + y0) * frame.Width + roi.X + x1) * 4;
                    int c = ((roi.Y + y1) * frame.Width + roi.X + x0) * 4;
                    int d = ((roi.Y + y1) * frame.Width + roi.X + x1) * 4;
                    int target = (y * width + x) * 4;
                    for (int channel = 0; channel < 4; channel++)
                        pixels[target + channel] = (byte)Math.Round(
                            (frame.Bgra[a + channel] * (1 - wx) + frame.Bgra[b + channel] * wx) * (1 - wy)
                            + (frame.Bgra[c + channel] * (1 - wx) + frame.Bgra[d + channel] * wx) * wy);
                }
            }
        }
        else
        {
            for (int y = 0; y < height; y++)
            {
                int srcY = roi.Y + Math.Min(roi.Height - 1, (int)(y / scale));
                for (int x = 0; x < width; x++)
                {
                    int srcX = roi.X + Math.Min(roi.Width - 1, (int)(x / scale));
                    System.Buffer.BlockCopy(frame.Bgra, (srcY * frame.Width + srcX) * 4,
                        pixels, (y * width + x) * 4, 4);
                }
            }
        }
        return pixels;
    }
}
