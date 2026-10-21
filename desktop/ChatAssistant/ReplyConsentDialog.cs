using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ChatAssistant;
public sealed class ReplyConsentDialog : Window
{
    public ReplyConsentDialog(string transcript)
    {
        Title="确认本次发送给千问的文字";Width=490;Height=560;MinWidth=390;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var grid=new Grid { Margin=new Thickness(20) };
        grid.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });grid.RowDefinitions.Add(new RowDefinition());grid.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        var heading=new TextBlock { Text="本次只发送下方文字\n目的地：maas.qianwenaiapi.com\n模型：qwen3.8-flash\n不发送联系人名称或截图。返回的建议不会自动发给对方。", TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,15),Foreground=new SolidColorBrush(Color.FromRgb(55,90,73)) };
        grid.Children.Add(heading);
        var preview=new TextBox { Text=transcript,IsReadOnly=true,AcceptsReturn=true,VerticalScrollBarVisibility=ScrollBarVisibility.Auto };Grid.SetRow(preview,1);grid.Children.Add(preview);
        var buttons=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0) };
        var cancel=new Button { Content="取消",IsCancel=true };var send=new Button { Content="发送这段文字",IsDefault=true };send.Click+=(_,_)=>DialogResult=true;buttons.Children.Add(cancel);buttons.Children.Add(send);Grid.SetRow(buttons,2);grid.Children.Add(buttons);Content=grid;
    }
}
