using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

/// <summary>
/// 端到端：把一份真实的 .blake3 记录交给真实的 MainWindow，等它算完，
/// 断言那一行判定为「通过」。这一条覆盖的是用户真正会走的路径 ——
/// 目录、内核、记录解析、引擎、状态推导全都要对，缺一个就是「校验失败」。
///
/// 期望值由官方 blake3 包算出，不是从产品自己那儿抄的。
/// 另外几条探针都是「分别验证某一层」，只有这里验证它们拼在一起还能用。
///
/// 用法: guiprobe.exe &lt;HashChecker.exe&gt; &lt;工作目录&gt; &lt;期望的 BLAKE3 值&gt;
/// 退出码: 0 = 全部通过, 1 = 有失败项, 2 = 用法错误
/// </summary>
class GuiProbe
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
        if (args.Length < 3)
        {
            Console.WriteLine("用法: guiprobe.exe <HashChecker.exe> <工作目录> <期望的 BLAKE3 值>");
            return 2;
        }

        string asmPath = Path.GetFullPath(args[0]);
        string work = Path.GetFullPath(args[1]);
        string expected = args[2].Trim().ToLowerInvariant();

        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);

        // 一份 10240 字节的文件，内容与生成期望值时用的完全一致
        string target = Path.Combine(work, "sample.bin");
        var data = new byte[10240];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i & 0xFF);
        File.WriteAllBytes(target, data);

        string record = target + ".blake3";
        File.WriteAllText(record, expected + " *sample.bin\r\n", new UTF8Encoding(false));

        var asm = Assembly.LoadFrom(asmPath);
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string simple = new AssemblyName(e.Name).Name;
            string candidate = Path.Combine(Path.GetDirectoryName(asmPath), simple + ".exe");
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        };

        Console.WriteLine("=== 端到端：拖入 .blake3 记录必须判定通过 ===");
        Console.WriteLine("被测程序: " + asmPath);
        Console.WriteLine("");

        Type windowType = asm.GetType("HashChecker.MainWindow");
        Window window = (Window)Activator.CreateInstance(windowType);

        try
        {
            // AddPaths(paths, autoStart) 是产品自己的入口，拖放与命令行最终都走它。
            // 记录载入后自动开始计算（autoStart=true），这正是用户拖入记录时的行为。
            MethodInfo addPaths = windowType.GetMethod("AddPaths",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            if (addPaths == null) { Console.WriteLine("!! 找不到 MainWindow.AddPaths"); return 1; }

            addPaths.Invoke(window, new object[] { new[] { record }, true });

            ListView list = (ListView)windowType
                .GetField("ItemList", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .GetValue(window);

            Check("记录反推出了一行", list.Items.Count == 1, "列表 " + list.Items.Count + " 行");
            if (list.Items.Count == 0) return 1;

            object item = list.Items[0];
            Check("这一行是 payload 本身，不是记录",
                string.Equals((string)Get(item, "Name"), "sample.bin", StringComparison.OrdinalIgnoreCase),
                (string)Get(item, "Name"));
            Check("保存值已从记录填入", (string)Get(item, "ExpectedHash") == expected,
                (string)Get(item, "ExpectedHash"));

            // AddPaths 里的自动开始是通过 async void 触发的，这里只等结果落盘，
            // 不再自己去调启动入口 —— 那会把 async 状态机掰成两半。
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < 30 && string.IsNullOrEmpty((string)Get(item, "CurrentHash")))
            {
                DispatcherFrame frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(delegate { frame.Continue = false; }));
                Dispatcher.PushFrame(frame);
                System.Threading.Thread.Sleep(20);
            }

            string current = (string)Get(item, "CurrentHash");
            Check("算出了当前值", !string.IsNullOrEmpty(current), current ?? "(空)");
            Check("当前值等于官方参考值", current == expected,
                current == expected ? "" : "实际=" + current + " 期望=" + expected);

            string hashAlgorithm = (string)Get(item, "AlgorithmText");
            Check("算法列显示 BLAKE3", hashAlgorithm == "BLAKE3", hashAlgorithm ?? "(空)");

            string status = (string)Get(item, "StatusText");
            bool passed = (bool)Get(item, "IsPassed");
            Check("这一行判定为通过", passed, "状态=" + status);
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

    /// <summary>没有公开启动入口时的退路：直接调引擎算，再按产品的方式写回行。</summary>
    static void DriveEngine(Assembly asm, Window window, object item, string path)
    {
        Type engineType = asm.GetType("HashChecker.Services.HashingEngine");
        object engine = Activator.CreateInstance(engineType);

        MethodInfo hashFile = engineType.GetMethod("HashFile", BindingFlags.NonPublic | BindingFlags.Instance);
        object required = item.GetType().GetProperty("RequiredAlgorithms").GetValue(item);

        object[] argv = hashFile.GetParameters().Length >= 4
            ? new object[] { path, required, new FileInfo(path).Length, System.Threading.CancellationToken.None }
            : new object[] { path, required, System.Threading.CancellationToken.None };

        object digests = hashFile.Invoke(engine, argv);
        string value = (string)digests.GetType().GetProperty("Item").GetValue(digests, new object[] { "blake3" });
        object algorithm = digests == null ? null : required.GetType().GetProperty("Item").GetValue(required, new object[] { 0 });

        item.GetType().GetMethod("ApplyHash").Invoke(item, new object[] { value, digests, algorithm });
    }

    static object Get(object instance, string property)
    {
        return instance.GetType().GetProperty(property).GetValue(instance);
    }

    /// <summary>
    /// 把配置目录改道到临时目录。必须在触碰产品任何类型之前调用 ——
    /// 探针用 LoadFrom 载入产品程序集后，产品会拿「入口程序集名」当配置目录名，
    /// 那个名字就是本探针，配置会落到 %LOCALAPPDATA%\<探针名>\ 里，
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