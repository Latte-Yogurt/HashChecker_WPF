using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Media;
using HashChecker.Services;

namespace HashChecker.Models
{
    /// <summary>
    /// 一行在列表里的状态。枚举顺序就是筛选条从左到右的顺序，改动会直接改变界面顺序。
    /// </summary>
    public enum CheckStatus
    {
        /// <summary>文件不存在或读不出来。</summary>
        Unavailable = 0,

        /// <summary>已加入列表，既没算过也没有期望值。</summary>
        Unknown = 1,

        /// <summary>从校验记录载入了期望值，尚未计算。</summary>
        Loaded = 2,

        /// <summary>新加入且自带期望值，尚未计算。</summary>
        New = 3,

        /// <summary>算出的值与期望值不一致，或读取失败。</summary>
        Failed = 4,

        /// <summary>算出的值与期望值一致；没有期望值但计算成功也算通过。</summary>
        Passed = 5
    }

    /// <summary>
    /// 状态到画刷的映射。集中在一处，避免每个绑定各自造对象；
    /// 画刷构造后立即 Freeze，后台线程读绑定属性时不必再同步。
    /// </summary>
    public static class CheckItemColors
    {
        public static readonly Brush Normal = Freeze(Color.FromRgb(0x1A, 0x1A, 0x1A));
        public static readonly Brush Failed = Freeze(Color.FromRgb(0xC6, 0x28, 0x28));
        public static readonly Brush Passed = Freeze(Color.FromRgb(0x1B, 0x7F, 0x3B));
        public static readonly Brush Unavailable = Freeze(Color.FromRgb(0x8A, 0x6D, 0x00));
        public static readonly Brush Unknown = Freeze(Color.FromRgb(0x4A, 0x90, 0xD9));
        public static readonly Brush Loaded = Freeze(Color.FromRgb(0x3A, 0x6E, 0xA5));

