using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ChatAssistant.Platform;

namespace ChatAssistant;

public sealed class RegionDialog : Window
{
    public NormalizedRegion HeaderRegion { get; private set; }
    public NormalizedRegion MessageRegion { get; private set; }
    public bool CurrentFrameOnly { get; private set; }
    readonly Canvas canvas;
    readonly TextBlock instructions;
    readonly Button done;
    readonly Button adjustHeader;
    readonly Rectangle headerBox;
    readonly Rectangle messageBox;
    readonly ChatLayoutEstimate estimate;
    Rectangle? selection;
    Point start;
    int editing = -1;

    public RegionDialog(CaptureFrame frame)
    {
        estimate = ChatLayoutEstimator.Estimate(frame);
        HeaderRegion = estimate.Header; MessageRegion = estimate.Messages;
        Title = "确认聊天区域";
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var work = SystemParameters.WorkArea;
        MaxWidth = Math.Max(320, work.Width - 24); MaxHeight = Math.Max(300, work.Height - 24);
        double availableWidth = Math.Min(1060, MaxWidth - 48);
        double availableHeight = Math.Max(120, Math.Min(660, MaxHeight - 260));
        double scale = Math.Min(1, Math.Min(availableWidth / frame.Width, availableHeight / frame.Height));
        double width = frame.Width * scale, height = frame.Height * scale;
        Width = Math.Max(Math.Min(640, MaxWidth), width + 48);
        SizeToContent = SizeToContent.Height;
        var panel = new StackPanel { Margin = new Thickness(18), Width = Width - 48 };
        instructions = new TextBlock { Text = "已推荐识别区域，检查绿色框后即可开始。框不合适时再调整。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), FontWeight = FontWeights.SemiBold };
        panel.Children.Add(instructions);
        canvas = new Canvas { Width = width, Height = height, Background = Brushes.Black,
            ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Center };
        var source = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Bgra, frame.Width * 4);
        source.Freeze();
        canvas.Children.Add(new Image { Source = source, Width = width, Height = height,
            Stretch = Stretch.Fill, IsHitTestVisible = false });
        headerBox = Box(Brushes.DarkOrange); messageBox = Box(Brushes.SeaGreen);
        canvas.Children.Add(headerBox); canvas.Children.Add(messageBox);
        DrawRegions();
        canvas.MouseLeftButtonDown += Down; canvas.MouseMove += Move; canvas.MouseLeftButtonUp += Up;
        panel.Children.Add(canvas);
        var options = new WrapPanel { Margin = new Thickness(0, 10, 0, 4) };
        adjustHeader = Button("调整橙色标题框", (_, _) => BeginEdit(0));
        options.Children.Add(adjustHeader);
        options.Children.Add(Button("调整绿色消息框", (_, _) => BeginEdit(1)));
        options.Children.Add(Button("恢复推荐框", (_, _) =>
        {
            EndEdit(); HeaderRegion = estimate.Header; MessageRegion = estimate.Messages; DrawRegions();
            instructions.Text = "已恢复推荐区域。绿色框应只包含聊天消息。";
        }));
        panel.Children.Add(options);
        var current = new CheckBox { Content = "仅识别消息区域（标题识别不准时使用）", Margin = new Thickness(3, 4, 0, 8) };
        current.Checked += (_, _) => SetCurrentFrame(true);
        current.Unchecked += (_, _) => SetCurrentFrame(false);
        panel.Children.Add(current);
        panel.Children.Add(new TextBlock { Text = "绿色：消息　橙色：联系人名称。请排除左侧列表、输入框和滚动条。推荐框只是起点，请检查。",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 10) });
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(Button("取消", (_, _) => DialogResult = false));
        done = Button("确认并开始识别", (_, _) => DialogResult = true);
        done.FontWeight = FontWeights.SemiBold;
        actions.Children.Add(done); panel.Children.Add(actions);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = MaxHeight - 40 };
    }

    static Button Button(string text, RoutedEventHandler handler)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(3) };
        button.Click += handler; return button;
    }
    static Rectangle Box(Brush stroke) => new() { Stroke = stroke, StrokeThickness = 2,
        Fill = new SolidColorBrush(Color.FromArgb(24, 20, 200, 130)), IsHitTestVisible = false };
    void SetCurrentFrame(bool value)
    {
        CurrentFrameOnly = value; adjustHeader.IsEnabled = !value;
        headerBox.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        if (value && editing == 0) EndEdit();
        instructions.Text = value ? "仅使用绿色框中的当前画面。切换联系人后请重新核对内容。"
            : "已推荐识别区域，检查绿色框后即可开始。框不合适时再调整。";
    }
    void DrawRegions() { Draw(headerBox, HeaderRegion); Draw(messageBox, MessageRegion); }
    void Draw(Rectangle box, NormalizedRegion region)
    {
        Canvas.SetLeft(box, region.X * canvas.Width); Canvas.SetTop(box, region.Y * canvas.Height);
        box.Width = region.Width * canvas.Width; box.Height = region.Height * canvas.Height;
    }
    void BeginEdit(int area)
    {
        EndEdit(); editing = area; canvas.Cursor = Cursors.Cross; done.IsEnabled = false;
        instructions.Text = area == 0 ? "在图中拖动，框住当前联系人名称。" : "在图中拖动，框住聊天消息，排除输入框与左侧列表。";
    }
    void EndEdit()
    {
        if (selection != null) canvas.Children.Remove(selection);
        selection = null; canvas.ReleaseMouseCapture(); editing = -1; canvas.Cursor = Cursors.Arrow;
        if (done != null) done.IsEnabled = true;
    }
    void Down(object sender, MouseButtonEventArgs e)
    {
        if (editing < 0) return;
        start = e.GetPosition(canvas);
        selection = Box(editing == 0 ? Brushes.DarkOrange : Brushes.LimeGreen);
        canvas.Children.Add(selection); Canvas.SetLeft(selection, start.X); Canvas.SetTop(selection, start.Y); canvas.CaptureMouse();
    }
    void Move(object sender, MouseEventArgs e)
    {
        if (selection == null || !canvas.IsMouseCaptured) return;
        var p = e.GetPosition(canvas); p.X = Math.Clamp(p.X, 0, canvas.Width); p.Y = Math.Clamp(p.Y, 0, canvas.Height);
        Canvas.SetLeft(selection, Math.Min(p.X, start.X)); Canvas.SetTop(selection, Math.Min(p.Y, start.Y));
        selection.Width = Math.Abs(p.X - start.X); selection.Height = Math.Abs(p.Y - start.Y);
    }
    void Up(object sender, MouseButtonEventArgs e)
    {
        if (selection == null || !canvas.IsMouseCaptured) return;
        Move(sender, e); canvas.ReleaseMouseCapture();
        if (selection.Width < 20 || selection.Height < (editing == 0 ? 8 : 35))
        { canvas.Children.Remove(selection); selection = null; instructions.Text = "区域太小，请重新拖动框选。"; return; }
        var region = new NormalizedRegion(Canvas.GetLeft(selection) / canvas.Width, Canvas.GetTop(selection) / canvas.Height,
            selection.Width / canvas.Width, selection.Height / canvas.Height);
        var other = editing == 0 ? MessageRegion : HeaderRegion;
        if (!CurrentFrameOnly && new Rect(other.X, other.Y, other.Width, other.Height)
                .IntersectsWith(new Rect(region.X, region.Y, region.Width, region.Height)))
        {
            canvas.Children.Remove(selection); selection = null;
            instructions.Text = "标题与消息框不能重叠，请重新框选，或勾选仅识别消息区域。"; return;
        }
        if (editing == 0) HeaderRegion = region; else MessageRegion = region;
        EndEdit(); DrawRegions(); instructions.Text = "区域已调整，可以开始识别。";
    }
}
