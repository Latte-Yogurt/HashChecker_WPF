using System.Windows;
using HashChecker.Services;

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

            string version;
            try
            {
                version = System.Reflection.Assembly.GetEntryAssembly()
                    .GetName().Version.ToString();
            }
            catch
            {
                // 取不到程序集版本（极少见）时退回固定串，关于窗口不该因此打不开。
                version = "1.2.0.0";
            }

            BodyText.Text = Localization.Format("AboutBody", version);
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
