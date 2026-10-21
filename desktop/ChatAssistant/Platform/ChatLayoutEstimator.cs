namespace ChatAssistant.Platform;

public sealed record ChatLayoutEstimate(NormalizedRegion Header, NormalizedRegion Messages,
    bool HasDetectedStructure);

/// <summary>Suggests editable regions from long UI separators; it does not identify contacts.</summary>
public static class ChatLayoutEstimator
{
    public static ChatLayoutEstimate Estimate(CaptureFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width < 80 || frame.Height < 80 || frame.Bgra.LongLength != (long)frame.Width * frame.Height * 4)
            throw new ArgumentException("窗口图像尺寸无效。", nameof(frame));
        int w = frame.Width, h = frame.Height;
        // A contacts pane usually ends at a continuous vertical color boundary. Requiring most
        // sampled rows to agree prevents individual avatars/messages from becoming a divider.
        int divider = FindVertical(frame, (int)(w * .16), (int)(w * .53));
        bool detected = divider >= 0;
        double left = detected ? (divider + Math.Max(5, w * .012)) / w : .32;
        left = Math.Clamp(left, .16, .55);
        int bodyLeft = (int)(left * w);
        int topLine = FindHorizontal(frame, bodyLeft, (int)(h * .075), (int)(h * .22));
        int inputLine = FindHorizontal(frame, bodyLeft, (int)(h * .62), (int)(h * .88));
        double top = topLine >= 0 ? (topLine + 5d) / h : .14;
        double bottom = inputLine >= 0 ? (inputLine - 6d) / h : .76;
        bottom = Math.Max(bottom, top + .35);
        double headerBottom = Math.Min(top - .015, .12);
        var header = new NormalizedRegion(Math.Min(left + .018, .59), .025,
            Math.Min(.68, .94 - left - .018), Math.Max(.045, headerBottom - .025));
        var messages = new NormalizedRegion(left, top, .98 - left, bottom - top);
        return new ChatLayoutEstimate(header, messages, detected);
    }

    private static int FindVertical(CaptureFrame f, int start, int end)
    {
        int best = -1; double bestScore = 0;
        for (int x = start; x <= end; x += 2)
        {
            int hits = 0, count = 0; double difference = 0;
            for (int y = (int)(f.Height * .18); y < f.Height * .72; y += Math.Max(3, f.Height / 80))
            {
                double delta = Contrast(f, x - 4, y, x + 4, y); count++;
                if (delta >= 8) { hits++; difference += Math.Min(delta, 35); }
            }
            double score = count == 0 ? 0 : (double)hits / count;
            if (score >= .72 && score * 10 + difference / count > bestScore)
            { bestScore = score * 10 + difference / count; best = x; }
        }
        return best;
    }

    private static int FindHorizontal(CaptureFrame f, int left, int start, int end)
    {
        int best = -1; double bestScore = 0;
        for (int y = start; y <= end; y += 2)
        {
            int hits = 0, count = 0; double difference = 0;
            for (int x = left + (f.Width - left) / 12; x < f.Width * .94; x += Math.Max(3, f.Width / 100))
            {
                double delta = Contrast(f, x, y - 3, x, y + 3); count++;
                if (delta >= 7) { hits++; difference += Math.Min(delta, 30); }
            }
            double score = count == 0 ? 0 : (double)hits / count;
            if (score >= .92 && score * 10 + difference / count > bestScore)
            { bestScore = score * 10 + difference / count; best = y; }
        }
        return best;
    }

    private static double Contrast(CaptureFrame f, int ax, int ay, int bx, int by)
    {
        int a = (Math.Clamp(ay, 0, f.Height - 1) * f.Width + Math.Clamp(ax, 0, f.Width - 1)) * 4;
        int b = (Math.Clamp(by, 0, f.Height - 1) * f.Width + Math.Clamp(bx, 0, f.Width - 1)) * 4;
        return (Math.Abs(f.Bgra[a] - f.Bgra[b]) + Math.Abs(f.Bgra[a + 1] - f.Bgra[b + 1])
            + Math.Abs(f.Bgra[a + 2] - f.Bgra[b + 2])) / 3d;
    }
}
