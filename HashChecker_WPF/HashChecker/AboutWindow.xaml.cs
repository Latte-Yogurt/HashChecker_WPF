using System;
using System.Reflection;
using System.Windows;

namespace HashChecker
{
    /// <summary>关于窗口：显示版本号与程序用途。</summary>
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();
            ApplyLocalization();
        }

        private void ApplyLocalization()
        {
            Title = Localization.Get("AboutTitle");
            BtnOk.Content = Localization.Get("AboutOk");

            BodyText.Text = Localization.Format("AboutBody", VersionText);
        }

        /// <summary>
        /// 版本号文本，取自**本程序集**的 AssemblyVersion，构建时自动带出来。
        ///
        /// 为什么不用 Assembly.GetEntryAssembly()：它返回的是「启动这个进程的程序集」。
        /// 正常双击运行时那确实就是本程序，但只要有别的东西用 Assembly.LoadFrom 载入本程序
        /// （测试探针就是这么做的），入口程序集就变成了那个宿主，版本号会取成宿主的
        /// ——探针场景下实测显示「版本 0.0.0.0」。typeof(AboutWindow).Assembly 永远是本程序集，
        /// 与谁启动进程无关。
        ///
        /// 修完这个缺陷后，改版本号只需要动 Properties/AssemblyInfo.cs 一处，
        /// 关于窗口会自动跟着变，不必再回来改这里。
        /// </summary>
        private static string VersionText
        {
            get
            {
                Version version = typeof(AboutWindow).Assembly.GetName().Version;
                if (version == null) return Localization.Get("StatusUnknown");
                return version.ToString();
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
