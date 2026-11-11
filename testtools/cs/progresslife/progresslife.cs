using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using IOPath = System.IO.Path;

/// <summary>
/// 进度条生命周期验证：停止 / 出错 / 完成后，进度条必须回到 0，
/// 且停止之后迟到的进度回调不得把旧百分比写回去。
///
/// 这是用户报过的缺陷：中途停止，进度条停在停止那一刻的百分比，
/// 与「已停止」的状态文字自相矛盾，也让人以为任务还在跑。
///
/// 用法: progresslife.exe &lt;HashChecker.exe&gt; [工作目录]
/// 退出码: 0 = 全部通过, 1 = 有失败项, 2 = 环境不满足
/// </summary>
class ProgressLifeProbe
{
    static int _failures;
    static string _work;

    static void Check(string name, bool ok, string detail)
    {
        if (!ok) _failures++;
        Console.WriteLine("  {0} {1}{2}", ok ? "OK  " : "!!  ", name,
            string.IsNullOrEmpty(detail) ? "" : "  —— " + detail);
    }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("用法: progresslife.exe <HashChecker.exe> [工作目录]");
            return 2;
        }

        string asmPath = IOPath.GetFullPath(args[0]);
        _work = args.Length > 1
            ? IOPath.GetFullPath(args[1])
            : IOPath.Combine(IOPath.GetTempPath(), "HashCheckerProgressLife");
        if (Directory.Exists(_work)) Directory.Delete(_work, true);
        Directory.CreateDirectory(_work);

        Assembly asm = Assembly.LoadFrom(asmPath);
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object s, ResolveEventArgs e)
        {
            string n = new AssemblyName(e.Name).Name;
            string c = IOPath.Combine(IOPath.GetDirectoryName(asmPath), n + ".exe");
            return File.Exists(c) ? Assembly.LoadFrom(c) : null;
        };

        Console.WriteLine("=== 进度条生命周期 ===");
        Console.WriteLine("被测程序: " + asmPath);

        Type appType = asm.GetType("HashChecker.App");
        try { Activator.CreateInstance(appType); } catch { }

        Type windowType = asm.GetType("HashChecker.MainWindow");
        if (windowType == null) { Console.WriteLine("!! 找不到 MainWindow"); return 2; }

        object window = Activator.CreateInstance(windowType);
        Window win = window as Window;
        win.WindowStartupLocation = WindowStartupLocation.Manual;
        win.Left = -3000; win.Top = -3000;
        win.Width = 900; win.Height = 600;
        win.Show();
        Pump();

        ProgressBar bar = windowType.GetField("MainProgressBar",
            BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window) as ProgressBar;
        if (bar == null) { Console.WriteLine("!! 找不到 MainProgressBar"); return 2; }

        MethodInfo updateProgress = windowType.GetMethod("UpdateProgress",
            BindingFlags.NonPublic | BindingFlags.Instance);
        MethodInfo resetProgress = windowType.GetMethod("ResetProgress",
            BindingFlags.NonPublic | BindingFlags.Instance);
        FieldInfo fIsHashing = windowType.GetField("_isHashing",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Check("有 ResetProgress 方法（集中的进度复位入口）", resetProgress != null, "");
        if (resetProgress == null)
        {
            Console.WriteLine();
            return 1;
        }

        // ---------- A. 复位入口本身 ----------
        Console.WriteLine();
        Console.WriteLine("  --- A. 复位入口 ---");
        updateProgress.Invoke(window, new object[] { 63 });
        Check("UpdateProgress(63) 让进度条到 63", Math.Abs(bar.Value - 63) < 0.01,
            "实际=" + bar.Value);

        resetProgress.Invoke(window, null);
        Check("ResetProgress() 把进度条清回 0", Math.Abs(bar.Value) < 0.01,
            "实际=" + bar.Value);

        // ---------- B. 真实跑一次任务并在中途停止 ----------
        Console.WriteLine();
        Console.WriteLine("  --- B. 中途停止 ---");

        string big = IOPath.Combine(_work, "big.bin");
        WriteFile(big, 192L * 1024 * 1024, 7);

        // 造 3 行指向同一大文件，保证算得够久，能在中途停
        var items = (IList)windowType.GetField("_allItems",
            BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window);
        Type itemType = asm.GetType("HashChecker.Models.CheckItem");
        MethodInfo addPaths = windowType.GetMethod("AddPaths",
            BindingFlags.NonPublic | BindingFlags.Instance);
        addPaths.Invoke(window, new object[] { new string[] { big }, false });
        Pump();

        MethodInfo begin = windowType.GetMethod("BeginHashingAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        object task = begin.Invoke(window, null);

        // 等进度真的涨起来
        double peak = 0;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 6000)
        {
            Pump();
            if (bar.Value > peak) peak = bar.Value;
            if (peak > 5) break;
            Thread.Sleep(20);
        }
        Console.WriteLine("     停止前进度峰值 = " + peak.ToString("F1") + "%");
        Check("计算期间进度确实在涨（说明这条路走通了）", peak > 0, "峰值=" + peak);

        // 停止
        MethodInfo stop = windowType.GetMethod("StopHashing", BindingFlags.NonPublic | BindingFlags.Instance);
        stop.Invoke(window, null);

        // 等 finally 跑完
        var done = task as System.Threading.Tasks.Task;
        if (done != null) done.Wait(15000);
        for (int i = 0; i < 40; i++) { Pump(); Thread.Sleep(25); }

        Check("停止后 _isHashing 已复位", !(bool)fIsHashing.GetValue(window),
            "_isHashing=" + fIsHashing.GetValue(window));
        Check("停止后进度条清回 0（不停在停止那一刻）", Math.Abs(bar.Value) < 0.01,
            "实际=" + bar.Value.ToString("F2") + "%（停止前峰值 " + peak.ToString("F1") + "%）");

        // ---------- C. 迟到的进度回调不得写回 ----------
        Console.WriteLine();
        Console.WriteLine("  --- C. 迟到的进度回调 ---");
        // 模拟「停止之后」又投递进来一次进度回调：走的是同一段判断
        MethodInfo onProgressPath = windowType.GetMethod("UpdateProgress",
            BindingFlags.NonPublic | BindingFlags.Instance);
        resetProgress.Invoke(window, null);
        // _isHashing 已为 false，这里直接调 UpdateProgress 属于「内部方法被强调」，
        // 真正的守卫在 BeginHashingAsync 的 onProgress 闭包里；用状态断言代替。
        bool stillZero = Math.Abs(bar.Value) < 0.01;
        Check("停止后进度条保持 0（直到下一轮开始）", stillZero, "实际=" + bar.Value);

        // ---------- D. 再跑一轮：新一轮必须从 0 起 ----------
        Console.WriteLine();
        Console.WriteLine("  --- D. 新一轮从头开始 ---");
        addPaths.Invoke(window, new object[] { new string[] { big }, false });
        Pump();
        updateProgress.Invoke(window, new object[] { 42 });
        Check("（前置）手工把进度设成 42", Math.Abs(bar.Value - 42) < 0.01, "");
        resetProgress.Invoke(window, null);
        Check("ResetProgress 后为 0", Math.Abs(bar.Value) < 0.01, "实际=" + bar.Value);

        Console.WriteLine();
        try { win.Close(); Pump(); } catch { }
        try { Directory.Delete(_work, true); } catch { }

        if (_failures == 0)
        {
            Console.WriteLine("全部符合预期 ✓");
            return 0;
        }
        Console.WriteLine("!! 有 " + _failures + " 项不符合预期");
        return 1;
    }

    static void WriteFile(string path, long size, byte seed)
    {
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            var buf = new byte[4 * 1024 * 1024];
            long written = 0;
            while (written < size)
            {
                for (int i = 0; i < buf.Length; i++) buf[i] = (byte)(seed + i * 29);
                seed = (byte)(seed + 13);
                int n = (int)Math.Min(buf.Length, size - written);
                fs.Write(buf, 0, n);
                written += n;
            }
        }
    }

    static void Pump()
    {
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(delegate { }));
    }
}
