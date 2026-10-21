namespace ChatAssistant.Core;

/// <summary>Roles are exactly self, other or unknown; coordinates belong to the capture layer.</summary>
public sealed record ObservedMessage(string Speaker, string Text);

public sealed record ChatMessage(string Id, string Speaker, string Text);

public sealed record ConversationSnapshot(
    string Key,
    long Version,
    IReadOnlyList<ChatMessage> Messages,
    bool CanSuggest,
    string Status)
{
    /// <summary>Text is stable even when individual group speakers cannot be assigned.</summary>
    public bool IsStable { get; init; }
}

public sealed record TrackerUpdate(
    ConversationSnapshot Snapshot,
    IReadOnlyList<ChatMessage> NewMessages,
    bool Changed,
    bool IsBaseline);

public sealed record ArrangementHint(
    string Intent,
    string Title,
    string Reason,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Conflicts);
