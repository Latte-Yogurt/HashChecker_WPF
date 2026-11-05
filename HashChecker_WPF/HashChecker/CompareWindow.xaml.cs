using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HashChecker.Models;
using HashChecker.Services;

namespace HashChecker
{
    /// <summary>
    /// 「比较摘要值」对话框：显示该行的当前值与保存值，
    /// 并允许粘贴一个外部摘要值即时比对。
    /// </summary>
    public partial class CompareWindow : Window
    {
        private readonly CheckItem _item;

        /// <summary>用户确认后要写回列表的期望值；取消时为 null。</summary>
        public string AcceptedHash { get; private set; }

        /// <summary>确认值对应的算法；只按长度推出多个候选时为 null。</summary>
        public HashAlgorithmInfo AcceptedAlgorithm { get; private set; }

        public CompareWindow(CheckItem item)
        {
            InitializeComponent();
            _item = item;
            ApplyLocalization();
            LoadItem();
        }

        private void ApplyLocalization()
        {
            Title = Localization.Get("CompareTitle");
            LblFileCaption.Text = Localization.Get("CompareFileLabel");
            LblCurrentCaption.Text = Localization.Get("CompareCurrentLabel");
            LblExpectedCaption.Text = Localization.Get("CompareExpectedLabel");
            LblInputCaption.Text = Localization.Get("CompareInputLabel");
            BtnPaste.Content = Localization.Get("ComparePaste");
            BtnOk.Content = Localization.Get("CompareOk");
            BtnCancel.Content = Localization.Get("CompareCancel");
        }

        private void LoadItem()
        {
            FileNameText.Text = _item.Name;
            CurrentHashText.Text = _item.HasCurrentHash
                ? HashValues.Format(_item.CurrentHash, AppSettingsHost.UpperCase)
                : Localization.Get("MsgNoValue");
            ExpectedHashText.Text = _item.HasExpectedHash
                ? HashValues.Format(_item.ExpectedHash, AppSettingsHost.UpperCase)
                : Localization.Get("MsgNoValue");

            InputBox.Text = _item.HasExpectedHash
                ? HashValues.Format(_item.ExpectedHash, AppSettingsHost.UpperCase)
                : string.Empty;

            UpdateComparison();
        }

        private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateComparison();
        }

        /// <summary>
        /// 实时比对：把输入值规范化后与当前值比较，结果用颜色与文字双重提示。
        /// 长度对上多个算法时（SHA-256 与 SHA3-256 都是 64 位）按候选逐个比，任一个命中即通过。
        /// </summary>
        private void UpdateComparison()
        {
            string raw = InputBox.Text;
            string normalized = HashValues.Normalize(raw);

            if (normalized == null)
            {
                ResultText.Text = Localization.Get("CompareResultPending");
                ResultText.Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x7A));
                BtnOk.IsEnabled = false;
                return;
            }

            BtnOk.IsEnabled = true;

            if (!_item.HasCurrentHash)
            {
                ResultText.Text = Localization.Get("MsgNoHash");
                ResultText.Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x6D, 0x00));
                return;
            }

            if (Matches(normalized))
            {
                ResultText.Text = Localization.Get("CompareResultMatch");
                ResultText.Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x7F, 0x3B));
            }
            else
            {
                ResultText.Text = Localization.Get("CompareResultMismatch");
                ResultText.Foreground = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
            }
        }

        /// <summary>输入值与当前值是否一致；同长度多算法时按候选逐个比。</summary>
        private bool Matches(string normalized)
        {
            return _item.MatchesDigest(normalized);
        }

        private void Paste_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 剪贴板被别的程序占用时取文本会抛异常，忽略即可，粘贴失败不该让对话框崩掉。
                if (Clipboard.ContainsText())
                {
                    InputBox.Text = Clipboard.GetText().Trim();
                    InputBox.CaretIndex = InputBox.Text.Length;
                }
            }
            catch
            {
                /* 剪贴板被别的程序占用时读取会抛，忽略后用户仍可手工粘贴 */
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            string normalized = HashValues.Normalize(InputBox.Text);
            if (normalized == null)
            {
                MessageBox.Show(this, Localization.Get("CompareInvalid"),
                    Localization.Get("MsgWarningTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            AcceptedHash = normalized;

            List<HashAlgorithmInfo> candidates = HashAlgorithms.CandidatesFor(normalized);
            AcceptedAlgorithm = candidates.Count == 1 ? candidates[0] : null;

            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            InputBox.Focus();
            InputBox.SelectAll();
        }
    }
}