        private static Brush Freeze(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>
    /// 列表行模型：一个文件或文件夹，以及它当前算出值与记录里的期望值。
    /// 计算在后台线程完成，写属性前必须已切回 UI 线程——绑定因此不需要额外同步。
    /// </summary>
    public class CheckItem : INotifyPropertyChanged
    {
        #region 字段与构造

        private static long _sequenceCounter;

        private readonly long _sequence;
        private readonly string _fullPath;
        private string _folder;
        private long _size;
        private DateTime _modified;
        private string _expectedHash;
        private string _currentHash;
        private CheckStatus _status;
        private string _error;
        private bool _isDirectory;
        private string _sourceFile;
        private HashAlgorithmInfo _expectedAlgorithm;
        private HashAlgorithmInfo _selectedAlgorithm;
        private HashAlgorithmInfo _currentAlgorithm;
        private SortedDictionary<string, string> _digests;

        public CheckItem(string fullPath)
        {
            _fullPath = fullPath;
            _sequence = Interlocked.Increment(ref _sequenceCounter);
            Refresh();
        }

        public string FullPath
        {
            get { return _fullPath; }
        }

        #endregion

        #region 磁盘信息

        public string Name
        {
            get
            {
                if (string.IsNullOrEmpty(_fullPath)) return string.Empty;

                string name = Path.GetFileName(_fullPath);

                if (string.IsNullOrEmpty(name) && _isDirectory) name = _fullPath;

                return _isDirectory ? name + Path.DirectorySeparatorChar : name;
            }
        }

        public string Folder
        {
            get { return _folder; }
        }

        public long Size
        {
            get { return _size; }
        }

        public string SizeText
        {
            get { return _size.ToString("N0", CultureInfo.CurrentCulture); }
        }

        public DateTime Modified
        {
            get { return _modified; }
        }

        public string ModifiedText
        {
            get { return _modified.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture); }
        }

        public bool IsDirectory
        {
            get { return _isDirectory; }
        }

        #endregion

        #region 哈希值

        public string CurrentHash
        {
            get { return _currentHash; }
            set
            {
                if (_currentHash == value) return;
                _currentHash = value;
                Raise("CurrentHash");
                Raise("HasCurrentHash");
                Raise("CurrentHashText");
                Raise("CurrentHashTooltip");
            }
        }

        public string ExpectedHash
        {
            get { return _expectedHash; }
            set
            {
                if (_expectedHash == value) return;
                _expectedHash = value;
                Raise("ExpectedHash");
                Raise("HasExpectedHash");
                Raise("ExpectedHashText");
                Raise("ExpectedHashTooltip");
                Raise("AlgorithmText");
            }
        }

        /// <summary>
        /// 期望值对应的算法：记录后缀写明时就是那一个；只按长度推且同长度有多个候选时为 null，
        /// 此时按候选集合比对。
        /// </summary>
        public HashAlgorithmInfo ExpectedAlgorithm
        {
            get { return _expectedAlgorithm; }
            set
            {
                if (_expectedAlgorithm == value) return;
                _expectedAlgorithm = value;
                Raise("ExpectedAlgorithm");
                Raise("AlgorithmText");
                Raise("AlgorithmTooltip");
                Raise("CurrentHashText");
                Raise("ExpectedHashText");
            }
        }

        /// <summary>
        /// 这一行要算的算法。优先级：用户显式选择的 &gt; 期望值对应的 &gt; 默认算法。
        ///
        /// 用户选择排在期望值之前是刻意的：文件旁边若有 .sha256 记录，按记录走就再也换不了算法，
        /// 下拉框形同虚设。用户一旦显式选择，期望值仍在，但不再约束算法。
        /// </summary>
        public IList<HashAlgorithmInfo> RequiredAlgorithms
        {
            get
            {
                if (_selectedAlgorithm != null) return Single(_selectedAlgorithm);

                if (!HasExpectedHash) return Single(SelectedAlgorithm);

                if (_expectedAlgorithm != null) return Single(_expectedAlgorithm);

                List<HashAlgorithmInfo> candidates = HashAlgorithms.CandidatesFor(_expectedHash);
                if (candidates.Count > 0) return candidates;

                return Single(SelectedAlgorithm);
            }
        }

        #endregion

        #region 算法优先级

        /// <summary>用户是否显式选过算法；选过之后记录后缀不再约束这一行。</summary>
        public bool HasExplicitAlgorithm
        {
            get { return _selectedAlgorithm != null; }
        }

        /// <summary>没有期望值时用的算法，即下拉框当前那一项。</summary>
        public HashAlgorithmInfo SelectedAlgorithm
        {
            get { return _selectedAlgorithm ?? HashAlgorithms.Default; }
            set
            {
                if (_selectedAlgorithm == value) return;
                _selectedAlgorithm = value;
                Raise("SelectedAlgorithm");
                Raise("HasExplicitAlgorithm");
                Raise("AlgorithmText");
                Raise("AlgorithmTooltip");
            }
        }

        /// <summary>
        /// 主算法：显式选择最优先，其次期望值对应的算法。
        /// 保存记录时按它决定后缀，这正是「按算法命名而不是按算法族命名」的落点。
        /// </summary>
        public HashAlgorithmInfo PrimaryAlgorithm
        {
            get { return _selectedAlgorithm ?? _expectedAlgorithm ?? HashAlgorithms.Default; }
        }

        /// <summary>本次真正算出当前值的算法，没算过时为 null。保存后缀要按它来。</summary>
        public HashAlgorithmInfo ComputedAlgorithm
        {
            get { return _currentAlgorithm; }
        }

        #endregion

        #region 哈希文本

        private static IList<HashAlgorithmInfo> Single(HashAlgorithmInfo info)
        {
            return new List<HashAlgorithmInfo> { info ?? HashAlgorithms.Default };
        }

        /// <summary>期望值的来源文件，可为空。</summary>
        public string SourceFile
        {
            get { return _sourceFile; }
            set
            {
                if (_sourceFile == value) return;
                _sourceFile = value;
                Raise("SourceFile");
                Raise("ExpectedHashTooltip");
            }
        }

        public bool HasCurrentHash { get { return !string.IsNullOrEmpty(_currentHash); } }

        public bool HasExpectedHash { get { return !string.IsNullOrEmpty(_expectedHash); } }

        public string CurrentHashText
        {
            get { return HashValues.Format(_currentHash, AppSettingsHost.UpperCase); }
        }

        public string ExpectedHashText
        {
            get { return HashValues.Format(_expectedHash, AppSettingsHost.UpperCase); }
        }

        public string CurrentHashTooltip
        {
            get
            {
                if (!string.IsNullOrEmpty(_error) && string.IsNullOrEmpty(_currentHash)) return _error;
                return _currentHash ?? string.Empty;
            }
        }

        public string ExpectedHashTooltip
        {
            get
            {
                if (string.IsNullOrEmpty(_expectedHash)) return string.Empty;

                string head = string.IsNullOrEmpty(_sourceFile)
                    ? _expectedHash
                    : _expectedHash + "\n" + _sourceFile;

                string algorithm = AlgorithmText;
                if (!string.IsNullOrEmpty(algorithm)) head = head + "\n" + algorithm;
                return head;
            }
        }

        #endregion

        #region 算法显示

        /// <summary>
        /// 算法列的显示文本。算过就显示实际用过的算法；只按长度推断且候选不止一个时
        /// 如实列出全部候选，不假装知道是 SHA-256 还是 SHA3-256。
        /// </summary>
        public string AlgorithmText
        {
            get
            {
                if (_currentAlgorithm != null) return _currentAlgorithm.DisplayName;

                if (_selectedAlgorithm != null) return _selectedAlgorithm.DisplayName;

                if (_expectedAlgorithm != null) return _expectedAlgorithm.DisplayName;

                if (HasExpectedHash)
                {
                    List<HashAlgorithmInfo> candidates = HashAlgorithms.CandidatesFor(_expectedHash);
                    if (candidates.Count > 1)
                    {
                        var names = new List<string>(candidates.Count);
                        foreach (HashAlgorithmInfo info in candidates) names.Add(info.DisplayName);
                        return string.Join(" / ", names);
                    }
                    if (candidates.Count == 1) return candidates[0].DisplayName;
                }

                return SelectedAlgorithm.DisplayName;
            }
        }

        /// <summary>算法列的悬停说明：讲清楚这个结论是记录给的、推断的还是下拉框选的。</summary>
        public string AlgorithmTooltip
        {
            get
            {
                if (_expectedAlgorithm != null)
                {
                    return Localization.Format("TipAlgorithmFromRecord", _expectedAlgorithm.DisplayName);
                }

                if (HasExpectedHash)
                {
                    List<HashAlgorithmInfo> candidates = HashAlgorithms.CandidatesFor(_expectedHash);
                    if (candidates.Count > 1)
                    {
                        return Localization.Format("TipAlgorithmAmbiguous", candidates.Count);
                    }
                }

                return Localization.Format("TipAlgorithmSelected", SelectedAlgorithm.DisplayName);
            }
        }

        #endregion

        #region 状态与外观

        public CheckStatus Status
        {
            get { return _status; }
            set
            {
                if (_status == value) return;
                _status = value;
                Raise("Status");
                Raise("StatusText");
                Raise("IsPassed");
                Raise("IsFailed");
                Raise("IsUnknown");
                Raise("IsUnavailable");
                Raise("IsLoaded");
                Raise("IsNew");
                Raise("CurrentHashBrush");
                Raise("ExpectedHashBrush");
                Raise("IconGlyph");
                Raise("IconBrush");
                Raise("IconText");
            }
        }

        /// <summary>这一行最近一次读取或计算失败的原因。</summary>
        public string Error
        {
            get { return _error; }
            set
            {
                if (_error == value) return;
                _error = value;
                Raise("Error");
                Raise("CurrentHashTooltip");
            }
        }

        public bool IsPassed { get { return _status == CheckStatus.Passed; } }

        public bool IsFailed { get { return _status == CheckStatus.Failed; } }

        public bool IsUnknown { get { return _status == CheckStatus.Unknown; } }

        public bool IsUnavailable { get { return _status == CheckStatus.Unavailable; } }

        public bool IsLoaded { get { return _status == CheckStatus.Loaded; } }

        public bool IsNew { get { return _status == CheckStatus.New; } }

        public string StatusText
        {
            get
            {
                switch (_status)
                {
                    case CheckStatus.Unavailable: return Localization.Get("StatusUnavailable");
                    case CheckStatus.Unknown: return Localization.Get("StatusUnknown");
                    case CheckStatus.Loaded: return Localization.Get("StatusLoaded");
                    case CheckStatus.New: return Localization.Get("StatusNew");
                    case CheckStatus.Failed: return Localization.Get("StatusFailed");
                    case CheckStatus.Passed: return Localization.Get("StatusPassed");
                    default: return Localization.Get("StatusUnknown");
                }
            }
        }

        /// <summary>当前值文字颜色：失败标红、通过标绿，其余按状态取色。</summary>
        public Brush CurrentHashBrush
        {
            get
            {
                switch (_status)
                {
                    case CheckStatus.Failed: return CheckItemColors.Failed;
                    case CheckStatus.Passed: return CheckItemColors.Passed;
                    case CheckStatus.Unavailable: return CheckItemColors.Unavailable;
                    case CheckStatus.Unknown: return CheckItemColors.Unknown;
                    default: return CheckItemColors.Normal;
                }
            }
        }

        /// <summary>保存值文字颜色：只有判定成通过或失败时才着色。</summary>
        public Brush ExpectedHashBrush
        {
            get
            {
                switch (_status)
                {
                    case CheckStatus.Failed: return CheckItemColors.Failed;
                    case CheckStatus.Passed: return CheckItemColors.Passed;
                    default: return CheckItemColors.Normal;
                }
            }
        }

        /// <summary>名称列前的状态图标（Segoe MDL2 Assets 字形）。</summary>
        public string IconGlyph
        {
            get
            {
                switch (_status)
                {
                    case CheckStatus.Unavailable: return "\uE7BA";
                    case CheckStatus.Failed: return "\uE783";
                    case CheckStatus.Passed: return "\uE930";
                    case CheckStatus.Loaded: return "\uE896";
                    case CheckStatus.New: return "\uE946";
                    default: return "\uE9CE";
                }
            }
        }

        public string IconText
        {
            get { return StatusText; }
        }

        public Brush IconBrush
        {
            get
            {
                switch (_status)
                {
                    case CheckStatus.Unavailable: return CheckItemColors.Unavailable;
                    case CheckStatus.Failed: return CheckItemColors.Failed;
                    case CheckStatus.Passed: return CheckItemColors.Passed;
                    case CheckStatus.Loaded: return CheckItemColors.Loaded;
                    default: return CheckItemColors.Unknown;
                }
            }
        }

        /// <summary>加入列表的顺序，保存时按它输出，保证与界面一致。</summary>
        public int Sequence
        {
            get { return (int)(_sequence % int.MaxValue); }
        }

        #endregion

        #region 状态推导

        /// <summary>
        /// 按当前值与期望值推导状态。有期望值就比对；没有期望值但算出了值记为通过，
        /// 此时保存值列仍为空，一眼能看出这一行没比对过。
        /// </summary>
        public void RecomputeStatus()
        {
            if (_status == CheckStatus.Unavailable) return;

            if (string.IsNullOrEmpty(_currentHash))
            {
                Status = string.IsNullOrEmpty(_expectedHash) ? CheckStatus.Unknown : CheckStatus.Loaded;
                return;
            }

            if (string.IsNullOrEmpty(_expectedHash))
            {
                Status = CheckStatus.Passed;
                return;
            }

            Status = MatchesExpected() ? CheckStatus.Passed : CheckStatus.Failed;
        }

        /// <summary>
        /// 当前值是否与期望值一致。后缀明确了算法就只认那一个；只按长度推断且有多个候选时，
        /// 任一候选命中即通过——16 位十六进制可能对应好几个 128 位算法，误报失败比漏报更糟。
        /// </summary>
        private bool MatchesExpected()
        {
            string expected = HashAlgorithms.NormalizeDigest(_expectedHash);
            if (expected == null) return false;

            if (_expectedAlgorithm != null)
            {
                return HashAlgorithms.NormalizeDigest(_currentHash) != null
                    && string.Equals(NormalizeFor(_expectedAlgorithm), expected, StringComparison.Ordinal);
            }

            if (string.Equals(HashAlgorithms.NormalizeDigest(_currentHash), expected, StringComparison.Ordinal))
            {
                return true;
            }

            foreach (HashAlgorithmInfo info in HashAlgorithms.CandidatesFor(expected))
            {
                string value = _digests != null && _digests.ContainsKey(info.Id) ? _digests[info.Id] : null;
                if (value == null) continue;
                if (string.Equals(value, expected, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        private string NormalizeFor(HashAlgorithmInfo algorithm)
        {
            if (_digests != null && _digests.ContainsKey(algorithm.Id)) return _digests[algorithm.Id];
            return HashAlgorithms.NormalizeDigest(_currentHash);
        }

        /// <summary>
        /// 外部给的一个值是否与这一行算出的值一致。同长度多候选时逐个比，任一个命中即算一致。
        /// </summary>
        public bool MatchesDigest(string candidate)
        {
            string normalized = HashAlgorithms.NormalizeDigest(candidate);
            if (normalized == null || string.IsNullOrEmpty(_currentHash)) return false;

            if (string.Equals(normalized, HashAlgorithms.NormalizeDigest(_currentHash), StringComparison.Ordinal))
            {
                return true;
            }

            if (_digests == null) return false;

            foreach (KeyValuePair<string, string> pair in _digests)
            {
                if (string.Equals(pair.Value, normalized, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// 计算成功后写入当前值并推导状态。<paramref name="digests"/> 是本次算出的全部算法值，
        /// 供多候选比对使用。
        /// </summary>
        public void ApplyHash(string hash, SortedDictionary<string, string> digests, HashAlgorithmInfo algorithm)
        {
            _digests = digests;
            _currentAlgorithm = algorithm;
            _error = null;
            Raise("Error");

            Raise("AlgorithmText");
            Raise("AlgorithmTooltip");

            CurrentHash = hash;
            RecomputeStatus();
        }

        /// <summary>计算成功后写入当前值（单算法版本）。</summary>
        public void ApplyHash(string hash)
        {
            ApplyHash(hash, null, _expectedAlgorithm ?? SelectedAlgorithm);
        }

        /// <summary>
        /// 清掉算出的值并回到未计算状态，供切换算法后强制重算。
        /// 期望值保留：它来自校验记录，与本次用哪个算法计算无关。
        /// </summary>
        public void ClearComputedHash()
        {
            _digests = null;
            _currentAlgorithm = null;
            _error = null;
            CurrentHash = null;
            Raise("AlgorithmText");
            Raise("AlgorithmTooltip");
            RecomputeStatus();
        }

        /// <summary>计算失败后记录原因并推导状态；文件已消失时落到不可用而不是失败。</summary>
        public void ApplyError(string message)
        {
            _error = message;
            Raise("Error");
            _digests = null;
            CurrentHash = null;

            Status = (!File.Exists(_fullPath) && !Directory.Exists(_fullPath))
                ? CheckStatus.Unavailable
                : CheckStatus.Failed;
        }

        #endregion

        #region 磁盘刷新

        /// <summary>
        /// 从磁盘读大小与修改时间，文件消失时标记为不可用。
        /// 这里绝不向外抛异常：一个坏文件不该打断整个列表的加载。
        /// </summary>
        public void Refresh()
        {
            try
            {
                if (Directory.Exists(_fullPath))
                {
                    _isDirectory = true;
                    _folder = SafeParent(_fullPath, true);
                    _size = DirectorySize(_fullPath);
                    _modified = SafeLastWrite(_fullPath, true);

                    if (string.IsNullOrEmpty(_currentHash) && _status == CheckStatus.Unavailable)
                    {
                        _status = string.IsNullOrEmpty(_expectedHash) ? CheckStatus.Unknown : CheckStatus.Loaded;
                    }

                    RaiseAll();
                    return;
                }

                if (File.Exists(_fullPath))
                {
                    _isDirectory = false;
                    _folder = SafeParent(_fullPath, false);
                    _size = SafeLength(_fullPath);
                    _modified = SafeLastWrite(_fullPath, false);

                    if (string.IsNullOrEmpty(_currentHash) && _status == CheckStatus.Unavailable)
                    {
                        _status = string.IsNullOrEmpty(_expectedHash) ? CheckStatus.Unknown : CheckStatus.Loaded;
                    }

                    RaiseAll();
                    return;
                }

                MarkUnavailable(string.Format(CultureInfo.CurrentCulture,
                    Localization.Get("MsgFileNotFound"), _fullPath));
            }
            catch (Exception ex)
            {
                MarkUnavailable(ex.Message);
            }
        }

        private void MarkUnavailable(string message)
        {
            _isDirectory = false;
            _folder = SafeParent(_fullPath, false);
            _size = 0;
            _modified = DateTime.MinValue;
            _error = message;
            Status = CheckStatus.Unavailable;
            RaiseAll();
        }

        /// <summary>
        /// 取所在文件夹；盘符根（C:\）去掉分隔符后只剩 C:，再取父目录会得到空串，
        /// 所以长度 ≤ 2 时直接返回空。
        /// </summary>
        private static string SafeParent(string path, bool isDirectory)
        {
            try
            {
                string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (trimmed.Length <= 2) return string.Empty;

                return Path.GetDirectoryName(trimmed) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static long SafeLength(string path)
        {
            try { return new FileInfo(path).Length; }
            catch { return 0; }
        }

        private static DateTime SafeLastWrite(string path, bool isDirectory)
        {
            try
            {
                return isDirectory
                    ? new DirectoryInfo(path).LastWriteTime
                    : new FileInfo(path).LastWriteTime;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        /// <summary>
        /// 递归累加文件夹内全部文件的长度，用于「大小」列。
        /// 单个文件与被占用、无权限之类的情形一律跳过：统计不完整好过整个列表报错。
        /// </summary>
        private static long DirectorySize(string path)
        {
            long total = 0;
            try
            {
                foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(file).Length; }
                    catch { /* 单个文件取不到大小就按 0 计，不影响其余文件 */ }
                }
            }
            catch { /* 目录整体不可枚举（无权限、设备目录）时按已累计的部分返回 */ }
            return total;
        }

        #endregion

        #region 属性通知

        private void RaiseAll()
        {
            Raise("Name");
            Raise("Folder");
            Raise("Size");
            Raise("SizeText");
            Raise("Modified");
            Raise("ModifiedText");
            Raise("IsDirectory");
            Raise("Status");
            Raise("StatusText");
            Raise("Error");
            Raise("IsPassed");
            Raise("IsFailed");
            Raise("IsUnknown");
            Raise("IsUnavailable");
            Raise("IsLoaded");
            Raise("IsNew");
            Raise("CurrentHashText");
            Raise("ExpectedHashText");
            Raise("CurrentHashTooltip");
            Raise("ExpectedHashTooltip");
            Raise("CurrentHashBrush");
            Raise("ExpectedHashBrush");
            Raise("IconGlyph");
            Raise("IconBrush");
            Raise("IconText");
        }

        /// <summary>显示文本依赖大小写设置，选项保存后由窗口调这里刷新已绑定的文本。</summary>
        public void RaiseDisplayText()
        {
            Raise("CurrentHashText");
            Raise("ExpectedHashText");
            Raise("CurrentHashTooltip");
            Raise("ExpectedHashTooltip");
            Raise("StatusText");
            Raise("IconText");
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise(string name)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0} [{1}]", _fullPath, _status);
        }

        #endregion
    }
}
