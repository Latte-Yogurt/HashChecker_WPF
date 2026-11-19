using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace HashChecker.Services
{
    /// <summary>
    /// 按天分文件的文本日志：Info 进常规日志，Warning 及以上进问题日志，
    /// 用户反馈时只需提供后者。
    ///
    /// 为什么值得单独做一层而不是随手 File.AppendAllText：
    /// ① 多线程同时写同一文件会把行交错，反馈出去的日志就没法看了 → 一把锁串行化；
    /// ② 进程被强杀时缓冲区里的内容会丢 → 每次写入重开句柄、写完即关；
    /// ③ 问题日志无限增长会撑爆用户磁盘 → 按文件名里的日期保留 7 天。
    /// </summary>
    public class LogService
    {
        #region 日志文件路径

        /// <summary>问题日志的文件名前缀，清理时按同一前缀匹配，避免误删其他日志。</summary>
        private static readonly string _appName = "HashChecker";

        /// <summary>多个线程可能同时写同一个文件，路径计算与写入都放在这把锁里串行化。</summary>
        private static readonly object _writeLock = new object();

        /// <summary>不写 BOM，外部脚本按行合并日志时不会在文件中间混入 BOM 字节。</summary>
        private static readonly Encoding _utf8NoBom = new UTF8Encoding(false);

        /// <summary>问题日志保留一周，覆盖用户反馈周期，又不至于无限增长。</summary>
        private const int IssueLogRetentionDays = 7;

        /// <summary>
        /// 路径按当天日期生成后缓存；进程跨天运行仍写同一文件，
        /// 避免一次会话的日志被拆成两份。
        /// </summary>
        private static string _normalLogFile;

        private static string _issueLogFile;

        /// <summary>默认只记 Warning 及以上，需要更细的排查信息时由调用方显式调低阈值。</summary>
        public static ErrorLevel MinLogLevel { get; set; } = ErrorLevel.Warning;

        private static string LogDirectory
        {
            get { return AppPaths.LogDirectory; }
        }

        /// <summary>日志目录切换后必须清空缓存，否则仍会写回旧路径。</summary>
        public static void ResetLogFiles()
        {
            lock (_writeLock)
            {
                _normalLogFile = null;
                _issueLogFile = null;
            }
        }

        /// <summary>常规日志（Info）的完整路径。</summary>
        public static string LogFilePath
        {
            get
            {
                if (_normalLogFile == null)
                {
                    lock (_writeLock)
                    {
                        if (_normalLogFile == null)
                        {
                            _normalLogFile = BuildLogFilePath(_appName, DateTime.Now);
                        }
                    }
                }

                return _normalLogFile;
            }
        }

        /// <summary>问题日志（Warning / Error）的完整路径。</summary>
        public static string IssueLogFilePath
        {
            get
            {
                if (_issueLogFile == null)
                {
                    lock (_writeLock)
                    {
                        if (_issueLogFile == null)
                        {
                            _issueLogFile = BuildLogFilePath(_appName + "_Issue", DateTime.Now);
                        }
                    }
                }

                return _issueLogFile;
            }
        }

        #endregion

        #region 日志级别与写入

        /// <summary>
        /// 数值大小用于级别过滤，新增级别必须插在合适的位置，否则过滤结果会错。
        /// </summary>
        public enum ErrorLevel
        {
            Info = 0,
            Warning = 1,
            Error = 2
        }

        /// <summary>Info 之外的级别都进问题日志，用户反馈时只需要提供 Issue 文件。</summary>
        public void WriteLog(string message, ErrorLevel level)
        {
            string path = level == ErrorLevel.Info ? LogFilePath : IssueLogFilePath;
            WriteLog(message, level, path);
        }

        private void WriteLog(string message, ErrorLevel level, string logPath)
        {
            // 低于阈值的消息直接丢弃，避免高频 Info 淹没真正需要排查的问题。
            if (string.IsNullOrWhiteSpace(message) || level < MinLogLevel) return;

            try
            {
                lock (_writeLock)
                {
                    // 目录可能被用户或清理工具删掉，每次写入前确保存在。
                    Directory.CreateDirectory(LogDirectory);

                    // 每次写入都重新打开再关闭，进程被强杀时已写内容仍会留在磁盘上。
                    using (var writer = new StreamWriter(logPath, true, _utf8NoBom))
                    {
                        string nowTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                        writer.WriteLine("{0} [{1}]：{2}",
                            nowTime, level.ToString().ToUpperInvariant(), message);
                    }
                }
            }
            catch
            {
                // 日志失败不应影响主流程，这里不弹窗，避免错误风暴。
            }
        }

        /// <summary>便捷重载：默认按 Error 记。</summary>
        public void WriteLog(string message)
        {
            WriteLog(message, ErrorLevel.Error);
        }

        #endregion

        #region 日志清理与命名

        /// <summary>
        /// 只清理问题日志，按文件名里的日期判断；常规日志留给用户自行处置，
        /// 日期解析不出的文件一律不动（可能是别的工具放进来的）。
        /// </summary>
        public static void CleanupOldLogs()
        {
            try
            {
                string directory = LogDirectory;
                if (!Directory.Exists(directory)) return;

                DateTime threshold = DateTime.Now.Date.AddDays(-IssueLogRetentionDays);
                string pattern = _appName + "_Issue_*.log";

                foreach (string file in Directory.GetFiles(directory, pattern))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    int separator = name.LastIndexOf('_');
                    if (separator < 0 || separator >= name.Length - 1) continue;

                    string dateText = name.Substring(separator + 1);
                    DateTime logDate;
                    if (!DateTime.TryParseExact(dateText, "yyyy-MM-dd",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out logDate))
                    {
                        continue;
                    }

                    if (logDate.Date >= threshold) continue;

                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                        // 文件被占用时保留，等下次启动再清理。
                    }
                }
            }
            catch
            {
                // 清理失败不影响启动。
            }
        }

        private static string BuildLogFilePath(string prefix, DateTime date)
        {
            return Path.Combine(LogDirectory,
                string.Format(CultureInfo.InvariantCulture, "{0}_{1:yyyy-MM-dd}.log", prefix, date));
        }

        #endregion
    }
}
