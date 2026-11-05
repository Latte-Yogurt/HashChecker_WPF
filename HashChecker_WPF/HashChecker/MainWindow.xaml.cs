using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using HashChecker.Models;
using HashChecker.Services;

namespace HashChecker
{
    public partial class MainWindow : Window
        #region 字段
    {

        private readonly ObservableCollection<CheckItem> _allItems = new ObservableCollection<CheckItem>();
        private readonly ListCollectionView _view;
        private readonly HashingEngine _engine = new HashingEngine();

        private CancellationTokenSource _hashCts;
        private bool _isHashing;
        private string _currentFilter = "All";
        private string _lastChecksumPath;
        private bool _suppressFilterEvent;
        private bool _suppressAlgorithmEvent;
        private ListRubberBand _rubberBand;

        #endregion
        #region 构造与初始化
        public MainWindow()
        {
            InitializeComponent();

            _view = (ListCollectionView)CollectionViewSource.GetDefaultView(_allItems);
            _view.Filter = FilterPredicate;
            _view.SortDescriptions.Add(new SortDescription("Sequence", ListSortDirection.Ascending));
            ItemList.ItemsSource = _view;

            ApplyLocalization();
            HookShortcuts();
            FillAlgorithmPicker();
            InstallRubberBand();

            _allItems.CollectionChanged += AllItems_CollectionChanged;
            UpdateEmptyState();
            UpdateCounters();
            UpdateCommandStates();
        }

        private void InstallRubberBand()
        {
            _rubberBand = new ListRubberBand(ItemList);

            Loaded += (s, e) =>
            {
                if (_rubberBand == null) return;

                Grid host = ItemList.Parent as Grid;
                if (host != null) _rubberBand.AttachOverlay(host, Grid.GetRow(ItemList));
            };
        }

        private void FillAlgorithmPicker()
        {
            _suppressAlgorithmEvent = true;
            try
            {
                foreach (HashAlgorithmInfo info in HashAlgorithms.All)
                {
                    AlgorithmPicker.Items.Add(info);
                }

                HashAlgorithmInfo preferred = HashAlgorithms.ById(Settings.AlgorithmId) ?? HashAlgorithms.Default;

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

        #endregion

        #region 算法选择
        /// <summary>下拉框切换后，界面上每一行都要跟着换算法并清掉旧值，否则会拿旧值误判成通过。</summary>
        private void AlgorithmPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressAlgorithmEvent) return;

            HashAlgorithmInfo selected = SelectedAlgorithm();

            Settings.AlgorithmId = selected.Id;

            foreach (CheckItem item in _allItems)
            {
                item.SelectedAlgorithm = selected;
                item.ClearComputedHash();
            }

            UpdateCounters();
            if (_currentFilter != "All") _view.Refresh();

            // 切换算法是用户主动动作，状态栏给一句明确回执，免得他以为点了没反应。
            StatusRight.Text = Localization.Format("MsgAlgorithmChanged", selected.DisplayName);
        }

        /// <summary>设置走进程级单例，窗口与行模型读的是同一份，避免传参。</summary>
        private AppSettings Settings
        {
            get { return AppSettingsHost.Current; }
        }

        #endregion

        #region 窗口尺寸
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            RestoreWindowPlacement();
            UpdateEmptyState();
            FitColumnsToWidth();
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            FitColumnsToWidth();
        }

        /// <summary>列宽之和必须落在视口宽度内，否则会出现横向滚动条，把大小与日期列挤出可视区。</summary>
        private void FitColumnsToWidth()
        {
            if (ColName == null || ItemList == null) return;

            double available = ViewportWidth();
            if (available <= 0) return;
            if (available < 480) return;

            const double sizeWidth = 92;
            const double modifiedWidth = 142;
            const double algorithmWidth = 96;
            double fixedPart = sizeWidth + modifiedWidth + algorithmWidth;
            double flexPart = available - fixedPart;

            if (flexPart < 300) return;

            ColName.Width = flexPart * 0.13;
            ColFolder.Width = flexPart * 0.12;
            ColAlgorithm.Width = algorithmWidth;
            ColCurrent.Width = flexPart * 0.375;
            ColSaved.Width = flexPart * 0.375;
            ColSize.Width = sizeWidth;

            double used = ColName.Width + ColFolder.Width + ColAlgorithm.Width
                + ColCurrent.Width + ColSaved.Width + sizeWidth;
            ColModified.Width = Math.Max(modifiedWidth, available - used);
        }

        /// <summary>取 ScrollViewer 的视口宽度而不是 ListView 的 ActualWidth：后者含滚动条宽度。</summary>
        private double ViewportWidth()
        {
            ScrollViewer viewer = FindScrollViewer(ItemList);
            if (viewer != null && viewer.ViewportWidth > 0)
            {
                return viewer.ViewportWidth - 1;
            }

            double listWidth = ItemList.ActualWidth;
            if (listWidth <= 0) return 0;
            return listWidth - SystemParameters.VerticalScrollBarWidth - 1;
        }

