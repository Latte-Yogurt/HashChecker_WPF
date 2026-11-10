using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace HashChecker.Services
{
    /// <summary>
    /// 轻量用户设置。存成 %LOCALAPPDATA%\HashChecker\settings.json，不写注册表：
    /// 绿色、可移植、出问题时用户能直接打开看。
    ///
    /// 手写 JSON 而不引第三方库：.NET Framework 4.7.2 没有内置 JSON 序列化器，
    /// System.Text.Json 只属于 .NET Core，System.Web.Extensions 又会为十个扁平键
    /// 往便携工具里拖进一个很大的程序集。只要「扁平对象 + 数字 / 布尔 / 字符串」时，
    /// 手写读写更短，也能完全控制容错行为。
    /// </summary>
    public sealed class AppSettings
    {
        private const string FileName = "settings.json";

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
            AlgorithmId = "sha256";
            WindowLeft = double.NaN;
            WindowTop = double.NaN;

            WindowWidth = 1120;
            WindowHeight = 620;
            WindowMaximized = false;
        }

        /// <summary>
        /// 设置目录：%LOCALAPPDATA%\&lt;程序集名&gt;，本机就是 %LOCALAPPDATA%\HashChecker。
        /// 用 LocalApplicationData 而不是 Roaming：这里存的是窗口坐标与线程数这类
        /// 与机器绑定的值，漫游到别的机器会把窗口放到屏幕之外。
        /// 目录名取程序集名，便携改名后配置目录跟着走，不会串到别人的配置里。
        /// </summary>
        public static string SettingsFolder
        {
            get
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(localAppData, FolderName);
            }
        }

        /// <summary>程序集名，用作配置目录名；取不到时退回固定名。</summary>
        private static string FolderName
        {
            get
            {
                try
                {
                    string name = System.Reflection.Assembly.GetEntryAssembly() == null
                        ? null
                        : System.Reflection.Assembly.GetEntryAssembly().GetName().Name;

                    return string.IsNullOrEmpty(name) ? DefaultFolderName : name;
                }
                catch
                {
                    return DefaultFolderName;
                }
            }
        }

        private const string DefaultFolderName = "HashChecker";

        /// <summary>当前设置文件路径（settings.json）。</summary>
        public static string SettingsPath
        {
            get { return Path.Combine(SettingsFolder, FileName); }
        }

        /// <summary>读取设置。文件缺失或损坏时一律用默认值，绝不因设置问题让程序起不来。</summary>
        public static AppSettings Load()
        {
            var settings = new AppSettings();

            try
            {
                if (TryLoadJson(SettingsPath, settings)) return settings;
            }
            catch
            {
                return new AppSettings();
            }

            settings.Normalize();
            return settings;
        }

        /// <summary>读一份 JSON；成功时把内容应用到 settings 并返回 true，语法坏了返回 false。</summary>
        private static bool TryLoadJson(string path, AppSettings settings)
        {
            if (!File.Exists(path)) return false;

            try
            {
                JsonValue root = JsonParser.Parse(File.ReadAllText(path, Encoding.UTF8));
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

        /// <summary>把解析出来的 JSON 对象应用到当前实例；缺失的键保持默认值。</summary>
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

            WindowLeft = root.GetDouble("WindowLeft", WindowLeft);
            WindowTop = root.GetDouble("WindowTop", WindowTop);
            WindowWidth = root.GetDouble("WindowWidth", WindowWidth);
            WindowHeight = root.GetDouble("WindowHeight", WindowHeight);
            WindowMaximized = root.GetBool("WindowMaximized", WindowMaximized);
        }

        /// <summary>
        /// 写入设置。失败时静默忽略，不在退出流程里弹窗打扰用户；
        /// 没记录过窗口位置（NaN）时不写坐标——JSON 没有 NaN，
        /// 写进去再读回来会解析失败，等于把其他设置也一起丢掉。
        /// </summary>
        public void Save()
        {
            Normalize();

            try
            {
                string path = SettingsPath;
                Directory.CreateDirectory(SettingsFolder);

                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"Threads\": " + Threads + ",");
                sb.AppendLine("  \"UpperCase\": " + BoolText(UpperCase) + ",");
                sb.AppendLine("  \"AutoStartAfterAdd\": " + BoolText(AutoStartAfterAdd) + ",");
                sb.AppendLine("  \"AutoLoadCompanion\": " + BoolText(AutoLoadCompanion) + ",");
                sb.AppendLine("  \"WriteBom\": " + BoolText(WriteBom) + ",");
                sb.AppendLine("  \"RememberWindow\": " + BoolText(RememberWindow) + ",");
                sb.AppendLine("  \"AutoFitColumns\": " + BoolText(AutoFitColumns) + ",");
                sb.AppendLine("  \"Algorithm\": " + Quote(AlgorithmId ?? "sha256") + ",");

                if (!double.IsNaN(WindowLeft) && !double.IsNaN(WindowTop))
                {
                    sb.AppendLine("  \"WindowLeft\": " + Number(WindowLeft) + ",");
                    sb.AppendLine("  \"WindowTop\": " + Number(WindowTop) + ",");
                }

                sb.AppendLine("  \"WindowWidth\": " + Number(WindowWidth) + ",");
                sb.AppendLine("  \"WindowHeight\": " + Number(WindowHeight) + ",");
                sb.AppendLine("  \"WindowMaximized\": " + BoolText(WindowMaximized));
                sb.AppendLine("}");

                WriteAtomic(path, sb.ToString());
            }
            catch
            {
                /* 设置保存失败不影响程序本身，退出流程里不弹窗打扰用户 */
            }
        }

        /// <summary>
        /// 原子写入：先写临时文件再替换。直接覆盖时若进程在写一半被杀，
        /// 留下的半截 JSON 会让下次启动丢光设置。
        /// </summary>
        private static void WriteAtomic(string path, string content)
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, content, new UTF8Encoding(false));

            if (File.Exists(path))
            {
                File.Replace(temp, path, null, true);
            }
            else
            {
                File.Move(temp, path);
            }
        }

        /// <summary>把越界的值收回到合法范围。</summary>
        public void Normalize()
        {
            int max = MaxThreads;
            if (Threads < 1) Threads = 1;
            if (Threads > max) Threads = max;

            if (double.IsNaN(WindowWidth) || WindowWidth < 600) WindowWidth = 1120;
            if (double.IsNaN(WindowHeight) || WindowHeight < 400) WindowHeight = 620;

            if (string.IsNullOrEmpty(AlgorithmId)) AlgorithmId = "sha256";
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

        private static string BoolText(bool value)
        {
            return value ? "true" : "false";
        }

        /// <summary>用 R 保证往返精度，用 InvariantCulture 保证小数点不随系统区域变成逗号。</summary>
        private static string Number(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        /// <summary>JSON 字符串转义：反斜杠、引号与控制字符都要处理。</summary>
        private static string Quote(string text)
        {
            var sb = new StringBuilder(text.Length + 2);
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
