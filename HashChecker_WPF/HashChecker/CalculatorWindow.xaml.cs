using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using HashChecker.Models;
using HashChecker.Services;

namespace HashChecker
{
    /// <summary>
    /// 摘要计算器：对一段文本（按 UTF-8）或一个文件按当前算法求值。
    /// </summary>
    public partial class CalculatorWindow : Window
    {
        private bool _suppressAlgorithmEvent;

        public CalculatorWindow()
        {
            InitializeComponent();
            ApplyLocalization();
            FillAlgorithmPicker();
        }

        private void ApplyLocalization()
        {
            Title = Localization.Get("CalcTitle");
            LblAlgorithmCaption.Text = Localization.Get("LblAlgorithm") + "：";
            LblTextCaption.Text = Localization.Get("CalcTextLabel");
            LblResultCaption.Text = Localization.Get("CalcResultLabel");
            BtnFromFile.Content = Localization.Get("CalcFromFile");
            BtnCompute.Content = Localization.Get("CalcCompute");
            BtnCopy.Content = Localization.Get("CalcCopy");
            BtnClose.Content = Localization.Get("CalcClose");
            HintText.Text = Localization.Get("CalcHint");
        }

        /// <summary>填算法下拉框，默认跟主窗口设置里记住的那一个一致。</summary>
        private void FillAlgorithmPicker()
        {
            _suppressAlgorithmEvent = true;
            try
            {
                foreach (HashAlgorithmInfo info in HashAlgorithms.All)
                {
                    AlgorithmPicker.Items.Add(info);
                }

                HashAlgorithmInfo preferred =
                    HashAlgorithms.ById(AppSettingsHost.Current.AlgorithmId) ?? HashAlgorithms.Default;

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
            finally
            {
                _suppressAlgorithmEvent = false;
            }
        }

        private HashAlgorithmInfo SelectedAlgorithm()
        {
            HashAlgorithmInfo info = AlgorithmPicker.SelectedItem as HashAlgorithmInfo;
            return info ?? HashAlgorithms.Default;
        }

        private void AlgorithmPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressAlgorithmEvent) return;
            if (IsLoaded) ComputeText();
        }

        private void ComputeText()
        {
            try
            {
                string hash = HashValues.HashText(InputText.Text ?? string.Empty, SelectedAlgorithm());
                ResultBox.Text = HashValues.Format(hash, AppSettingsHost.UpperCase);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Localization.Get("MsgErrorTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>输入即算：文本求值很快，没必要让用户再点一次「计算」。</summary>
        private void Input_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (IsLoaded) ComputeText();
        }

        private void Compute_Click(object sender, RoutedEventArgs e)
        {
            ComputeText();
        }

        private void FromFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = Localization.Get("CalcFromFile"),
                Filter = Localization.Get("MsgFileFilter"),
                Multiselect = false
            };

            if (dialog.ShowDialog(this) != true) return;

            ComputeFromFile(dialog.FileName, SelectedAlgorithm());
        }

        /// <summary>
        /// 读一个文件求值。必须异步：大文件配 SHA3 要算好几秒，同步做会把 UI 线程占满，
        /// 窗口不重绘、按钮点不动——用户看到的就是「卡死」。计算期间禁用按钮，避免重复起算。
        /// </summary>
        private async void ComputeFromFile(string path, HashAlgorithmInfo algorithm)
        {
            Cursor = Cursors.Wait;
            BtnFromFile.IsEnabled = false;
            BtnCompute.IsEnabled = false;

            try
            {
                string hash = await Task.Run(() =>
                {
                    using (var stream = new FileStream(path, FileMode.Open,
                               FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan))
                    {
                        IHashCore core = algorithm.CreateCore();
                        core.Initialize();

                        byte[] buffer = new byte[1024 * 1024];
                        int read;
                        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            core.Append(buffer, 0, read);
                        }

                        return HashValues.ToHex(core.Finish());
                    }
                });

                // 计算期间窗口可能已被关掉，这时不必再回写界面。
                if (!IsLoaded) return;

                ResultBox.Text = HashValues.Format(hash, AppSettingsHost.UpperCase);

                FileInfo info = new FileInfo(path);
                FileText.Text = string.Format("{0}  ({1})  {2}",
                    path, HashValues.FormatSize(info.Length), algorithm.DisplayName);
            }
            catch (Exception ex)
            {
                if (IsLoaded)
                {
                    MessageBox.Show(this, ex.Message, Localization.Get("MsgErrorTitle"),
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally
            {
                Cursor = Cursors.Arrow;
                BtnFromFile.IsEnabled = true;
                BtnCompute.IsEnabled = true;
            }
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(ResultBox.Text)) return;

            try
            {
                Clipboard.SetText(ResultBox.Text);
            }
            catch
            {
                /* 剪贴板被别的程序占用时复制失败，静默忽略即可 */
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