        /// <summary>按可视树递归找 ListView 内部的 ScrollViewer。</summary>
        private static ScrollViewer FindScrollViewer(DependencyObject root)
        {
            if (root == null) return null;

            ScrollViewer direct = root as ScrollViewer;
            if (direct != null) return direct;

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                ScrollViewer found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
                if (found != null) return found;
            }
            return null;
        }

        #endregion

        #region 关闭与启动路径
        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_isHashing) StopHashing();

            SaveWindowPlacement();
            Settings.Save();
        }

        /// <summary>命令行与「拖到图标上」共用这里；非法路径静默丢弃，不让一个坏参数挡住窗口。</summary>
        public void AddStartupPaths(string[] paths)
        {
            if (paths == null || paths.Length == 0) return;

            var valid = new List<string>();
            foreach (string path in paths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                try
                {
                    string full = Path.GetFullPath(path.Trim().Trim('"'));
                    if (File.Exists(full) || Directory.Exists(full)) valid.Add(full);
                }
                catch
                {
                    /* 非法路径（含 null 字符、超长、非法字符）直接忽略，不让一个坏参数挡住整批添加 */
                }
            }

            if (valid.Count == 0) return;

            AddPaths(valid, false);
            if (Settings.AutoStartAfterAdd) _ = BeginHashingAsync();
        }

        #endregion

        #region 本地化
        private void ApplyLocalization()
        {
            Title = Localization.Get("Title");

            MenuFile.Header = Localization.Get("MenuFile");
            MenuFileSave.Header = Localization.Get("FileSave");
            MenuFileSaveItem.Header = Localization.Get("FileSaveItem");
            MenuFileSaveAs.Header = Localization.Get("FileSaveAs");
            MenuFileExit.Header = Localization.Get("FileExit");

            MenuEdit.Header = Localization.Get("MenuEdit");
            MenuEditAdd.Header = Localization.Get("EditAdd");
            MenuEditRemove.Header = Localization.Get("EditRemove");
            MenuEditClear.Header = Localization.Get("EditClear");
            MenuEditStart.Header = Localization.Get("EditStart");
            MenuEditStop.Header = Localization.Get("EditStop");
            MenuEditCompare.Header = Localization.Get("EditCompare");
            MenuEditCopy.Header = Localization.Get("EditCopy");
           MenuEditCopyPath.Header = Localization.Get("EditCopyPath");
            MenuEditSelectAll.Header = Localization.Get("EditSelectAll");

            MenuView.Header = Localization.Get("MenuView");
            MenuViewAll.Header = Localization.Get("ViewAll");
            MenuViewUnavailable.Header = Localization.Get("ViewUnavailable");
            MenuViewUnknown.Header = Localization.Get("ViewUnknown");
            MenuViewLoaded.Header = Localization.Get("ViewLoaded");
            MenuViewNew.Header = Localization.Get("ViewNew");
            MenuViewFailed.Header = Localization.Get("ViewFailed");
            MenuViewPassed.Header = Localization.Get("ViewPassed");
            MenuViewLocate.Header = Localization.Get("ViewLocate");
            MenuViewProperties.Header = Localization.Get("ViewProperties");

            MenuTools.Header = Localization.Get("MenuTools");
            MenuToolsSelfTest.Header = Localization.Get("ToolsSelfTest");
            MenuToolsCalculator.Header = Localization.Get("ToolsCalculator");
            MenuToolsOptions.Header = Localization.Get("ToolsOptions");

            MenuHelp.Header = Localization.Get("MenuHelp");
            MenuHelpAbout.Header = Localization.Get("HelpAbout");

            LblSave.Text = Localization.Get("BtnSave");
            LblSaveItem.Text = Localization.Get("BtnSaveItem");
            LblSaveAs.Text = Localization.Get("BtnSaveAs");
            LblAdd.Text = Localization.Get("BtnAdd");
            LblRemove.Text = Localization.Get("BtnRemove");
            LblClear.Text = Localization.Get("BtnClear");
            LblStart.Text = Localization.Get("BtnStart");
            LblStop.Text = Localization.Get("BtnStop");
            LblCopy.Text = Localization.Get("BtnCopy");
           LblLocate.Text = Localization.Get("BtnLocate");
            LblOptions.Text = Localization.Get("BtnOptions");
            LblSelfTest.Text = Localization.Get("BtnSelfTest");
            LblAlgorithm.Text = Localization.Get("LblAlgorithm");

            BtnSave.ToolTip = Localization.Get("BtnSave");
            BtnSaveItem.ToolTip = Localization.Get("BtnSaveItem");
            BtnSaveAs.ToolTip = Localization.Get("BtnSaveAs");
            BtnAdd.ToolTip = Localization.Get("BtnAdd");
            BtnRemove.ToolTip = Localization.Get("BtnRemove");
            BtnClear.ToolTip = Localization.Get("BtnClear");
            BtnStart.ToolTip = Localization.Get("EditStart");
            BtnStop.ToolTip = Localization.Get("EditStop");
            BtnCopy.ToolTip = Localization.Get("BtnCopy");
           BtnLocate.ToolTip = Localization.Get("BtnLocate");
            BtnOptions.ToolTip = Localization.Get("BtnOptions");
            BtnSelfTest.ToolTip = Localization.Get("BtnSelfTest");

            ColName.Header = Localization.Get("ColName");
            ColAlgorithm.Header = Localization.Get("ColAlgorithm");
            ColFolder.Header = Localization.Get("ColFolder");
            ColCurrent.Header = Localization.Get("ColCurrent");
            ColSaved.Header = Localization.Get("ColSaved");
            ColSize.Header = Localization.Get("ColSize");
            ColModified.Header = Localization.Get("ColModified");

            EmptyHint.Text = Localization.Get("MsgDropHint");
            StatusRight.Text = Localization.Get("StReady");
        }

        private void UpdateFilterLabels(int all, int unavailable, int unknown, int loaded, int newly, int failed, int passed)
        {
            LblFilterAll.Text = Localization.Format("FilterCount", Localization.Get("FilterAll"), all);
            LblFilterUnavailable.Text = Localization.Format("FilterCount", Localization.Get("FilterUnavailable"), unavailable);
            LblFilterUnknown.Text = Localization.Format("FilterCount", Localization.Get("FilterUnknown"), unknown);
            LblFilterLoaded.Text = Localization.Format("FilterCount", Localization.Get("FilterLoaded"), loaded);
            LblFilterNew.Text = Localization.Format("FilterCount", Localization.Get("FilterNew"), newly);
            LblFilterFailed.Text = Localization.Format("FilterCount", Localization.Get("FilterFailed"), failed);
            LblFilterPassed.Text = Localization.Format("FilterCount", Localization.Get("FilterPassed"), passed);
        }

        #endregion

        #region 快捷键
        /// <summary>用 InputBinding 注册而不是监听 KeyDown，输入框里打字时才不会被主窗口抢走按键。</summary>
        private void HookShortcuts()
        {
            AddShortcut(Key.S, ModifierKeys.Control, Save_Click);
            AddShortcut(Key.S, ModifierKeys.Control | ModifierKeys.Shift, SaveItem_Click);
            AddShortcut(Key.Insert, ModifierKeys.None, Add_Click);
            AddShortcut(Key.Delete, ModifierKeys.None, Remove_Click);
            AddShortcut(Key.Delete, ModifierKeys.Control, Remove_Click);
            AddShortcut(Key.N, ModifierKeys.Control, Clear_Click);
            AddShortcut(Key.F2, ModifierKeys.None, Compare_Click);
            AddShortcut(Key.F3, ModifierKeys.None, Locate_Click);
            AddShortcut(Key.Enter, ModifierKeys.Alt, Properties_Click);
            AddShortcut(Key.C, ModifierKeys.Control, Copy_Click);
            AddShortcut(Key.A, ModifierKeys.Control, SelectAll_Click);
            AddShortcut(Key.Enter, ModifierKeys.None, Start_Click);
            AddShortcut(Key.Escape, ModifierKeys.None, Stop_Click);
        }

        private void AddShortcut(Key key, ModifierKeys modifiers, RoutedEventHandler handler)
        {
            string name = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "shortcut_{0}_{1}", key, modifiers);

            var command = new RoutedCommand(name, typeof(MainWindow));
            InputBindings.Add(new KeyBinding(command, key, modifiers));
            CommandBindings.Add(new CommandBinding(command, (s, e) =>
            {
                handler(s, new RoutedEventArgs());
                e.Handled = true;
            }));
        }

        #endregion

        #region 筛选
        private bool FilterPredicate(object candidate)
        {
            CheckItem item = candidate as CheckItem;
            if (item == null) return false;

            switch (_currentFilter)
            {
                case "Unavailable": return item.Status == CheckStatus.Unavailable;
                case "Unknown": return item.Status == CheckStatus.Unknown;
                case "Loaded": return item.Status == CheckStatus.Loaded;
                case "New": return item.Status == CheckStatus.New;
                case "Failed": return item.Status == CheckStatus.Failed;
                case "Passed": return item.Status == CheckStatus.Passed;
                default: return true;
            }
        }

        #endregion

        #region 列表状态
        /// <summary>增删时同步订阅与退订属性变化，否则已删的行仍会参与计数。</summary>
        private void AllItems_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (object removed in e.OldItems)
                {
                    CheckItem item = removed as CheckItem;
                    if (item != null) item.PropertyChanged -= Item_PropertyChanged;
                }
            }

            if (e.NewItems != null)
            {
                foreach (object added in e.NewItems)
                {
                    CheckItem item = added as CheckItem;
                    if (item != null) item.PropertyChanged += Item_PropertyChanged;
                }
            }

            UpdateEmptyState();
            UpdateCounters();
            UpdateCommandStates();
        }

        private void Item_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "Status")
            {
                UpdateCounters();
                if (_currentFilter != "All") _view.Refresh();
            }
        }

        private void UpdateCounters()
        {
            int all = _allItems.Count;
            int unavailable = 0, unknown = 0, loaded = 0, newly = 0, failed = 0, passed = 0;

            foreach (CheckItem item in _allItems)
            {
                switch (item.Status)
                {
                    case CheckStatus.Unavailable: unavailable++; break;
                    case CheckStatus.Unknown: unknown++; break;
                    case CheckStatus.Loaded: loaded++; break;
                    case CheckStatus.New: newly++; break;
                    case CheckStatus.Failed: failed++; break;
                    case CheckStatus.Passed: passed++; break;
                }
            }

            UpdateFilterLabels(all, unavailable, unknown, loaded, newly, failed, passed);
            StatusMain.Text = Localization.Format("StObjects", all);
        }

        private void UpdateEmptyState()
        {
            bool empty = _allItems.Count == 0;
            EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>按钮与菜单项的可用状态集中在这里推导，避免各入口各写一份判断。</summary>
        private void UpdateCommandStates()
        {
            bool hasSelection = ItemList.SelectedItems.Count > 0;
            bool hasItems = _allItems.Count > 0;

            BtnStart.IsEnabled = hasItems && !_isHashing;
            BtnStop.IsEnabled = _isHashing;
            BtnSave.IsEnabled = hasItems;
            BtnSaveItem.IsEnabled = hasSelection;
            BtnSaveAs.IsEnabled = hasItems;
            BtnRemove.IsEnabled = hasSelection && !_isHashing;
            BtnClear.IsEnabled = hasItems && !_isHashing;
            BtnCopy.IsEnabled = hasSelection;
           BtnLocate.IsEnabled = hasSelection;

            MenuEditStart.IsEnabled = BtnStart.IsEnabled;
            MenuEditStop.IsEnabled = BtnStop.IsEnabled;
            MenuEditRemove.IsEnabled = BtnRemove.IsEnabled;
            MenuEditClear.IsEnabled = BtnClear.IsEnabled;
            MenuEditCompare.IsEnabled = hasSelection;
            MenuEditCopy.IsEnabled = hasSelection;
           MenuEditCopyPath.IsEnabled = hasSelection;
            MenuEditSelectAll.IsEnabled = hasItems;
            MenuFileSave.IsEnabled = BtnSave.IsEnabled;
            MenuFileSaveItem.IsEnabled = hasSelection;
            MenuFileSaveAs.IsEnabled = hasItems;
            MenuViewLocate.IsEnabled = hasSelection;
            MenuViewProperties.IsEnabled = hasSelection;
        }

        private void ItemList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateCommandStates();

            int selected = ItemList.SelectedItems.Count;
            if (selected > 0 && !_isHashing)
            {
                StatusRight.Text = Localization.Format("StSelected", selected);
            }
        }

        #endregion

        #region 筛选同步
        /// <summary>筛选条与菜单两个入口共用 ApplyFilter，标签取自 Tag，写错就当「全部」。</summary>
        private void FilterBar_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressFilterEvent) return;

            ToggleButton source = sender as ToggleButton;
            if (source == null) return;
            ApplyFilter(source.Tag as string ?? "All");
        }

        private void FilterMenu_Click(object sender, RoutedEventArgs e)
        {
            MenuItem source = sender as MenuItem;
            if (source == null) return;
            ApplyFilter(source.Tag as string ?? "All");
        }

        /// <summary>程序化改写 IsChecked 会再触发一次 Click，用 _suppressFilterEvent 把这一轮回声挡掉。</summary>
        private void SyncFilterBar(string tag)
        {
            _suppressFilterEvent = true;
            try
            {
                FilterAll.IsChecked = tag == "All";
                FilterUnavailable.IsChecked = tag == "Unavailable";
                FilterUnknown.IsChecked = tag == "Unknown";
                FilterLoaded.IsChecked = tag == "Loaded";
                FilterNew.IsChecked = tag == "New";
                FilterFailed.IsChecked = tag == "Failed";
                FilterPassed.IsChecked = tag == "Passed";

                MenuViewAll.IsChecked = tag == "All";
                MenuViewUnavailable.IsChecked = tag == "Unavailable";
                MenuViewUnknown.IsChecked = tag == "Unknown";
                MenuViewLoaded.IsChecked = tag == "Loaded";
                MenuViewNew.IsChecked = tag == "New";
                MenuViewFailed.IsChecked = tag == "Failed";
                MenuViewPassed.IsChecked = tag == "Passed";
            }
            finally
            {
                _suppressFilterEvent = false;
            }
        }

        private void ApplyFilter(string tag)
        {
            if (_view == null) return;

            _currentFilter = tag;
            SyncFilterBar(tag);
            _view.Refresh();
            UpdateCommandStates();
        }

        #endregion

        #region 添加与删除
        private void Add_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = Localization.Get("EditAdd"),
                Multiselect = true,
                Filter = Localization.Get("MsgFileFilter")
            };

            if (dialog.ShowDialog(this) != true) return;
            if (dialog.FileNames.Length == 0) return;

            AddPaths(dialog.FileNames, Settings.AutoStartAfterAdd);
        }

        /// <summary>
        /// 批量添加的唯一入口，按两条路分派：校验记录反推目标文件；普通文件夹同目录找记录。
        /// 同一批内复用解析缓存，避免多个文件反复重读同一份记录。
        /// </summary>
        private void AddPaths(IEnumerable<string> paths, bool autoStart)
        {
            if (paths == null) return;

            var known = new HashSet<string>(
                _allItems.Select(item => item.FullPath),
                StringComparer.OrdinalIgnoreCase);

            int added = 0;
            int fromRecords = 0;
            var notes = new List<string>();

            ChecksumResolver.ResetCache();

            foreach (string raw in paths)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;

                string full;
                try { full = Path.GetFullPath(raw.Trim().Trim('"')); }
                catch { continue; }

                if (known.Contains(full)) continue;

                if (Directory.Exists(full))
                {
                    var folderItem = new CheckItem(full);
                    known.Add(full);
                    _allItems.Add(folderItem);
                    added++;
                    continue;
                }

                if (!File.Exists(full)) continue;

                if (ChecksumResolver.LooksLikeRecord(full))
                {
                    UpdatedCount = 0;
                    int resolved = AddRecordTargets(full, known, notes);

                    if (resolved > 0 || UpdatedCount > 0)
                    {
                        added += resolved;
                        fromRecords += resolved + UpdatedCount;
                        continue;
                    }

                }

                var item = new CheckItem(full);

                item.SelectedAlgorithm = SelectedAlgorithm();

                TryLoadRecord(item);

                known.Add(full);
                _allItems.Add(item);
                added++;
            }

            UpdateEmptyState();
            UpdateCounters();
            UpdateCommandStates();

            if (added > 0 || fromRecords > 0)
            {
                ChecksumResolver.ResetCache();

                StatusRight.Text = notes.Count > 0
                    ? string.Join("；", notes)
                    : Localization.Get("StReady");

                if (autoStart || fromRecords > 0) _ = BeginHashingAsync();
                return;
            }

            ChecksumResolver.ResetCache();

            if (notes.Count > 0)
            {
                ShowInfo(Localization.Get("MsgNoticeTitle"), string.Join(Environment.NewLine, notes));
            }
        }

        #endregion

        #region 删除与清空
        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            if (_isHashing)
            {
                ShowInfo(Localization.Get("MsgWarningTitle"), Localization.Get("MsgHashRunning"));
                return;
            }

            var selected = SelectedItems();
            if (selected.Count == 0)
            {
                ShowInfo(Localization.Get("MsgNoticeTitle"), Localization.Get("MsgNoSelection"));
                return;
            }

            foreach (CheckItem item in selected)
            {
                item.PropertyChanged -= Item_PropertyChanged;
                _allItems.Remove(item);
            }

            UpdateEmptyState();
            UpdateCounters();
            UpdateCommandStates();
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            if (_isHashing)
            {
                ShowInfo(Localization.Get("MsgWarningTitle"), Localization.Get("MsgHashRunning"));
                return;
            }

            if (_allItems.Count == 0) return;

            MessageBoxResult confirm = MessageBox.Show(
                this,
                Localization.Format("MsgClearConfirm", _allItems.Count),
                Localization.Get("MsgNoticeTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            foreach (CheckItem item in _allItems)
            {
                item.PropertyChanged -= Item_PropertyChanged;
            }

            _allItems.Clear();
            UpdateEmptyState();
            UpdateCounters();
            UpdateCommandStates();
            StatusRight.Text = Localization.Get("StReady");
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            if (_allItems.Count == 0) return;
            ItemList.SelectAll();
        }

        /// <summary>取选中行并固定成列表，调用方随后可能改动选择。</summary>
        private List<CheckItem> SelectedItems()
        {
            return ItemList.SelectedItems.Cast<CheckItem>().ToList();
        }

        #endregion

        #region 记录反查
        /// <summary>
        /// 由一份记录反推目标文件并加入列表。目标已在列表里时只补期望值，不再加一行。
        /// </summary>
        private int AddRecordTargets(string recordPath, HashSet<string> known, List<string> notes)
        {
            RecordResolution resolution = ChecksumResolver.ResolveRecord(recordPath);

            if (resolution.Resolved.Count == 0)
            {
                if (resolution.Error != null) notes.Add(resolution.Error);
                return 0;
            }

            int added = 0;
            int updated = 0;
            foreach (ResolvedEntry resolved in resolution.Resolved)
            {
                string target = resolved.TargetPath;
                try { target = Path.GetFullPath(target); }
                catch { /* 记录里的路径无法规范化时按原样使用，后面还有多重回退 */ }

                if (known.Contains(target))
                {
                    CheckItem existing = FindItem(target);
                    if (existing != null)
                    {
                        existing.ExpectedHash = resolved.Entry.Hash;
                        existing.ExpectedAlgorithm = resolved.Entry.Algorithm;
                        existing.SourceFile = recordPath;

                        existing.ClearComputedHash();

                        PropagateRecordAlgorithm(resolved.Entry.Algorithm);

                        updated++;
                        continue;
                    }
                }

                var item = new CheckItem(target);
                item.ExpectedHash = resolved.Entry.Hash;
                item.ExpectedAlgorithm = resolved.Entry.Algorithm;
                item.SourceFile = recordPath;

                item.SelectedAlgorithm = resolved.Entry.Algorithm ?? SelectedAlgorithm();

                PropagateRecordAlgorithm(resolved.Entry.Algorithm);

                item.RecomputeStatus();

                known.Add(target);
                _allItems.Add(item);
                added++;
            }

            string summary = Localization.Format("MsgRecordLoaded", resolution.Resolved.Count,
                recordPath, resolution.EncodingName);
            if (resolution.UnresolvedCount > 0)
            {
                summary += " " + Localization.Format("MsgRecordPartial", resolution.UnresolvedCount);
            }
            notes.Add(summary);

            UpdatedCount = updated;
            return added;
        }

        /// <summary>上一次反查里「命中已有行、只更新期望值」的条数，调用方据此判断是否算载入了记录。</summary>
        private int UpdatedCount { get; set; }

        /// <summary>给普通文件找同目录记录；找不到就安静跳过，这是锦上添花的功能，不该弹窗。</summary>
        private void TryLoadRecord(CheckItem item)
        {
            if (!Settings.AutoLoadCompanion) return;

            try
            {
                RecordMatch match = ChecksumResolver.FindRecordForFile(item.FullPath);
                if (match == null)
                {
                    // 没有记录只代表「只算值不判定」，这是正常路径，不是错误：
                    // 用提示文字说清楚，免得用户以为程序漏掉了什么。
                    StatusRight.Text = Localization.Get("MsgNoRecordHint");
                    return;
                }

                item.ExpectedHash = match.Hash;
                item.ExpectedAlgorithm = match.Algorithm;
                item.SourceFile = match.RecordPath;

                item.RecomputeStatus();

                if (match.Algorithm != null)
                {
                    StatusRight.Text = Localization.Format("MsgRecordAlgorithm", match.Algorithm.DisplayName);
                }
            }
            catch
            {
                /* 同目录记录读取失败不影响这一行：文件本身仍可正常计算 */
            }
        }

        /// <summary>让下拉框跟随刚载入的记录算法，只改下拉框，不改任何行。</summary>
        private void PropagateRecordAlgorithm(HashAlgorithmInfo algorithm)
        {
            if (algorithm == null) return;
            if (_suppressAlgorithmEvent) return;

            bool matched = false;

            _suppressAlgorithmEvent = true;
            try
            {
                foreach (object candidate in AlgorithmPicker.Items)
                {
                    HashAlgorithmInfo info = candidate as HashAlgorithmInfo;
                    if (info == null || info.Id != algorithm.Id) continue;

                    AlgorithmPicker.SelectedItem = candidate;
                    matched = true;
                    break;
                }
            }
            finally
            {
                _suppressAlgorithmEvent = false;
            }

            if (matched) Settings.AlgorithmId = algorithm.Id;
        }

        private CheckItem FindItem(string fullPath)
        {
            foreach (CheckItem item in _allItems)
            {
                if (string.Equals(item.FullPath, fullPath, StringComparison.OrdinalIgnoreCase)) return item;
            }
            return null;
        }

        #endregion

        #region 计算
        private async void Start_Click(object sender, RoutedEventArgs e)
        {
            await BeginHashingAsync();
        }

        /// <summary>
        /// 只算还没有当前值的行，已有值的不重算，避免重复扫描大文件；
        /// 计算本身丢到线程池，进度与结果经 Dispatcher 切回 UI 线程。
        /// </summary>
        private async Task BeginHashingAsync()
        {
            if (_isHashing) return;

            var targets = _allItems
                .Where(item => !item.IsDirectory
                               && !item.IsUnavailable
                               && string.IsNullOrEmpty(item.CurrentHash))
                .ToList();

            if (targets.Count == 0)
            {
                string message = _allItems.Count == 0
                    ? Localization.Get("MsgNoHashingTargets")
                    : Localization.Get("MsgNoPendingTargets");
                ShowInfo(Localization.Get("MsgNoticeTitle"), message);
                StatusRight.Text = Localization.Get("StDone");
                return;
            }

            _isHashing = true;
            _hashCts = new CancellationTokenSource();
            UpdateCommandStates();
            UpdateProgress(0);

            CancellationToken token = _hashCts.Token;

            try
            {
                await Task.Run(() =>
                {
                    _engine.Run(
                        targets,
                        Settings.Threads,
                        outcome => Dispatcher.InvokeAsync(() => ApplyOutcome(outcome)),
                        percent => Dispatcher.InvokeAsync(() => UpdateProgress(percent)),
                        token);
                }, token);

                StatusRight.Text = Localization.Get("StDone");
            }
            catch (OperationCanceledException)
            {
                StatusRight.Text = Localization.Get("StStopped");
            }
            catch (Exception ex)
            {
                ShowError(Localization.Get("MsgErrorTitle"), ex.Message);
                StatusRight.Text = Localization.Get("StReady");
            }
            finally
            {
                _isHashing = false;

                if (_hashCts != null)
                {
                    _hashCts.Dispose();
                    _hashCts = null;
                }

                UpdateCommandStates();
                UpdateCounters();

                if (_currentFilter != "All") _view.Refresh();
            }
        }

        /// <summary>单行结果的落地点；失败也写回该行，不打断其余行。</summary>
        private void ApplyOutcome(HashOutcome outcome)
        {
            if (outcome == null || outcome.Item == null) return;

            if (outcome.Success)
            {
                outcome.Item.ApplyHash(outcome.Hash, outcome.Digests, outcome.Algorithm);
            }
            else
            {
                outcome.Item.ApplyError(outcome.Error);
            }
        }

        /// <summary>进度回调是异步投递的，可能晚于「计算完成」，所以只在确实还在算时才改状态文字。</summary>
        private void UpdateProgress(int percent)
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;

            MainProgressBar.Value = percent;

            if (_isHashing) StatusRight.Text = Localization.Format("StHashing", percent);
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            StopHashing();
        }

        private void StopHashing()
        {
            if (_hashCts == null || !_isHashing) return;

            try { _hashCts.Cancel(); }
            catch { /* 已经释放或已经取消时 Cancel 会抛，停止操作本身不需要报错 */ }
        }

        #endregion

        #region 保存
        /// <summary>复用上次路径，但后缀要跟着本次实际算出的算法走，否则会写出「后缀说一种、内容另一种」的文件。</summary>
        private void Save_Click(object sender, RoutedEventArgs e)
        {
            string path = _lastChecksumPath;
            if (!string.IsNullOrEmpty(path))
            {
                string wanted = AlgorithmOf(FirstComputedItem()).Extension;

                if (!string.Equals(Path.GetExtension(path), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    path = Path.ChangeExtension(path, wanted);
                }
            }

            SaveTo(path ?? SuggestChecksumPath(), false);
        }

        /// <summary>取第一个算出了值的行；没有时返回 null，调用方需容错。</summary>
        private CheckItem FirstComputedItem()
        {
            foreach (CheckItem item in _allItems)
            {
                if (item.HasCurrentHash) return item;
            }
            return null;
        }

        private void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            SaveTo(SuggestChecksumPath(), true);
        }

        private void SaveItem_Click(object sender, RoutedEventArgs e)
        {
            var selected = SelectedItems();
            if (selected.Count == 0)
            {
                ShowInfo(Localization.Get("MsgNoticeTitle"), Localization.Get("MsgNoSelection"));
                return;
            }

            var lines = BuildChecksumLines(selected);
            if (lines.Count == 0)
            {
                ShowInfo(Localization.Get("MsgNoticeTitle"), Localization.Get("MsgNothingToSave"));
                return;
            }

            CheckItem first = selected.FirstOrDefault(item => item.HasCurrentHash);
            HashAlgorithmInfo algorithm = AlgorithmOf(first);
            string suggested = first != null
                ? first.FullPath + algorithm.Extension
                : (SuggestChecksumPath() ?? "checksums" + algorithm.Extension);

            SaveAsDialogAndWrite(lines, suggested, algorithm);
        }

        /// <summary>后缀必须按「实际算出这个值的算法」取，而不是按记录后缀推断的算法。</summary>
        private static HashAlgorithmInfo AlgorithmOf(CheckItem item)
        {
            if (item == null) return HashAlgorithms.Default;

            HashAlgorithmInfo computed = item.ComputedAlgorithm;
            if (computed != null) return computed;
            return item.PrimaryAlgorithm;
        }

        private HashAlgorithmInfo SelectedAlgorithm()
        {
            HashAlgorithmInfo info = AlgorithmPicker.SelectedItem as HashAlgorithmInfo;
            return info ?? HashAlgorithms.Default;
        }

        private void SaveAsDialogAndWrite(List<string> lines, string suggested, HashAlgorithmInfo algorithm)
        {
            var dialog = new SaveFileDialog
            {
                Title = Localization.Get("FileSaveAs"),
                Filter = Localization.Get("MsgSaveListFilter"),
                FileName = Path.GetFileName(suggested),
                DefaultExt = Path.GetExtension(suggested),
                AddExtension = true,
                OverwritePrompt = true
            };

            try
            {
                string directory = Path.GetDirectoryName(suggested);
                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                {
                    dialog.InitialDirectory = directory;
                }
            }
            catch
            {
                /* 取不到初始目录就退回系统默认目录，不该因此打不开保存对话框 */
            }

            if (dialog.ShowDialog(this) != true) return;

            WriteChecksumFile(NormalizeChecksumPath(dialog.FileName, algorithm), lines);
        }

        #endregion

        #region 保存路径纠正
        /// <summary>
        /// 把路径后缀纠正成与算法一致。已正确、且名字里没有多余算法后缀时原样返回；
        /// 否则从右往左剥掉所有算法后缀再接上首选后缀，无论几层都能收敛。
        /// </summary>
        private static string NormalizeChecksumPath(string path, HashAlgorithmInfo known)
        {
            if (string.IsNullOrEmpty(path)) return path;

            HashAlgorithmInfo algorithm = known;
            if (algorithm == null) return path;

            string wanted = algorithm.Extension;
            string folder = Path.GetDirectoryName(path) ?? string.Empty;
            string name = Path.GetFileName(path);

            HashAlgorithmInfo current = HashAlgorithms.ByExtension(name);
            if (current != null && current.Id == algorithm.Id)
            {
                string trimmed = name.Substring(0, name.Length - current.Extension.Length);
                if (LastAlgorithmSuffixStart(trimmed) < 0) return path;
            }

            string stem = name;
            while (true)
            {
                int cut = LastAlgorithmSuffixStart(stem);
                if (cut <= 0) break;
                stem = stem.Substring(0, cut);
            }

            if (!string.Equals(stem, name, StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(folder, stem + wanted);
            }

            return Path.ChangeExtension(path, wanted);
        }

        /// <summary>在名字里从右往左找最后一个算法后缀的起点；没找到返回 -1。</summary>
        private static int LastAlgorithmSuffixStart(string name)
        {
            int best = -1;
            foreach (HashAlgorithmInfo info in HashAlgorithms.All)
            {
                foreach (string extension in info.Extensions)
                {
                    if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) continue;
                    int start = name.Length - extension.Length;
                    if (start > best) best = start;
                }
            }
            return best;
        }

        private void SaveTo(string suggestedPath, bool forceDialog)
        {
            SaveTo(suggestedPath, forceDialog, AlgorithmOf(FirstComputedItem()));
        }

        private void SaveTo(string suggestedPath, bool forceDialog, HashAlgorithmInfo algorithm)
        {
            var lines = BuildChecksumLines(_allItems.ToList());
            if (lines.Count == 0)
            {
                ShowInfo(Localization.Get("MsgNoticeTitle"), Localization.Get("MsgNothingToSave"));
                return;
            }

            if (forceDialog || string.IsNullOrEmpty(suggestedPath))
            {
                SaveAsDialogAndWrite(lines, suggestedPath ?? "checksums.sha256", algorithm);
                return;
            }

            string target = NormalizeChecksumPath(suggestedPath, algorithm);

            if (File.Exists(target))
            {
                MessageBoxResult answer = MessageBox.Show(
                    this,
                    Localization.Format("MsgOverwrite", target),
                    Localization.Get("MsgNoticeTitle"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (answer != MessageBoxResult.Yes) return;
            }

            WriteChecksumFile(target, lines);
        }

        #endregion

        #region 记录写出
        /// <summary>只输出有当前值的行，且只写基名，校验文件跟着文件夹搬走时仍然有效。</summary>
        private List<string> BuildChecksumLines(IEnumerable<CheckItem> items)
        {
            var lines = new List<string>();

            foreach (CheckItem item in items.OrderBy(x => x.Sequence))
            {
                if (!item.HasCurrentHash) continue;

                string name;
                try
                {
                    name = Path.GetFileName(item.FullPath);
                    if (string.IsNullOrEmpty(name)) name = item.FullPath;
                }
                catch
                {
                    name = item.FullPath;
                }

                lines.Add(ChecksumFile.FormatLine(item.CurrentHash, name, Settings.UpperCase));
            }

            return lines;
        }

        /// <summary>默认放在第一个可用文件所在目录，名字取「文件夹名 + 算法后缀」。</summary>
        private string SuggestChecksumPath()
        {
            if (_allItems.Count == 0) return null;

            CheckItem first = _allItems.FirstOrDefault(item => !item.IsUnavailable);
            if (first == null) return null;

            string extension = AlgorithmOf(first).Extension;
            string folder = first.Folder;
            if (string.IsNullOrEmpty(folder))
            {
                return Path.ChangeExtension(first.FullPath, extension);
            }

            string folderName = Path.GetFileName(folder.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(folderName)) folderName = "checksums";

            return Path.Combine(folder, folderName + extension);
        }

        private void WriteChecksumFile(string path, List<string> lines)
        {
            try
            {
                ChecksumFile.Write(path, lines, Settings.WriteBom);
                _lastChecksumPath = path;
                StatusRight.Text = Localization.Format("StListFile", path);
                ShowInfo(Localization.Get("MsgNoticeTitle"),
                    Localization.Format("MsgSaved", lines.Count, path));
            }
            catch (Exception ex)
            {
                ShowError(Localization.Get("MsgErrorTitle"),
                    Localization.Format("MsgSaveFailed", ex.Message));
            }
        }

        #endregion

        #region 复制与定位
        /// <summary>没有算出值时退化为复制路径：复制是列表里最常用的动作，静默失败体验很差。</summary>
        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            var selected = SelectedItems();
            if (selected.Count == 0)
            {
                ShowInfo(Localization.Get("MsgNoticeTitle"), Localization.Get("MsgNoSelection"));
                return;
            }

            var values = selected.Where(item => item.HasCurrentHash)
                                 .Select(item => HashValues.Format(item.CurrentHash, Settings.UpperCase))
                                 .ToList();

            if (values.Count == 0)
            {
                CopyTextToClipboard(string.Join(Environment.NewLine,
                    selected.Select(item => item.FullPath)));
                return;
            }

            CopyTextToClipboard(string.Join(Environment.NewLine, values));
        }

        private void CopyPath_Click(object sender, RoutedEventArgs e)
        {
            var selected = SelectedItems();
            if (selected.Count == 0)
            {
                ShowInfo(Localization.Get("MsgNoticeTitle"), Localization.Get("MsgNoSelection"));
                return;
            }

            CopyTextToClipboard(string.Join(Environment.NewLine,
                selected.Select(item => item.FullPath)));
        }

        private void CopyTextToClipboard(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                ShowInfo(Localization.Get("MsgNoticeTitle"), Localization.Get("MsgCopyEmpty"));
                return;
            }

            try
            {
                Clipboard.SetText(text);
                StatusRight.Text = Localization.Get("MsgCopied");
            }
            catch (Exception ex)
            {
                ShowError(Localization.Get("MsgErrorTitle"), ex.Message);
            }
        }

        /// <summary>文件用 /select 定位到资源管理器，文件夹直接打开。</summary>
        private void Locate_Click(object sender, RoutedEventArgs e)
        {
            CheckItem item = ItemList.SelectedItem as CheckItem;
            if (item == null)
            {
                ShowInfo(Localization.Get("MsgNoticeTitle"), Localization.Get("MsgNoSelection"));
                return;
            }

            try
            {
                if (Directory.Exists(item.FullPath))
                {
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + item.FullPath + "\"");
                }
                else if (File.Exists(item.FullPath))
                {
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + item.FullPath + "\"");
                }
                else
                {
                    ShowInfo(Localization.Get("MsgWarningTitle"),
                        Localization.Format("MsgFileNotFound", item.FullPath));
                }
            }
            catch (Exception ex)
            {
                ShowError(Localization.Get("MsgErrorTitle"), ex.Message);
            }
        }

        private void Properties_Click(object sender, RoutedEventArgs e)
        {
            CheckItem item = ItemList.SelectedItem as CheckItem;
            if (item == null)
            {
                ShowInfo(Localization.Get("MsgNoticeTitle"), Localization.Get("MsgNoSelection"));
                return;
            }

            string none = Localization.Get("MsgNoValue");
            string current = item.HasCurrentHash
                ? HashValues.Format(item.CurrentHash, Settings.UpperCase)
                : none;
            string saved = item.HasExpectedHash
                ? HashValues.Format(item.ExpectedHash, Settings.UpperCase)
                : none;

            string body = Localization.Format(
                "MsgPropertiesBody",
                item.FullPath,
                item.Size.ToString("N0"),
                HashValues.FormatSize(item.Size),
                item.ModifiedText,
                item.AlgorithmText,
                current,
                saved);

            ShowInfo(item.Name, body);
        }

        #endregion

        #region 比较
        /// <summary>双击有值的行打开比较窗口，没值的行则定位到资源管理器。</summary>
        private void ItemList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            CheckItem item = ItemList.SelectedItem as CheckItem;
            if (item == null) return;

            if (item.HasCurrentHash) ShowCompareDialog(item);
            else Locate_Click(sender, e);
        }

        private void ItemList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CheckItem item = ItemList.SelectedItem as CheckItem;
                if (item != null && item.HasCurrentHash)
                {
                    ShowCompareDialog(item);
                    e.Handled = true;
                }
            }
        }

        private void Compare_Click(object sender, RoutedEventArgs e)
        {
            CheckItem item = ItemList.SelectedItem as CheckItem;
            if (item == null)
            {
                ShowInfo(Localization.Get("MsgNoticeTitle"), Localization.Get("MsgNoSelection"));
                return;
            }

            ShowCompareDialog(item);
        }

        private void ShowCompareDialog(CheckItem item)
        {
            var dialog = new CompareWindow(item) { Owner = this };
            dialog.ShowDialog();

            if (dialog.AcceptedHash == null) return;

            item.ExpectedHash = dialog.AcceptedHash;
            item.ExpectedAlgorithm = dialog.AcceptedAlgorithm;
            item.SourceFile = null;
            item.RecomputeStatus();
            UpdateCounters();
            if (_currentFilter != "All") _view.Refresh();
        }

        #endregion

        #region 工具
        private void Calculator_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new CalculatorWindow { Owner = this };
            dialog.ShowDialog();
        }

        /// <summary>选项保存后大小写设置可能已变，要刷新所有行已绑定的显示文本。</summary>
        private void Options_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OptionsWindow { Owner = this };
            if (dialog.ShowDialog() != true) return;

            foreach (CheckItem item in _allItems)
            {
                item.RaiseDisplayText();
            }

            UpdateCommandStates();
        }

        /// <summary>
        /// 自验证：对本程序自身求值。必须异步——大 exe 配 SHA3 要好几秒，同步做会把 UI 线程占满。
        /// 期间切等待光标并禁用入口按钮，计算完成时窗口可能已经关掉，所以弹窗前要判 IsLoaded。
        /// </summary>
        private async void SelfTest_Click(object sender, RoutedEventArgs e)
        {
            string exePath;

            try
            {
                exePath = System.Reflection.Assembly.GetEntryAssembly().Location;
            }
            catch
            {
                exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
            }

            HashAlgorithmInfo algorithm = SelectedAlgorithm();
            SetBusy(true, "BtnSelfTest");

            try
            {
                string hash = await Task.Run(() => ComputeFileHash(exePath, algorithm));

                string body = Localization.Format(
                    "MsgSelfTestBody",
                    algorithm.DisplayName,
                    exePath,
                    HashValues.Format(hash, Settings.UpperCase));

                if (!IsLoaded) return;

                MessageBox.Show(this, body, Localization.Get("MsgSelfTestTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                if (IsLoaded) ShowError(Localization.Get("MsgErrorTitle"), ex.Message);
            }
            finally
            {
                SetBusy(false, "BtnSelfTest");
            }
        }

        /// <summary>同步读文件算摘要，只在后台线程调用。</summary>
        private static string ComputeFileHash(string path, HashAlgorithmInfo algorithm)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite, 4096, FileOptions.SequentialScan))
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
        }

        /// <summary>进入或退出忙碌状态；恢复用 Arrow 而不是 AppStarting，后者语义是后台启动。</summary>
        private void SetBusy(bool busy, string buttonName)
        {
            Cursor = busy ? Cursors.Wait : Cursors.Arrow;

            FrameworkElement button = FindName(buttonName) as FrameworkElement;
            if (button != null) button.IsEnabled = !busy;
        }

        private void About_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new AboutWindow { Owner = this };
            dialog.ShowDialog();
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        #endregion

        #region 拖放
        private void Window_DragEnter(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;

            AddPaths(files, Settings.AutoStartAfterAdd);
            e.Handled = true;
        }

        #endregion

        #region 提示
        private void ShowInfo(string title, string message)
        {
            MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ShowError(string title, string message)
        {
            MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        #endregion

        #region 窗口位置
        /// <summary>还原前先做可见性校正：多显示器切换后旧坐标可能落在屏幕外。</summary>
        private void RestoreWindowPlacement()
        {
            if (!Settings.RememberWindow) return;

            try
            {
                double left = Settings.WindowLeft;
                double top = Settings.WindowTop;

                if (!double.IsNaN(left) && !double.IsNaN(top))
                {
                    double virtualLeft = SystemParameters.VirtualScreenLeft;
                    double virtualTop = SystemParameters.VirtualScreenTop;
                    double virtualWidth = SystemParameters.VirtualScreenWidth;
                    double virtualHeight = SystemParameters.VirtualScreenHeight;

                    bool visible = left >= virtualLeft && top >= virtualTop
                        && left + 200 <= virtualLeft + virtualWidth
                        && top + 100 <= virtualTop + virtualHeight;

                    if (visible)
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual;
                        Left = left;
                        Top = top;
                    }
                }

                if (Settings.WindowWidth >= MinWidth) Width = Settings.WindowWidth;
                if (Settings.WindowHeight >= MinHeight) Height = Settings.WindowHeight;

                if (Settings.WindowMaximized) WindowState = WindowState.Maximized;
            }
            catch
            {
                /* 位置还原失败就保持默认居中，不影响启动 */
            }
        }

        private void SaveWindowPlacement()
        {
            if (!Settings.RememberWindow) return;

            try
            {
                Settings.WindowMaximized = WindowState == WindowState.Maximized;

                if (WindowState == WindowState.Normal)
                {
                    Settings.WindowLeft = Left;
                    Settings.WindowTop = Top;
                    Settings.WindowWidth = Width;
                    Settings.WindowHeight = Height;
                }
            }
            catch
            {
                /* 窗口位置读取失败就保持默认值，不影响退出流程 */
            }
        }

        #endregion
    }
}
