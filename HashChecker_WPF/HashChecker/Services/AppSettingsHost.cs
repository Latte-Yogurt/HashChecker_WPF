
namespace HashChecker.Services
{
    /// <summary>
    /// 进程级设置单例。窗口与行模型都从这里取当前设置，
    /// 避免把 AppSettings 到处传参，也避免每行各存一份副本。
    /// </summary>
    public static class AppSettingsHost
    {
        private static AppSettings _current;

        /// <summary>当前设置，首次访问时从磁盘载入。</summary>
        public static AppSettings Current
        {
            get
            {
                if (_current == null)
                {
                    _current = AppSettings.Load();
                }
                return _current;
            }
        }

        /// <summary>摘要是否以大写显示。绑定会频繁读它，所以做成快捷属性。</summary>
        public static bool UpperCase
        {
            get { return Current.UpperCase; }
        }

        /// <summary>选项窗口保存后调用，替换当前设置。</summary>
        public static void Replace(AppSettings settings)
        {
            if (settings == null) return;
            _current = settings;
            _current.Normalize();
        }

        /// <summary>把当前设置写盘。</summary>
        public static void Save()
        {
            if (_current != null) _current.Save();
        }
    }
}
