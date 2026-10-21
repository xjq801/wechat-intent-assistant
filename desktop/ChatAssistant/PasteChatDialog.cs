using System.Windows;
using System.Windows.Controls;
using ChatAssistant.Core;

namespace ChatAssistant;

public sealed class PasteChatDialog : Window
{
    readonly TextBox editor = new() { AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 140, MaxLength = 16000 };
    readonly RadioButton other = new() { Content = "按群聊文字处理，保留原有昵称", Margin = new(0, 8, 0, 4) };
    readonly RadioButton labelled = new() { Content = "区分“我：/ 群友：”标记，其他昵称保留原文", Margin = new(0, 4, 0, 8) };
    readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 8) };
    public IReadOnlyList<ObservedMessage> Messages { get; private set; } = [];

    public PasteChatDialog(string clipboardText)
    {
        Title = "粘贴群聊记录";
        Width = 560;
        Height = Math.Min(570, SystemParameters.WorkArea.Height - 50);
        MinWidth = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new(18) };
        var head = new TextBlock { Text = "粘贴群里关于吃饭或出行的讨论", FontSize = 19, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) };
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        var footer = new StackPanel();
        footer.Children.Add(other);
        footer.Children.Add(labelled);
        notice.Text = "可保留“小王：……”这样的昵称。最多使用最近20条；消息条数不会当成人数。若开启自动千问，这些文字也会发送到千问。";
        footer.Children.Add(notice);
        var accept = new Button { Content = "使用这些文字", Style = (Style)FindResource("Primary"), HorizontalAlignment = HorizontalAlignment.Right };
        accept.Click += (_, _) =>
        {
            Messages = ChatTextImport.Parse(editor.Text, other.IsChecked == true);
            if (Messages.Count == 0) { notice.Text = "请先粘贴或输入一些文字。"; return; }
            DialogResult = true;
        };
        footer.Children.Add(accept);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        editor.Text = clipboardText.Length > 16000 ? clipboardText[..16000] : clipboardText;
        labelled.IsChecked = ChatTextImport.HasSpeakerLabels(editor.Text);
        other.IsChecked = labelled.IsChecked != true;
        bool manualMode = false;
        other.Click += (_, _) => manualMode = true;
        labelled.Click += (_, _) => manualMode = true;
        editor.TextChanged += (_, _) =>
        {
            if (manualMode) return;
            labelled.IsChecked = ChatTextImport.HasSpeakerLabels(editor.Text);
            other.IsChecked = labelled.IsChecked != true;
        };
        root.Children.Add(editor);
        Content = root;
    }
}
