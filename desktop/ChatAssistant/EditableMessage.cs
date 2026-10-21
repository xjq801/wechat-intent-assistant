using System.ComponentModel;
using System.Runtime.CompilerServices;
using ChatAssistant.Core;

namespace ChatAssistant;

public sealed class EditableMessage : INotifyPropertyChanged
{
    string text;
    string speakerLabel;
    public EditableMessage(ChatMessage message)
    {
        text = message.Text;
        speakerLabel = message.Speaker == "self" ? "我" : message.Speaker == "other" ? "群友" : "未标注";
    }
    public string Text { get => text; set => Set(ref text, value); }
    public string SpeakerLabel { get => speakerLabel; set => Set(ref speakerLabel, value); }
    public string Speaker => SpeakerLabel == "我" ? "self" : SpeakerLabel == "群友" ? "other" : "unknown";
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? Edited;
    void Set(ref string field, string value, [CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
        Edited?.Invoke();
    }
}
