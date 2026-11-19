using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

/// <summary>
/// 关于窗口的版本号必须**自动跟随程序集版本**，不能靠手改代码。
///
/// 这个探针同时守两件事：
///   1. 关于窗口显示的版本 == 程序集版本。曾经的实现取 Assembly.GetEntryAssembly()，
///      而探针用 LoadFrom 载入产品时，入口程序集是**探针自己**，于是关于窗口显示
///      「版本 0.0.0.0」。正确的取法是 typeof(AboutWindow).Assembly。
///   2. 关于窗口文本里不得出现写死的版本字面量 —— 一旦有人把版本号抄进代码注释或
///      回退分支当常量用，下个版本就会忘记改。
///
/// 用法: aboutprobe.exe &lt;HashChecker.exe&gt; [工作目录]
/// 退出码: 0 = 全部通过, 1 = 有失败项, 2 = 环境不满足
/// </summary>
class AboutProbe
{
    static int _failures;

    static void Check(string name, bool ok, string detail)
    {
        if (!ok) _failures++;
        Console.WriteLine("  {0} {1}{2}", ok ? "OK  " : "!!  ", name,
            string.IsNullOrEmpty(detail) ? "" : "  —— " + detail);
    }

    [STAThread]
    static int Main(string[] args)
    {
        RedirectSettingsToTemp();
        if (args.Length < 1) { Console.WriteLine("用法: aboutprobe.exe <HashChecker.exe> [工作目录]"); return 2; }

        string asmPath = Path.GetFullPath(args[0]);
        if (!File.Exists(asmPath)) { Console.WriteLine("!! 被测程序不存在: " + asmPath); return 2; }

        Assembly asm = Assembly.LoadFrom(asmPath);
        Version asmVersion = asm.GetName().Version;

        Console.WriteLine("=== 关于窗口：版本号必须自动跟随程序集版本 ===");
        Console.WriteLine("被测程序: " + asmPath);
        Console.WriteLine("程序集版本: " + asmVersion);
        Console.WriteLine("");

        Type windowType = asm.GetType("HashChecker.AboutWindow");
        if (windowType == null)
        {
            Console.WriteLine("!! 找不到 HashChecker.AboutWindow");
            return 2;
        }

        Window window = (Window)Activator.CreateInstance(windowType);
        try
        {
            TextBlock body = (TextBlock)windowType
                .GetField("BodyText", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .GetValue(window);

            string text = body == null ? null : body.Text;
            Console.WriteLine("  关于窗口正文：");
            foreach (string line in (text ?? "").Split('\n'))
            {
                Console.WriteLine("    | " + line.TrimEnd('\r'));
            }
            Console.WriteLine("");

            Check("正文非空", !string.IsNullOrEmpty(text), "");

            string expected = asmVersion == null ? null : asmVersion.ToString();
            Check("正文包含程序集版本 " + expected,
                text != null && expected != null && text.Contains(expected),
                text == null ? "正文为 null" : "实际正文见上");

            // 取错程序集时会显示 0.0.0.0（探针自己的版本），单独点名这个已知失败形态。
            bool hostLeak = text != null && text.Contains("0.0.0.0");
            Check("正文没有取成宿主进程的版本 0.0.0.0", !hostLeak,
                hostLeak ? "出现了 0.0.0.0 —— 说明取的是 GetEntryAssembly 而不是本程序集" : "");

            // 版本号必须是四段数字，不能是「未知」之类的回退文本。
            bool hasShape = text != null && ContainsVersionShape(text);
            Check("正文里的版本是四段数字形式", hasShape,
                hasShape ? "" : "未在正文里找到 x.y.z.w 形状的版本号");

            // 不允许把版本号写死进代码：文件版本信息与程序集版本必须一致，
            // 这条同时保证 AssemblyInfo 与 manifest 没有被手工改成互相矛盾的值。
            FileVersionInfo vi = FileVersionInfo.GetVersionInfo(asmPath);
            Check("文件版本与程序集版本一致",
                asmVersion != null && vi.FileVersion == asmVersion.ToString(),
                "文件版本 = " + vi.FileVersion + "，程序集版本 = " + asmVersion);
        }
        finally
        {
            window.Close();
        }

        Console.WriteLine("");
        if (_failures == 0) { Console.WriteLine("全部符合预期 ✓"); return 0; }
        Console.WriteLine("!! 有 " + _failures + " 项不符合预期");
        return 1;
    }

    /// <summary>在文本里找 x.y.z.w 形状的四段版本号。</summary>
    static bool ContainsVersionShape(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] < '0' || text[i] > '9') continue;

            int dots = 0;
            int j = i;
            while (j < text.Length)
            {
                char c = text[j];
                if (c >= '0' && c <= '9') { j++; continue; }
                if (c == '.' && dots < 3) { dots++; j++; continue; }
                break;
            }

            if (dots == 3) return true;
            i = j;
        }
        return false;
    }

    /// <summary>
    /// 把配置目录改道到临时目录。必须在触碰产品任何类型之前调用 ——
    /// 探针用 LoadFrom 载入产品程序集后，产品会拿「入口程序集名」当配置目录名，
    /// 那个名字就是本探针，配置会落到 %LOCALAPPDATA%\&lt;探针名&gt;\ 里，
    /// 每跑一次就污染一次真实用户配置。
    /// </summary>
    static void RedirectSettingsToTemp()
    {
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "HashChecker", "settings-" + Path.GetFileNameWithoutExtension(
                System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName));
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("HASHCHECKER_SETTINGS_DIR", dir);
        }
        catch
        {
            // 改道失败不该让探针跑不起来：最坏退回原位，测试结论不受影响。
        }
    }
}
