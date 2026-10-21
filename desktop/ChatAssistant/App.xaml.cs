using System.Windows;

namespace ChatAssistant;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            (MainWindow as MainWindow)?.PauseAfterError();
            MessageBox.Show("助手遇到问题，已停止本次操作。请暂停后重新选择窗口。\n" + args.Exception.GetType().Name, "聊天助手");
            args.Handled = true;
        };
        var main = new MainWindow(e.Args);
        MainWindow = main;
        main.Show();
    }
}
