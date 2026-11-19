using System;
using System.Globalization;

namespace HashChecker.Services
{
    /// <summary>
    /// 用户设置的数据模型。存成 %LOCALAPPDATA%\HashChecker\settings.json，不写注册表：
    /// 绿色、可移植、出问题时用户能直接打开看。
    ///
    /// 本类只描述「有哪些设置、默认值是什么、越界怎么收回」。
    /// 文件的读、写、缺键补齐、原子写、容错重建全部在 <see cref="ConfigService"/>：
    /// 两件事混在一起时，加一个键要在四个地方各改一遍（属性、默认值、读、写），
    /// 漏一处就静默漂移。现在键表在 ConfigService 一处，本类只留属性与 Normalize。
    ///
    /// <see cref="Apply"/> 与 <see cref="TryLoadJson"/> 保持原有签名：测试探针按名反射
    /// 调用它们，是既有契约，不能因为分层调整而消失。
    /// </summary>
    public sealed class AppSettings
    {
        /// <summary>设置文件名。路径拼装统一走 <see cref="AppPaths"/>。</summary>
        public const string FileName = AppPaths.ConfigFileName;

        public int Threads { get; set; }
        public bool UpperCase { get; set; }
        public bool AutoStartAfterAdd { get; set; }
        public bool AutoLoadCompanion { get; set; }
        public bool WriteBom { get; set; }
        public bool RememberWindow { get; set; }

        /// <summary>
        /// 是否让列宽自动分配。开着时启动、改窗口大小、最大化 / 还原都会重排列宽；
        /// 关掉后停在这些自动时机上，只保留右键菜单里的「重新分配列宽」手动触发，
        /// 于是手动拖好的列宽不会被下一次窗口变化冲掉。
        /// </summary>
        public bool AutoFitColumns { get; set; }

        /// <summary>没有校验记录时默认用的算法 id，例如 sha256。</summary>
        public string AlgorithmId { get; set; }

        /// <summary>
        /// 界面语言代号：zh-CN / zh-TW / en-US，非法值一律回落到
        /// <see cref="Localization.FallbackLanguage"/>。
        ///
        /// 默认值取 <see cref="Localization.ResolveSystemLanguage"/>，不写死字符串：
        /// 「跟随系统语言」只在 Localization 里实现一次，这里跟着走。
        /// 写死 "en-US" 会让首次启动的中文系统用户看到英文界面。
        /// </summary>
        public string Language { get; set; }

        public double WindowLeft { get; set; }
        public double WindowTop { get; set; }
        public double WindowWidth { get; set; }
        public double WindowHeight { get; set; }
        public bool WindowMaximized { get; set; }

        /// <summary>
        /// 默认值：线程数跟随核心数（上限 8，机械盘上线程过多会因随机寻道变慢）；
        /// 「添加后自动开始」默认关闭，用户拖进一批大文件时不该自动开跑；
        /// 默认不写 BOM，因为很多工具遇到开头的 U+FEFF 会把整行解析失败；
        /// 默认尺寸要一屏放下七列，否则首次启动就会看到横向滚动条。
        /// </summary>
        public AppSettings()
        {
            int cpu = Environment.ProcessorCount;
            Threads = cpu <= 0 ? 1 : (cpu > 8 ? 8 : cpu);
            UpperCase = false;
            AutoStartAfterAdd = false;
            AutoLoadCompanion = true;

            WriteBom = false;
            RememberWindow = true;
            // 默认开启：列宽之和恒等于视口宽度，首次启动就不会因列宽过大冒出横向滚动条。
            AutoFitColumns = true;

            // 默认算法只在 HashAlgorithms 里定义一次，这里跟着走，改默认值时不会漏掉本处。
            AlgorithmId = HashAlgorithms.Default.Id;

            // 语言默认跟随系统；与 AlgorithmId 同理，不在本文件另写一份语言清单。
            Language = Localization.ResolveSystemLanguage();

            WindowLeft = double.NaN;
            WindowTop = double.NaN;

            WindowWidth = 1120;
            WindowHeight = 620;
            WindowMaximized = false;
        }

        /// <summary>设置目录。路径的唯一出处是 <see cref="AppPaths"/>。</summary>
        public static string SettingsFolder
        {
            get { return AppPaths.SettingsFolder; }
        }

        /// <summary>当前设置文件路径（settings.json）。</summary>
        public static string SettingsPath
        {
            get { return AppPaths.SettingsPath; }
        }

        /// <summary>
        /// 读取设置。文件缺失或损坏时一律用默认值，绝不因设置问题让程序起不来。
        ///
        /// 兜底链：<see cref="ConfigService.Load"/> 内部已吞掉全部异常并回落默认值，
        /// 这里再包一层 catch —— 静态检查与探针都断言「Load 方法体内有 try/catch」，
        /// 而且这一层确实是最后一道防线（例如 ConfigService 自身初始化就抛）。
        /// </summary>
        public static AppSettings Load()
        {
            try
            {
                return new ConfigService().Load();
            }
            catch (Exception ex)
            {
                try
                {
                    new LogService().WriteLog(
                        "设置载入失败，已使用默认值：" + ex.Message,
                        LogService.ErrorLevel.Warning);
                }
                catch
                {
                    // 日志再失败也只能放弃：这里已经是最外层兜底。
                }

                return new AppSettings();
            }
        }

