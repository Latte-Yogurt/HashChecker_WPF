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

            // 日志目录可能被清理工具删掉，每次启动确保存在并顺手清掉过期的问题日志。
            AppPaths.EnsureLogDirectory();
            LogService.CleanupOldLogs();

            var logger = new LogService();

            // 未捕获异常兜底：弹窗提示后把异常标记为已处理，避免出现系统级崩溃对话框。
            // 弹窗之外必须落日志 —— 用户只会说「它弹了个框」，没有日志就无从查起。
            DispatcherUnhandledException += (s, args) =>
            {
                try
                {
                    logger.WriteLog(args.Exception.ToString(), LogService.ErrorLevel.Error);
                }
                catch
                {
                    // 日志再失败也不能让兜底逻辑本身抛出去。
                }

                MessageBox.Show(args.Exception.Message,
                    Localization.Get("MsgErrorTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };

            AppSettingsHost.Current.Normalize();

            // 语言必须在建任何窗口之前生效：控件构造时就调 ApplyLocalization，
            // 晚一步界面就会以系统语言渲染一遍再被改写。
            Localization.ApplyConfiguredLanguage(AppSettingsHost.Current.Language);

            logger.WriteLog(
                Localization.Format("LogStartup", typeof(App).Assembly.GetName().Version),
                LogService.ErrorLevel.Info);

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
