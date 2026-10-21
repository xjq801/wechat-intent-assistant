namespace ChatAssistant.Platform;

/// <summary>Conservative color components used only to retry bubbles omitted by whole-region OCR.</summary>
internal static class BubbleRegions
{
    public static IEnumerable<(int X, int Y, int Width, int Height)> FindMissing(CaptureFrame frame,
        (int X, int Y, int Width, int Height) roi, IReadOnlyList<OcrLine> lines)
    {
        // Bound the mask/queue work on high-DPI or very large captured windows.
        int step = Math.Max(3, (int)Math.Ceiling(Math.Sqrt((double)roi.Width * roi.Height / 250000)));
        int w = (roi.Width + step - 1) / step, h = (roi.Height + step - 1) / step;
        var mask = new byte[w * h]; var seen = new bool[mask.Length];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            int i = ((roi.Y + Math.Min(y * step, roi.Height - 1)) * frame.Width + roi.X + Math.Min(x * step, roi.Width - 1)) * 4;
            var b = frame.Bgra[i]; var g = frame.Bgra[i + 1]; var r = frame.Bgra[i + 2];
            mask[y * w + x] = g > 110 && g > r + 24 && g > b + 24 ? (byte)1
                : r >= 249 && g >= 249 && b >= 249 ? (byte)2 : (byte)0;
        }
        var candidates = new List<(int X, int Y, int Width, int Height)>();
        var queue = new Queue<int>();
        for (int start = 0; start < mask.Length; start++)
        {
            if (seen[start] || mask[start] == 0) continue;
            byte color = mask[start]; int minX = w, minY = h, maxX = 0, maxY = 0, count = 0;
            seen[start] = true; queue.Enqueue(start);
            while (queue.Count > 0)
            {
                int index = queue.Dequeue(), x = index % w, y = index / w; count++;
                minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
                if (x > 0) Visit(index - 1); if (x + 1 < w) Visit(index + 1);
                if (y > 0) Visit(index - w); if (y + 1 < h) Visit(index + w);
            }
            int left = Math.Max(0, minX * step - 3), top = Math.Max(0, minY * step - 3);
            int right = Math.Min(roi.Width, (maxX + 1) * step + 3), bottom = Math.Min(roi.Height, (maxY + 1) * step + 3);
            int width = right - left, height = bottom - top;
            if (count < 28 || width < 26 || height < 20 || width > roi.Width * .93 || height > roi.Height * .65) continue;
            if (lines.Any(line => line.X < right && line.X + line.Width > left
                && line.Y < bottom && line.Y + line.Height > top)) continue;
            candidates.Add((roi.X + left, roi.Y + top, width, height));

            void Visit(int index)
            {
                if (!seen[index] && mask[index] == color) { seen[index] = true; queue.Enqueue(index); }
            }
        }
        return candidates.OrderBy(c => c.Y).Take(2).ToArray();
    }
}
