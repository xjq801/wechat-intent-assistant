using System.Security.Cryptography;
using System.Text;
using ChatAssistant.Platform;

namespace ChatAssistant.Recognition;
public static class ConversationIdentity
{
    // Include an in-memory image signature: two different titles must not share a session merely
    // because OCR produces the same text. Window/ROI changes are separately reset by the UI.
    public static string Build(CaptureFrame frame,NormalizedRegion region,string title)
    {
        if(string.IsNullOrWhiteSpace(title))return "";
        var roi=region.ToPixels(frame.Width,frame.Height);const int columns=64,rows=16;
        var sample=new byte[columns*rows];
        for(var y=0;y<rows;y++)for(var x=0;x<columns;x++)
        {
            var x0=roi.X+x*roi.Width/columns;var x1=Math.Min(roi.X+roi.Width,roi.X+(x+1)*roi.Width/columns+1);
            var y0=roi.Y+y*roi.Height/rows;var y1=Math.Min(roi.Y+roi.Height,roi.Y+(y+1)*roi.Height/rows+1);
            long sum=0,count=0;
            for(var py=y0;py<y1;py++)for(var px=x0;px<x1;px++)
            {var i=(py*frame.Width+px)*4;sum+=(frame.Bgra[i]*11+frame.Bgra[i+1]*59+frame.Bgra[i+2]*30)/100;count++;}
            sample[y*columns+x]=(byte)(count==0?0:(sum/count)/16);
        }
        return title.Trim()+"\u001f"+Convert.ToHexString(SHA256.HashData(sample))[..16];
    }
    public static string Display(string key)=>key.Split('\u001f')[0];
}

public sealed record SessionIdentity(string Key, string DisplayName, bool FrameOnly, bool Changed);

/// <summary>
/// The title image is the identity anchor; OCR is a display label only. This avoids abandoning
/// a perfectly usable conversation whenever title OCR is empty or fluctuates by one character.
/// The anchor is never gradually updated, so a sequence of small changes cannot drift into a
/// different conversation. Reset when the selected source or title region changes.
/// </summary>
public sealed class SessionIdentityResolver
{
    private const int Columns = 160, Rows = 40;
    private byte[]? _anchor;
    private string _key = "", _displayName = "";
    private long _sequence;

    public SessionIdentity Resolve(CaptureFrame? frame, NormalizedRegion? headerRegion, string? ocrTitle)
    {
        var signature = CreateSignature(frame, headerRegion);
        if (signature is null)
        {
            bool changed = _key.Length > 0;
            Reset();
            return new("", "当前画面（不累计历史）", true, changed);
        }

        bool imageChanged = _anchor is null || !SameImage(_anchor, signature);
        if (imageChanged)
        {
            _anchor = signature;
            _key = $"visual-session-{++_sequence}";
            _displayName = "当前会话（名称未识别）";
        }
        var label = ReadableLabel(ocrTitle);
        if (label.Length > 0) _displayName = label;
        return new(_key, _displayName, false, imageChanged);
    }

    public void Reset()
    {
        _anchor = null;
        _key = "";
        _displayName = "";
    }

    private static string ReadableLabel(string? title)
    {
        string value = new((title ?? "").Normalize(NormalizationForm.FormKC)
            .Where(c => !char.IsControl(c)).Take(100).ToArray());
        value = value.Trim();
        return value.Any(char.IsLetterOrDigit) ? value : "";
    }

    private static byte[]? CreateSignature(CaptureFrame? frame, NormalizedRegion? region)
    {
        if (frame is null || region is null || frame.Width <= 0 || frame.Height <= 0
            || frame.Bgra is null || (long)frame.Width * frame.Height * 4 > frame.Bgra.Length)
            return null;
        (int X, int Y, int Width, int Height) roi;
        try { roi = region.ToPixels(frame.Width, frame.Height); }
        catch (ArgumentOutOfRangeException) { return null; }
        if (roi.Width < 6 || roi.Height < 6) return null;

        // The median represents the plain title-bar background in light and dark themes.
        // Contrast, rather than absolute RGB values, suppresses small lighting/compositor noise.
        var histogram = new int[256];
        for (int y = roi.Y; y < roi.Y + roi.Height; ++y)
            for (int x = roi.X; x < roi.X + roi.Width; ++x)
                ++histogram[Luminance(frame, x, y)];
        int count = roi.Width * roi.Height, middle = count / 2, cumulative = 0, background = 0;
        for (; background < 255; ++background)
        {
            cumulative += histogram[background];
            if (cumulative >= middle) break;
        }

        var ink = new long[Columns * Rows];
        var area = new int[Columns * Rows];
        long totalInk = 0;
        for (int y = 0; y < roi.Height; ++y)
            for (int x = 0; x < roi.Width; ++x)
            {
                int cell = Math.Min(Rows - 1, y * Rows / roi.Height) * Columns
                    + Math.Min(Columns - 1, x * Columns / roi.Width);
                ++area[cell];
                if (Math.Abs(Luminance(frame, roi.X + x, roi.Y + y) - background) >= 35)
                { ++ink[cell]; ++totalInk; }
            }

        // A blank/solid title region cannot establish who is speaking, even if OCR hallucinated
        // a name. Use the isolated current-frame route in that case.
        if (totalInk < 12 || totalInk < count * .001 || totalInk > count * .65)
            return null;
        var result = new byte[Columns * Rows];
        for (int index = 0; index < result.Length; ++index)
            result[index] = area[index] == 0 ? (byte)0 : (byte)(255 * ink[index] / area[index]);
        return result;
    }

    private static int Luminance(CaptureFrame frame, int x, int y)
    {
        int offset = (y * frame.Width + x) * 4;
        return (frame.Bgra[offset] * 11 + frame.Bgra[offset + 1] * 59 + frame.Bgra[offset + 2] * 30) / 100;
    }

    private static bool SameImage(byte[] left, byte[] right)
    {
        long difference = 0, leftInk = 0, rightInk = 0;
        for (int index = 0; index < left.Length; ++index)
        {
            difference += Math.Abs(left[index] - right[index]);
            leftInk += left[index]; rightInk += right[index];
        }
        if (difference > Math.Max(leftInk, rightInk) * .04) return false;

        // A changed character in a long title may contribute little to the total. Also compare
        // local tiles, preventing a similar long name from being accepted by a global average.
        for (int ty = 0; ty < Rows; ty += 5)
            for (int tx = 0; tx < Columns; tx += 10)
            {
                long localDifference = 0, localLeft = 0, localRight = 0;
                for (int y = ty; y < Math.Min(Rows, ty + 5); ++y)
                    for (int x = tx; x < Math.Min(Columns, tx + 10); ++x)
                    {
                        int index = y * Columns + x;
                        localDifference += Math.Abs(left[index] - right[index]);
                        localLeft += left[index]; localRight += right[index];
                    }
                if (localDifference > 200 && localDifference > Math.Max(localLeft, localRight) * .22)
                    return false;
            }
        return true;
    }
}
