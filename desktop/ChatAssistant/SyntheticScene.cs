using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChatAssistant.Core;
using ChatAssistant.Platform;

namespace ChatAssistant;
public static class SyntheticScene
{
    public static readonly NormalizedRegion Header=new(.32,.025,.64,.08);
    public static readonly NormalizedRegion Messages=new(.32,.14,.65,.69);
    public static CaptureFrame Render(string name,IReadOnlyList<ObservedMessage> messages)
    {
        const int width=1000,height=700;var visual=new DrawingVisual();
        using(var dc=visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(245,245,245)),null,new Rect(0,0,width,height));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(229,233,230)),null,new Rect(0,0,300,height));
            Draw(dc,"模拟聊天窗口",22,24,25);Draw(dc,"仅用于验证，不连接微信",22,70,17);Draw(dc,name,340,25,26);
            double y=110;
            foreach(var message in messages.TakeLast(4))
            {
                var text=Text(message.Text,23);text.MaxTextWidth=410;
                var w=Math.Min(445,text.Width+30);var h=text.Height+24;
                var x=message.Speaker=="self"?960-w:350;
                var fill=message.Speaker=="self"?Color.FromRgb(149,236,105):Color.FromRgb(255,255,255);
                dc.DrawRoundedRectangle(new SolidColorBrush(fill),null,new Rect(x,y,w,h),8,8);
                dc.DrawText(text,new Point(x+15,y+12));y+=h+25;
            }
            dc.DrawRectangle(Brushes.White,null,new Rect(301,590,699,110));Draw(dc,"输入框（不在识别区域内）",340,620,18);
        }
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
        var data=new byte[width*height*4];bitmap.CopyPixels(data,width*4,0);
        return new CaptureFrame(width,height,data,"模拟聊天窗口",DateTimeOffset.UtcNow);
    }
    static FormattedText Text(string s,double size)=>new(s,CultureInfo.GetCultureInfo("zh-CN"),FlowDirection.LeftToRight,new Typeface("Microsoft YaHei UI"),size,Brushes.Black,1);
    static void Draw(DrawingContext dc,string s,double x,double y,double size)=>dc.DrawText(Text(s,size),new Point(x,y));
}
