using System;
using System.Globalization;
using System.Windows;
using HashChecker.Services;

namespace HashChecker
{
    /// <summary>
    /// 选项窗口：线程数、大小写、自动开始、自动载入同目录记录、BOM、窗口记忆与默认算法。
    /// 「记住窗口位置与大小」的开关只影响下次启动时的还原，关闭后不再覆盖已有记录，
    /// 但也不主动清除，方便用户反复开关试。
    /// </summary>
    public partial class OptionsWindow : Window
    {
        public OptionsWindow()
        {
            InitializeComponent();
            ApplyLocalization();
            LoadFromSettings();
        }

        private void ApplyLocalization()
        {
            Title = Localization.Get("OptTitle");
            LblThreads.Text = Localization.Get("OptThreads");
            LblDefaultAlgorithm.Text = Localization.Get("OptAlgorithm");
            ChkUpperCase.Content = Localization.Get("OptUppercase");
            ChkAutoStart.Content = Localization.Get("OptAutoStart");
            ChkAutoCompanion.Content = Localization.Get("OptAutoLoadSidecar");
            ChkBom.Content = Localization.Get("OptBom");
            ChkRememberPlacement.Content = Localization.Get("OptRememberWindow");
            BtnOk.Content = Localization.Get("OptOk");
            BtnCancel.Content = Localization.Get("OptCancel");
        }

        private void LoadFromSettings()
        {
            AppSettings settings = AppSettingsHost.Current;

            int max = Math.Max(AppSettings.MaxThreads, 8);
            ThreadSlider.Minimum = 1;
            ThreadSlider.Maximum = max;
            ThreadSlider.Value = Math.Min(Math.Max(settings.Threads, 1), max);

            FillAlgorithmPicker(settings);

            ChkUpperCase.IsChecked = settings.UpperCase;
            ChkAutoStart.IsChecked = settings.AutoStartAfterAdd;
            ChkAutoCompanion.IsChecked = settings.AutoLoadCompanion;
            ChkBom.IsChecked = settings.WriteBom;
            ChkRememberPlacement.IsChecked = settings.RememberWindow;

            ThreadHint.Text = Localization.Format("MsgThreadsHint", max);
            UpdateThreadLabel();
        }

        /// <summary>填默认算法下拉框，选中设置里记住的那一项。</summary>
        private void FillAlgorithmPicker(AppSettings settings)
        {
            foreach (HashAlgorithmInfo info in HashAlgorithms.All)
            {
                AlgorithmPicker.Items.Add(info);
            }

            HashAlgorithmInfo preferred = HashAlgorithms.ById(settings.AlgorithmId) ?? HashAlgorithms.Default;
            foreach (object candidate in AlgorithmPicker.Items)
            {
                HashAlgorithmInfo info = candidate as HashAlgorithmInfo;
                if (info != null && info.Id == preferred.Id)
                {
                    AlgorithmPicker.SelectedItem = candidate;
                    break;
                }
            }

            if (AlgorithmPicker.SelectedItem == null) AlgorithmPicker.SelectedIndex = 0;
        }

        private void ThreadSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateThreadLabel();
        }

        private void UpdateThreadLabel()
        {
            if (ThreadValue == null) return;
            ThreadValue.Text = ((int)ThreadSlider.Value).ToString(CultureInfo.CurrentCulture);
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            AppSettings settings = AppSettingsHost.Current;

            settings.Threads = (int)ThreadSlider.Value;
            settings.UpperCase = ChkUpperCase.IsChecked == true;
            settings.AutoStartAfterAdd = ChkAutoStart.IsChecked == true;
            settings.AutoLoadCompanion = ChkAutoCompanion.IsChecked == true;
            settings.WriteBom = ChkBom.IsChecked == true;
            settings.RememberWindow = ChkRememberPlacement.IsChecked == true;

            HashAlgorithmInfo algorithm = AlgorithmPicker.SelectedItem as HashAlgorithmInfo;
            if (algorithm != null) settings.AlgorithmId = algorithm.Id;

            settings.Normalize();
            settings.Save();

            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
