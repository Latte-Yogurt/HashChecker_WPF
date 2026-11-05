using System;
using System.Windows;
using HashChecker.Services;

namespace HashChecker
{
    /// <summary>
    /// 应用入口。App.xaml 里没有 StartupUri，主窗口在这里创建，
    /// 因为要先把命令行参数拿进窗口。
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 未捕获异常兜底：弹窗提示后把异常标记为已处理，避免出现系统级崩溃对话框。
            DispatcherUnhandledException += (s, args) =>
            {
                MessageBox.Show(args.Exception.Message,
                    Localization.Get("MsgErrorTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };

            AppSettingsHost.Current.Normalize();

            var window = new MainWindow();
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();

            if (e.Args != null && e.Args.Length > 0)
            {
                window.AddStartupPaths(e.Args);
            }
        }
    }
}
