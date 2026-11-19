using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace HashChecker.Services
{
    /// <summary>
    /// 应用配置的读写入口。配置是标准 JSON：顶层一个对象，每个设置项对应一个键。
    /// 键序固定、缩进两格，用户可以手工编辑，任何现代 JSON 解析器都能读。
    ///
    /// 为什么把读写从 <see cref="AppSettings"/> 里搬出来：
    /// 那个类既要当「数据模型」（一堆属性），又要当「文件 I/O」（缺键补齐、原子写、
    /// 容错重建）。两件事混在一起时，加一个键就要在四个地方各改一遍
    /// （属性、默认值、读、写），漏一处就静默漂移。现在键表集中在
    /// <see cref="_keys"/> 一处，读、写、缺键补齐、默认值都从它推导。
    ///
    /// <see cref="AppSettings.Apply"/> 与 <see cref="AppSettings.TryLoadJson"/>
    /// 保持原样：测试探针按签名反射调用它们，是既有的公开契约。
    /// </summary>
    public class ConfigService
    {
        #region 键表

        /// <summary>
        /// 键表集中在一处：读取、写回、缺键补齐、默认值全部从这张表推导，
        /// 四处各写一份默认值必然漏改一处而静默漂移。
        /// </summary>
        private static readonly ConfigKey[] _keys =
        {
            new ConfigKey("Threads", false),
            new ConfigKey("UpperCase", true),
            new ConfigKey("AutoStartAfterAdd", true),
            new ConfigKey("AutoLoadCompanion", true),
            new ConfigKey("WriteBom", true),
            new ConfigKey("RememberWindow", true),
            new ConfigKey("AutoFitColumns", true),
            new ConfigKey("Algorithm", false),
            new ConfigKey("Language", false),
            new ConfigKey("WindowLeft", false),
            new ConfigKey("WindowTop", false),
            new ConfigKey("WindowWidth", false),
            new ConfigKey("WindowHeight", false),
            new ConfigKey("WindowMaximized", true),
            // 日志级别阈值：对照参照的 ConfigService，它也是配置键而不是写死在代码里。
            // 默认 Warning 意味着 Info 级消息（启动、退出等）默认不落盘，
            // 需要排查时由用户把这里改成 "Info"。
            new ConfigKey("ErrorLevel", false)
        };

        private sealed class ConfigKey
        {
            public readonly string Name;

            /// <summary>布尔键在写出时按原生 true/false，而不是带引号的 "True"。</summary>
            public readonly bool IsBoolean;

            public ConfigKey(string name, bool isBoolean)
            {
                Name = name;
                IsBoolean = isBoolean;
            }
        }

        #endregion

        /// <summary>
        /// 可注入的原子写入器。为 null 时用本类自己的实现。
        /// <see cref="AppSettings"/> 把自己的 WriteAtomic 挂上来，
        /// 这样「原子写」这件事在契约面与实现面是同一份代码，不会各写一遍。
        /// </summary>
        internal Func<string, string, bool> WriteAtomicPath { get; set; }

        /// <summary>当前配置文件路径。</summary>
        public string ConfigPath
        {
            get { return AppPaths.SettingsPath; }
        }

        #region 配置加载与保存

        /// <summary>
        /// 读配置。文件缺失或损坏时落一份默认文件并返回默认值；
        /// 单个键解析失败只回落该键的默认值，不整份作废。
        /// </summary>
        public AppSettings Load()
        {
            AppSettings settings = null;

            // 读取失败不抛给调用方：程序必须始终能启动。
            try
            {
                Dictionary<string, string> values = LoadValues();
                if (values == null) return new AppSettings();

                // 缺键补齐：读回来的键数少于键表就说明文件是旧版或手改少了，
                // 补一份完整的写回，保证磁盘与代码一致。
                if (HasMissingKeys(values))
                {
                    SaveDocument(BuildDocument(values));
                    Dictionary<string, string> reloaded = LoadValues();
                    if (reloaded != null) values = reloaded;
                }

                settings = new AppSettings();
                ApplyValues(settings, values);
                settings.Normalize();
                return settings;
            }
            catch (Exception ex)
            {
                new LogService().WriteLog(
                    Localization.Format("LogSettingsLoadFailed", ex.Message),
                    LogService.ErrorLevel.Warning);
                return settings ?? new AppSettings();
            }
        }

        /// <summary>
        /// 保存配置。以现有文件为基底再覆盖已知键：用户手工加的扩展键得以保留
        /// —— 直接整份重写会悄悄删掉用户自己写进去的注释性键。
        /// </summary>
        public bool Save(AppSettings settings)
        {
            if (settings == null) return false;

            try
            {
                settings.Normalize();

                Dictionary<string, string> values = LoadValues()
                    ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                Set(values, "Threads", settings.Threads.ToString(CultureInfo.InvariantCulture));
                Set(values, "UpperCase", settings.UpperCase.ToString());
                Set(values, "AutoStartAfterAdd", settings.AutoStartAfterAdd.ToString());
                Set(values, "AutoLoadCompanion", settings.AutoLoadCompanion.ToString());
                Set(values, "WriteBom", settings.WriteBom.ToString());
                Set(values, "RememberWindow", settings.RememberWindow.ToString());
                Set(values, "AutoFitColumns", settings.AutoFitColumns.ToString());

                // 兜底也走 HashAlgorithms.Default.Id，不写死 "sha256"：
                // 默认算法只在 HashAlgorithms 里定义一次，本文件任何一处都不该另写一份。
                Set(values, "Algorithm", settings.AlgorithmId ?? HashAlgorithms.Default.Id);

                // 语言与算法一样存「标识」而不是显示名：显示名跟语言走，标识不跟。
                // 先以 Localization 的实际生效值为准再落盘：内存里的 Language 可能是
                // 被 Normalize 改过之前的旧值，而界面已经是按 CurrentLanguage 渲染的。
                if (Localization.IsSupported(Localization.CurrentLanguage))
                {
                    settings.Language = Localization.CurrentLanguage;
                }
                Set(values, "Language", settings.Language ?? Localization.FallbackLanguage);

                // 没记录过窗口位置（NaN）时不写坐标——JSON 没有 NaN，
                // 写进去再读回来会解析失败，等于把其他设置也一起丢掉。
                if (!double.IsNaN(settings.WindowLeft) && !double.IsNaN(settings.WindowTop))
                {
                    Set(values, "WindowLeft", Number(settings.WindowLeft));
                    Set(values, "WindowTop", Number(settings.WindowTop));
                }

                Set(values, "WindowWidth", Number(settings.WindowWidth));
                Set(values, "WindowHeight", Number(settings.WindowHeight));
                Set(values, "WindowMaximized", settings.WindowMaximized.ToString());
                Set(values, "ErrorLevel", LogService.MinLogLevel.ToString());

                return SaveDocument(BuildDocument(values));
            }
            catch (Exception ex)
            {
                new LogService().WriteLog(
                    Localization.Format("LogSettingsSaveFailed", ex.Message),
                    LogService.ErrorLevel.Warning);
                return false;
            }
        }

        /// <summary>只改单个键，其余内容原样保留。</summary>
        public bool Update(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;

            try
            {
                Dictionary<string, string> values = LoadValues();
                if (values == null) return false;

                Set(values, key, value);
                return SaveDocument(BuildDocument(values));
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region 配置文档读写

        /// <summary>读完整份配置为键值表；文件不存在时先落默认文件，损坏时重建。</summary>
        private Dictionary<string, string> LoadValues()
        {
            string path = ConfigPath;

            if (!File.Exists(path))
            {
                if (!CreateDefaultConfig(path)) return null;
            }

            try
            {
                string text = File.ReadAllText(path, new System.Text.UTF8Encoding(false));

                JsonValue root = JsonParser.Parse(text);
                if (root != null && root.Kind == JsonKind.Object) return Flatten(root);
            }
            catch (JsonException)
            {
                // 语法坏了：下面重建默认配置。
            }
            catch (IOException)
            {
                // 读不出来就重建，与解析失败同路处理。
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }

            // 解析失败：文件损坏，重建一份默认配置。
            if (!CreateDefaultConfig(path, true)) return null;

            try
            {
                JsonValue rebuilt = JsonParser.Parse(
                    File.ReadAllText(path, new System.Text.UTF8Encoding(false)));
                return rebuilt != null && rebuilt.Kind == JsonKind.Object ? Flatten(rebuilt) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>把 JSON 对象摊平成键值表，值统一为字符串，供键表驱动逻辑使用。</summary>
        private static Dictionary<string, string> Flatten(JsonValue root)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root == null || root.Kind != JsonKind.Object) return values;

            foreach (string key in root.Keys)
            {
                JsonValue item = root.Get(key);
                if (item == null) continue;

                switch (item.Kind)
                {
                    case JsonKind.String:
                        values[key] = item.Text ?? string.Empty;
                        break;
                    case JsonKind.Bool:
                        values[key] = item.Bool.ToString();
                        break;
                    case JsonKind.Number:
                        values[key] = item.Number.ToString("R", CultureInfo.InvariantCulture);
                        break;
                    default:
                        // 嵌套对象 / 数组不是本项目的配置形态，跳过而不是报错。
                        break;
                }
            }

            return values;
        }

        /// <summary>把键值表按固定键序写成 JSON 文本；不在键表里的扩展键排到最后并保持相对顺序。</summary>
        private static string BuildDocument(Dictionary<string, string> values)
        {
            var entries = new List<KeyValuePair<string, string>>(_keys.Length + 8);
            var isBoolean = new List<bool>(_keys.Length + 8);

            foreach (ConfigKey key in _keys)
            {
                // 缺键按默认值写出：调用方可能只填了部分键。
                string value = values.TryGetValue(key.Name, out string existing)
                    ? existing
                    : DefaultValue(key.Name);
                entries.Add(new KeyValuePair<string, string>(key.Name, value));
                isBoolean.Add(key.IsBoolean);
            }

            foreach (KeyValuePair<string, string> pair in values)
            {
                if (IsKnownKey(pair.Key)) continue;
                entries.Add(pair);
                isBoolean.Add(false);
            }

            return JsonConfig.Build(entries, isBoolean);
        }

        /// <summary>先写临时文件再原子替换：写到一半崩溃不会留下半截 JSON。</summary>
        private bool SaveDocument(string content)
        {
            return SaveDocumentTo(ConfigPath, content);
        }

        /// <summary>
        /// 原子写入到指定路径。供 <see cref="AppSettings"/> 的 WriteAtomic 入口复用：
        /// 「设置的写入是原子的」这条保证在契约面就近可见，不必追到私有方法。
        /// </summary>
        internal bool WriteAtomicFile(string path, string content)
        {
            return SaveDocumentTo(path, content);
        }

        private bool SaveDocumentTo(string path, string content)
        {
            Func<string, string, bool> writer = WriteAtomicPath;
            if (writer != null) return writer(path, content);

            string tempPath = path + ".tmp";

            try
            {
                // 目录入口统一走 AppPaths：它是数据目录的唯一出处，
                // 这里再各自 Directory.CreateDirectory 会绕开改道逻辑。
                AppPaths.EnsureSettingsFolder();

                JsonConfig.WriteFile(tempPath, content);

                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, null, true);
                }
                else
                {
                    File.Move(tempPath, path);
                }

                return true;
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* 有意忽略：残留的临时文件删不掉不影响配置写入结果 */ }
                return false;
            }
        }

        #endregion

        #region 键值应用与容错

        /// <summary>把键值表灌进 <see cref="AppSettings"/>，逐键做校验与回落。</summary>
        private static void ApplyValues(AppSettings settings, Dictionary<string, string> values)
        {
            settings.Threads = ParseInt(values, "Threads", settings.Threads);
            settings.UpperCase = ParseBool(values, "UpperCase", settings.UpperCase);
            settings.AutoStartAfterAdd = ParseBool(values, "AutoStartAfterAdd", settings.AutoStartAfterAdd);
            settings.AutoLoadCompanion = ParseBool(values, "AutoLoadCompanion", settings.AutoLoadCompanion);
            settings.WriteBom = ParseBool(values, "WriteBom", settings.WriteBom);
            settings.RememberWindow = ParseBool(values, "RememberWindow", settings.RememberWindow);
            settings.AutoFitColumns = ParseBool(values, "AutoFitColumns", settings.AutoFitColumns);

            settings.AlgorithmId = ParseAlgorithm(GetValue(values, "Algorithm", null));
            settings.Language = ParseLanguage(GetValue(values, "Language", null));

            settings.WindowLeft = ParseDouble(values, "WindowLeft", settings.WindowLeft);
            settings.WindowTop = ParseDouble(values, "WindowTop", settings.WindowTop);
            settings.WindowWidth = ParseDouble(values, "WindowWidth", settings.WindowWidth);
            settings.WindowHeight = ParseDouble(values, "WindowHeight", settings.WindowHeight);
            settings.WindowMaximized = ParseBool(values, "WindowMaximized", settings.WindowMaximized);

            // 日志阈值既写进配置，也立刻作用到 LogService：配置是「用户意图」，
            // 静态属性是「本次进程的实际阈值」，两者必须在同一处一起更新。
            LogService.MinLogLevel = ParseErrorLevel(GetValue(values, "ErrorLevel", null));
        }

        /// <summary>取值统一做空值校验，非法内容返回 null 交给调用方决定默认值。</summary>
        private static string GetValue(Dictionary<string, string> values, string key, string defaultValue)
        {
            string value = values.TryGetValue(key, out string raw) ? raw : null;
            if (string.IsNullOrWhiteSpace(value)) return defaultValue;
            return value.Trim();
        }

        private static bool ParseBool(Dictionary<string, string> values, string key, bool defaultValue)
        {
            string value = values.TryGetValue(key, out string raw) ? raw : null;
            if (string.IsNullOrWhiteSpace(value)) return defaultValue;

            // 布尔解析失败时保持默认值而不是当作 false，否则拼错的 true 会静默改掉行为。
            return bool.TryParse(value.Trim(), out bool result) ? result : defaultValue;
        }

        private static int ParseInt(Dictionary<string, string> values, string key, int defaultValue)
        {
            string value = values.TryGetValue(key, out string raw) ? raw : null;
            if (string.IsNullOrWhiteSpace(value)) return defaultValue;

            return int.TryParse(value.Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int result) ? result : defaultValue;
        }

        private static double ParseDouble(Dictionary<string, string> values, string key, double defaultValue)
        {
            string value = values.TryGetValue(key, out string raw) ? raw : null;
            if (string.IsNullOrWhiteSpace(value)) return defaultValue;

            return double.TryParse(value.Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double result) ? result : defaultValue;
        }

        /// <summary>
        /// 校验算法标识：认得出就用规范标识（sha256 / SHA-256 / sha2-256 都归到 sha256），
        /// 认不出回落默认。规范化放在这里，配置里同一个算法就只有一种写法。
        /// </summary>
        private static string ParseAlgorithm(string text)
        {
            HashAlgorithmInfo info = HashAlgorithms.ByName(text ?? string.Empty);
            if (info != null) return info.Id;

            // 无法识别的算法只记一条警告并回落默认，配置异常不该阻断启动。
            // 静默回落会让用户以为「我明明写了 sha3-256」却一直在算 sha256。
            if (!string.IsNullOrWhiteSpace(text))
            {
                TryLogWarning(Localization.Format("LogUnknownAlgorithm", text, HashAlgorithms.Default.Id));
            }

            return HashAlgorithms.Default.Id;
        }

        /// <summary>校验语言代号：认不出回落默认语言。</summary>
        private static string ParseLanguage(string text)
        {
            if (Localization.IsSupported(text)) return text.Trim();

            if (!string.IsNullOrWhiteSpace(text))
            {
                TryLogWarning(Localization.Format("LogUnknownLanguage", text, Localization.FallbackLanguage));
            }

            return Localization.FallbackLanguage;
        }

        /// <summary>
        /// 记一条警告，但**永不因为记日志失败而影响配置读取**。
        /// 解析发生在启动路径上，日志目录不可写（只读介质、权限受限）时必须继续。
        /// </summary>
        private static void TryLogWarning(string message)
        {
            try
            {
                new LogService().WriteLog(message, LogService.ErrorLevel.Warning);
            }
            catch
            {
                // 有意忽略：日志是辅助信息，不能让它挡住启动。
            }
        }

        /// <summary>
        /// 解析日志级别阈值。认不出（含空值）回落 Warning：
        /// 阈值只影响「记多少日志」，配错了不该让程序起不来。
        /// </summary>
        private static LogService.ErrorLevel ParseErrorLevel(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return LogService.ErrorLevel.Warning;

            switch (text.Trim().ToLowerInvariant())
            {
                case "info": return LogService.ErrorLevel.Info;
                case "warning": return LogService.ErrorLevel.Warning;
                case "error": return LogService.ErrorLevel.Error;
            }

            return LogService.ErrorLevel.Warning;
        }

        #endregion

        #region 节点写回

        private static void Set(Dictionary<string, string> values, string key, string value)
        {
            values[key] = value ?? string.Empty;
        }

        private static bool IsKnownKey(string key)
        {
            foreach (ConfigKey candidate in _keys)
            {
                if (candidate.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        private static bool HasMissingKeys(Dictionary<string, string> values)
        {
            foreach (ConfigKey key in _keys)
            {
                if (!values.ContainsKey(key.Name)) return true;
            }

            return false;
        }

        #endregion

        #region 默认配置重建

        private static string DefaultValue(string key)
        {
            switch (key)
            {
                case "Threads": return new AppSettings().Threads.ToString(CultureInfo.InvariantCulture);
                case "UpperCase": return bool.FalseString;
                case "AutoStartAfterAdd": return bool.FalseString;
                case "AutoLoadCompanion": return bool.TrueString;
                case "WriteBom": return bool.FalseString;
                case "RememberWindow": return bool.TrueString;
                case "AutoFitColumns": return bool.TrueString;
                case "Algorithm": return HashAlgorithms.Default.Id;
                case "Language": return Localization.ResolveSystemLanguage();
                case "WindowLeft": return string.Empty;
                case "WindowTop": return string.Empty;
                case "WindowWidth": return "1120";
                case "WindowHeight": return "620";
                case "WindowMaximized": return bool.FalseString;
                case "ErrorLevel": return LogService.ErrorLevel.Warning.ToString();
                default: return string.Empty;
            }
        }

        /// <summary>
        /// 重建用于文件缺失与文件损坏两种情况；损坏时必须覆盖写入，
        /// 否则修不好的旧文件会一直被读到。
        /// </summary>
        private static bool CreateDefaultConfig(string configPath, bool forceOverwrite = false)
        {
            if (File.Exists(configPath) && !forceOverwrite) return true;

            var settings = new AppSettings();
            settings.Normalize();

            var values = new Dictionary<string, string>(_keys.Length, StringComparer.OrdinalIgnoreCase);
            foreach (ConfigKey key in _keys)
            {
                values[key.Name] = DefaultValue(key.Name);
            }

            values["Threads"] = settings.Threads.ToString(CultureInfo.InvariantCulture);
            values["Algorithm"] = settings.AlgorithmId;

            string tempPath = configPath + ".tmp";
            try
            {
                AppPaths.EnsureSettingsFolder();

                JsonConfig.WriteFile(tempPath, BuildDocument(values));

                if (File.Exists(configPath))
                {
                    File.Replace(tempPath, configPath, null, true);
                }
                else
                {
                    File.Move(tempPath, configPath);
                }

                return true;
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* 有意忽略 */ }
                return false;
            }
        }

        #endregion

        private static string Number(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
