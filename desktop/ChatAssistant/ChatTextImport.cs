using System.Text.RegularExpressions;
using ChatAssistant.Core;

namespace ChatAssistant;

public static class ChatTextImport
{
    static readonly Regex Prefix = new(@"^(我|群友|群成员|对方|未标注|self|other)\s*[:：]\s*(.*)$", RegexOptions.IgnoreCase);
    static readonly Regex NamedPrefix = new(@"^[^：:\r\n]{1,24}[:：]\s*.+$");
    public static bool HasSpeakerLabels(string text) => text.Split('\n').Any(line => Prefix.IsMatch(line.Trim()));

    public static IReadOnlyList<ObservedMessage> Parse(string text, bool allFromOther)
    {
        var result = new List<ObservedMessage>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (allFromOther) { result.Add(new("other", line)); continue; }
            var match = Prefix.Match(line);
            if (match.Success)
            {
                var role = match.Groups[1].Value;
                string speaker = role.Equals("我", StringComparison.Ordinal) || role.Equals("self", StringComparison.OrdinalIgnoreCase)
                    ? "self" : role == "未标注" ? "unknown" : "other";
                result.Add(new(speaker, match.Groups[2].Value));
            }
            // A named group participant after “我：” is not a continuation of my message.
            // Preserve the complete nickname/text as evidence without guessing that identity.
            else if (NamedPrefix.IsMatch(line)) result.Add(new("unknown", line));
            else if (result.Count > 0)
            {
                var previous = result[^1];
                result[^1] = previous with { Text = previous.Text + "\n" + line };
            }
            else result.Add(new("unknown", line));
        }
        return result.Where(m => !string.IsNullOrWhiteSpace(m.Text)).TakeLast(20).ToArray();
    }
}
