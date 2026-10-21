using System.Text.RegularExpressions;
using ChatAssistant.Core;
using ChatAssistant.Platform;

namespace ChatAssistant.Recognition;

public static class MessageParser
{
    static readonly Regex Timestamp = new(@"^(?:(?:\d{2,4}[-/年.]\d{1,2}(?:[-/月.]\d{1,2}日?)?|\d{1,2}月\d{1,2}日|昨天|今天|前天|星期[一二三四五六日天]|周[一二三四五六日天])\s*)?(?:(?:凌晨|早上|上午|中午|下午|晚上|AM|PM)\s*)?\d{1,2}[:：]\d{2}(?:[:：]\d{2})?(?:AM|PM)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex SystemLine = new(@"^(?:以下[为是]新消息|以上是打招呼.*|你撤回了一条消息|对方撤回了一条消息|.+撤回了一条消息|你已添加了.+现在可以开始聊天了[。.]?|消息已发出[，,]但被对方拒收了[。.]?|[\[【]?该消息已被撤回[\]】]?)$", RegexOptions.Compiled);
    static readonly Regex DateMarker = new(@"^(?:昨天|今天|前天|星期[一二三四五六日天]|周[一二三四五六日天]|\d{2,4}年\d{1,2}月\d{1,2}日)$", RegexOptions.Compiled);

    public static string NormalizeText(string text)
    {
        text = Regex.Replace(text.Trim(), @"\s+", " ");
        text = Regex.Replace(text, @"(?<=[\p{IsCJKUnifiedIdeographs}\p{IsCJKUnifiedIdeographsExtensionA}\d])\s+(?=[\p{IsCJKUnifiedIdeographs}\p{IsCJKUnifiedIdeographsExtensionA}\d])", "");
        text = Regex.Replace(text, @"\s+([，。！？、：；,.!?;:])", "$1");
        text = Regex.Replace(text, @"([，。！？、：；])\s+(?=[\p{IsCJKUnifiedIdeographs}\d])", "$1");
        text = Regex.Replace(text, @"(?<=\d)：(?=\d)", ":");
        return text;
    }

    public static IReadOnlyList<ObservedMessage> Parse(CaptureFrame frame, NormalizedRegion region, OcrSnapshot snapshot)
    {
        var result = new List<ObservedMessage>(); OcrLine? previous = null; string lastRole = "";
        var roi = region.ToPixels(frame.Width, frame.Height);
        var observations = snapshot.Lines.OrderBy(l => l.Y).ThenBy(l => l.X)
            .Select(line => Observe(frame, roi, snapshot, line)).ToArray();
        for (int index = 0; index < observations.Length; index++)
        {
            var observation = observations[index]; var line = observation.Line;
            var text = observation.Text; if (text.Length == 0) continue;
            var role = observation.Role;
            string compact = Regex.Replace(text, @"\s+", "");
            if (role == "unknown" && (Timestamp.IsMatch(compact) || DateMarker.IsMatch(compact) || SystemLine.IsMatch(compact)))
            { previous = null; lastRole = ""; continue; }
            if (IsGroupNickname(observations, index))
            {
                // A visible member label is also a message boundary; never join two members'
                // bubbles merely because both are on the incoming side of the conversation.
                previous = null; lastRole = ""; continue;
            }
            // A time inside a real bubble is meaningful conversation (e.g. a proposed meeting time).
            var gap = previous == null ? double.MaxValue : line.Y - (previous.Y + previous.Height);
            bool continuation = previous != null && role != "unknown" && role == lastRole && gap >= -2
                && gap < Math.Max(8, previous.Height * .65)
                && Math.Abs(line.X - previous.X) < Math.Max(22, previous.Height)
                && SameBubble(frame, roi, previous, line, role, gap);
            if (continuation && result.Count > 0)
            {
                var prior = result[^1]; result[^1] = new ObservedMessage(role, NormalizeText(prior.Text + " " + text));
            }
            else result.Add(new ObservedMessage(role, text));
            previous = line; lastRole = role;
        }
        return result.TakeLast(20).ToArray();
    }

    private sealed record LineObservation(OcrLine Line, string Text, string Role,
        bool GrayInk, double WhiteRatio, double GreenRatio);

    static LineObservation Observe(CaptureFrame frame, (int X, int Y, int Width, int Height) roi,
        OcrSnapshot snapshot, OcrLine line)
    {
        int green = 0, light = 0, samples = 0;
        var neutralInk = new List<int>();
        for (var y = Math.Max(0, (int)line.Y - 2); y < Math.Min(snapshot.Height, line.Y + line.Height + 3); y += 3)
        for (var x = Math.Max(0, (int)line.X - 5); x < Math.Min(snapshot.Width, line.X + line.Width + 6); x += 5)
        {
            if (!TryColor(frame, roi.X + x, roi.Y + y, out var r, out var g, out var b)) continue;
            samples++; if (IsGreen(r, g, b)) green++; if (IsWhite(r, g, b)) light++;
            if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) < 18 && r < 190)
                neutralInk.Add(r);
        }
        double greenRatio = (double)green / Math.Max(1, samples), whiteRatio = (double)light / Math.Max(1, samples);
        // A green bubble is a strong outgoing cue. Plain background text is not a white bubble.
        // Unsupported bubble themes remain unknown so the UI can ask the user to correct them.
        string role = green >= 3 && greenRatio > .04 ? "self"
            : line.X < snapshot.Width * .43 && light >= 3 && whiteRatio > .18 ? "other" : "unknown";
        bool gray = neutralInk.Count >= 3 && neutralInk.Order().ElementAt(neutralInk.Count / 2) >= 65;
        return new LineObservation(line, NormalizeText(line.Text), role, gray, whiteRatio, greenRatio);
    }

    static bool IsGroupNickname(IReadOnlyList<LineObservation> observations, int index)
    {
        if (index + 1 >= observations.Count) return false;
        var label = observations[index]; var body = observations[index + 1];
        if (label.Role != "unknown" || !label.GrayInk || label.WhiteRatio > .10 || label.GreenRatio > .015
            || body.Role != "other" || label.Text.Length is < 1 or > 32
            || label.Text.Any(char.IsPunctuation) || label.Text.Any(char.IsSymbol)) return false;
        double gap = body.Line.Y - (label.Line.Y + label.Line.Height);
        return label.Line.Height <= body.Line.Height * .78
            && gap >= 0 && gap <= Math.Max(4, body.Line.Height * .75)
            && Math.Abs(label.Line.X - body.Line.X) <= Math.Max(8, body.Line.Height * .4);
    }

    static bool SameBubble(CaptureFrame f, (int X, int Y, int Width, int Height) roi,
        OcrLine previous, OcrLine line, string role, double gap)
    {
        if (gap <= 1) return true;
        int good = 0, total = 0;
        // The margin immediately to the left of aligned text is normally bubble fill. Sampling
        // several rows in the gap distinguishes wrapped lines from two neighboring bubbles.
        for (int row = 1; row <= 3; row++)
        {
            int y = roi.Y + (int)(previous.Y + previous.Height + gap * row / 4);
            int x = roi.X + Math.Max(0, (int)Math.Min(previous.X, line.X) - 4);
            if (!TryColor(f, x, y, out var r, out var g, out var b)) continue;
            total++; if (role == "self" ? IsGreen(r, g, b) : IsWhite(r, g, b)) good++;
        }
        return total >= 2 && good == total;
    }
    static bool IsGreen(byte r, byte g, byte b) => g > 110 && g > r + 24 && g > b + 24;
    static bool IsWhite(byte r, byte g, byte b) => r >= 249 && g >= 249 && b >= 249;
    static bool TryColor(CaptureFrame f, int x, int y, out byte r, out byte g, out byte b)
    {
        r = g = b = 0; if (x < 0 || y < 0 || x >= f.Width || y >= f.Height) return false;
        int i = (y * f.Width + x) * 4; b = f.Bgra[i]; g = f.Bgra[i + 1]; r = f.Bgra[i + 2]; return true;
    }
}
