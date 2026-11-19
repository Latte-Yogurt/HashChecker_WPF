using System;
using System.IO;
using System.Reflection;

namespace HashChecker.Services
{
    /// <summary>
    /// 数据目录的唯一出处。配置、日志这些「程序自己产生的文件」都挂在这棵树下，
    /// 调用方不再各自拼 %LOCALAPPDATA%。
    ///
    /// 为什么单独抽一层：目录名与根路径一旦散落在多个类里，改一处就会漏一处，
    /// 出现「配置写在新目录、日志还写在旧目录」这种半迁移状态。
    /// </summary>
    public static class AppPaths
    {
        #region 目录与文件常量

        /// <summary>数据目录名，也是配置目录名的兜底值。</summary>
        public const string DataFolderName = "HashChecker";

        /// <summary>设置文件名。</summary>
        public const string ConfigFileName = "settings.json";

        /// <summary>日志子目录名。</summary>
        public const string LogFolderName = "Logs";

        /// <summary>
        /// 数据根目录的改道变量。设成**绝对路径**时，数据目录整个挂到它下面。
        ///
        /// 这条改道是给测试工具用的：测试驱动的是真实程序，程序会往数据目录写配置和日志。
        /// 默认落在 %LOCALAPPDATA%\HashChecker 时，跑一轮测试就把用户自己的配置改了、
        /// 日志刷了 —— 用户下次打开程序看到的是测试留下的状态。
        ///
        /// 注意：不能靠改 LOCALAPPDATA 环境变量达到同样效果 ——
        /// <c>Environment.GetFolderPath(SpecialFolder.LocalApplicationData)</c> 走的是
        /// SHGetFolderPath，读的是用户配置文件里的已知文件夹，与环境变量无关。
        /// 所以只能由程序自己提供一个明确的改道点。
        /// </summary>
        public const string DataRootVariable = "HASHCHECKER_DATA_ROOT";

        /// <summary>
        /// 只改道**配置目录**的旧变量，保留以兼容既有测试探针。
        /// 新代码请用 <see cref="DataRootVariable"/>；两者都设时本变量优先（更具体）。
        /// </summary>
        public const string SettingsDirVariable = "HASHCHECKER_SETTINGS_DIR";

        private static readonly object _syncRoot = new object();

        private static string _dataDirectory;

        #endregion

        #region 目录构成

        /// <summary>配置目录。日志目录是它的子目录，两者同源。</summary>
        public static string SettingsFolder
        {
            get
            {
                // 旧变量更具体（只改配置），优先于根改道。
                string settingsOnly = ReadOverride(SettingsDirVariable);
                if (settingsOnly != null) return settingsOnly;

                return DataDirectory;
            }
        }

        /// <summary>当前配置文件路径（settings.json）。</summary>
        public static string SettingsPath
        {
            get { return Path.Combine(SettingsFolder, ConfigFileName); }
        }

        /// <summary>日志目录，挂在数据目录下的 Logs\。</summary>
        public static string LogDirectory
        {
            get
            {
                // 只改道配置目录时，日志也跟着走，否则会写进用户真实目录。
                string settingsOnly = ReadOverride(SettingsDirVariable);
                if (settingsOnly != null) return Path.Combine(settingsOnly, LogFolderName);

                return Path.Combine(DataDirectory, LogFolderName);
            }
        }

        /// <summary>数据目录：根目录 + 固定的数据目录名。</summary>
        public static string DataDirectory
        {
            get
            {
                lock (_syncRoot)
                {
                    if (_dataDirectory == null)
                    {
                        _dataDirectory = ComputeDataDirectory();
                    }
                    return _dataDirectory;
                }
            }
        }

        private static string ComputeDataDirectory()
        {
            string root = ReadOverride(DataRootVariable);
            if (root != null) return root;

            string localAppData;
            try
            {
                localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }
            catch
            {
                localAppData = string.Empty;
            }

            if (string.IsNullOrEmpty(localAppData))
            {
                // 读不到已知文件夹时退回程序目录旁，至少仍是可写且不会串到别人机器上的位置。
                return Path.Combine(GetProgramDirectory(), DataFolderName);
            }

            return Path.Combine(localAppData, FolderName);
        }

        /// <summary>目录名取程序集名，便携改名后配置目录跟着走，不会串到别人的配置里。</summary>
        private static string FolderName
        {
            get
            {
                try
                {
                    Assembly entry = Assembly.GetEntryAssembly();
                    string name = entry == null ? null : entry.GetName().Name;
                    return string.IsNullOrEmpty(name) ? DataFolderName : name;
                }
                catch
                {
                    return DataFolderName;
                }
            }
        }

        /// <summary>
        /// 读改道变量。只认绝对路径：相对路径的含义取决于当前工作目录，
        /// 而工作目录可能在程序运行期间被改掉，那会让同一台机器上的两次运行
        /// 写进两个不同的地方 —— 这种不确定性比「忽略改道」更危险。
        /// </summary>
        private static string ReadOverride(string variable)
        {
            string value;
            try
            {
                value = Environment.GetEnvironmentVariable(variable);
            }
            catch
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(value)) return null;

            try
            {
                string full = Path.GetFullPath(value.Trim());
                return Path.IsPathRooted(full) ? full : null;
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region 程序目录与目录创建

        /// <summary>程序所在目录。</summary>
        public static string GetProgramDirectory()
        {
            // 单文件发布或影子复制时 GetEntryAssembly().Location 可能是空的，
            // 这时退回 AppDomain 的基目录，否则路径会算成当前工作目录。
            try
            {
                Assembly entry = Assembly.GetEntryAssembly();
                string exePath = entry == null ? null : entry.Location;
                if (!string.IsNullOrEmpty(exePath))
                {
                    string directory = Path.GetDirectoryName(exePath);
                    if (!string.IsNullOrEmpty(directory)) return directory;
                }
            }
            catch
            {
                // 落回下面的基目录
            }

            try
            {
                return AppDomain.CurrentDomain.BaseDirectory;
            }
            catch
            {
                return Directory.GetCurrentDirectory();
            }
        }

        /// <summary>确保配置目录存在，成功返回 true。</summary>
        public static bool EnsureSettingsFolder()
        {
            try
            {
                Directory.CreateDirectory(SettingsFolder);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>确保日志目录存在，成功返回 true。</summary>
        public static bool EnsureLogDirectory()
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>目录切换后作废缓存（测试与诊断用）。</summary>
        public static void ResetCache()
        {
            lock (_syncRoot)
            {
                _dataDirectory = null;
            }
            LogService.ResetLogFiles();
        }

        #endregion
    }
}