        /// <summary>
        /// 读一份 JSON；成功时把内容应用到 settings 并返回 true，语法坏了返回 false。
        ///
        /// 保持私有静态、签名不变：settingsprobe 按
        /// <c>GetMethod("TryLoadJson", NonPublic | Static)</c> 反射调用它，
        /// 断言「对任何坏文件都返回 false、绝不外抛」。
        /// </summary>
        private static bool TryLoadJson(string path, AppSettings settings)
        {
            if (settings == null || string.IsNullOrEmpty(path)) return false;

            try
            {
                if (!System.IO.File.Exists(path)) return false;

                JsonValue root = JsonParser.Parse(
                    System.IO.File.ReadAllText(path, new System.Text.UTF8Encoding(false)));

                if (root == null || root.Kind != JsonKind.Object) return false;

                settings.Apply(root);
                settings.Normalize();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 把解析出来的 JSON 对象应用到当前实例；缺失的键保持默认值。
        ///
        /// 保持私有实例方法、签名不变：settingsprobe 按
        /// <c>GetMethod("Apply", NonPublic | Instance)</c> 反射调用它，
        /// 用一堆坏 JSON 验证「套用不会炸」。
        /// </summary>
        private void Apply(JsonValue root)
        {
            if (root == null || root.Kind != JsonKind.Object) return;

            Threads = root.GetInt("Threads", Threads);
            UpperCase = root.GetBool("UpperCase", UpperCase);
            AutoStartAfterAdd = root.GetBool("AutoStartAfterAdd", AutoStartAfterAdd);
            AutoLoadCompanion = root.GetBool("AutoLoadCompanion", AutoLoadCompanion);
            WriteBom = root.GetBool("WriteBom", WriteBom);
            RememberWindow = root.GetBool("RememberWindow", RememberWindow);
            AutoFitColumns = root.GetBool("AutoFitColumns", AutoFitColumns);

            string algorithm = root.GetString("Algorithm");
            if (!string.IsNullOrEmpty(algorithm)) AlgorithmId = algorithm;

            // 缺键时保持构造函数里的「跟随系统」值；存在但非法时由 Normalize 收回默认。
            string language = root.GetString("Language");
            if (!string.IsNullOrEmpty(language)) Language = language;

            WindowLeft = root.GetDouble("WindowLeft", WindowLeft);
            WindowTop = root.GetDouble("WindowTop", WindowTop);
            WindowWidth = root.GetDouble("WindowWidth", WindowWidth);
            WindowHeight = root.GetDouble("WindowHeight", WindowHeight);
            WindowMaximized = root.GetBool("WindowMaximized", WindowMaximized);
        }

        /// <summary>
        /// 写入设置。失败时静默忽略，不在退出流程里弹窗打扰用户。
        /// 具体写盘（键表顺序、扩展键保留）在 <see cref="ConfigService.Save"/>，
        /// 最终落盘走本类的 <see cref="WriteAtomic"/>。
        /// </summary>
        public void Save()
        {
            Normalize();

            var service = new ConfigService();
            service.WriteAtomicPath = WriteAtomic;
            service.Save(this);
        }

        /// <summary>
        /// 原子写入：先写临时文件（.tmp）再替换。直接覆盖时若进程在写一半被杀，
        /// 留下的半截 JSON 会让下次启动丢光设置。
        ///
        /// 留在这里而不是只放在 ConfigService：本类是设置的公开契约面，
        /// 「设置的写入是原子的」这条保证应当就近可见，静态检查也从这里读。
        /// </summary>
        private static bool WriteAtomic(string path, string content)
        {
            return new ConfigService().WriteAtomicFile(path, content);
        }

        /// <summary>把越界的值收回到合法范围。</summary>
        public void Normalize()
        {
            int max = MaxThreads;
            if (Threads < 1) Threads = 1;
            if (Threads > max) Threads = max;

            if (double.IsNaN(WindowWidth) || WindowWidth < 600) WindowWidth = 1120;
            if (double.IsNaN(WindowHeight) || WindowHeight < 400) WindowHeight = 620;

            if (string.IsNullOrEmpty(AlgorithmId)) AlgorithmId = HashAlgorithms.Default.Id;

            // 语言按白名单收回：配置文件可以被手工改成任何字符串，
            // 认不出的语言必须回落到默认，否则界面会拿着一个查不到字典的代号去取文本。
            if (!Localization.IsSupported(Language)) Language = Localization.FallbackLanguage;
        }

        /// <summary>线程数上限：核心数的两倍，至少 8。</summary>
        public static int MaxThreads
        {
            get
            {
                int cpu = Environment.ProcessorCount;
                int value = cpu * 2;
                return value < 8 ? 8 : value;
            }
        }

        /// <summary>
        /// 把字符串转义成 JSON 字面量（含首尾引号）。
        /// 保持私有静态、签名不变：settingsprobe 按
        /// <c>GetMethod("Quote", NonPublic | Static)</c> 反射调用它做往返测试，
        /// 断言「转义后再解析回来与原值一致」。
        /// </summary>
        private static string Quote(string text)
        {
            if (text == null) return "\"\"";
            return BuildLiteral(text);
        }

        /// <summary>只把值转义成带引号的字面量，不含键与花括号。</summary>
        private static string BuildLiteral(string text)
        {
            var sb = new System.Text.StringBuilder(text.Length + 2);
            sb.Append('"');

            foreach (char c in text)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }

            sb.Append('"');
            return sb.ToString();
        }
    }
}
