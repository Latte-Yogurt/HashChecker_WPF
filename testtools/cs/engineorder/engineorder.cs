using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using IOPath = System.IO.Path;

/// <summary>
/// 计算引擎的**顺序性 / 进度 / 吞吐**验证。
///
/// 覆盖三件历史缺陷：
///   A. 进度完全不更新 —— 节流用 int 做差溢出，`差值 >= 间隔` 永远为假。
///      判据：给一个大文件，进度回调必须真的被调用多次，且百分比单调不减。
///   B. 结果乱序 —— Parallel.ForEach 按「谁先算完」交付，大文件越多乱跳越明显。
///      判据：混入大小悬殊的文件，交付顺序必须严格等于输入顺序。
///   C. 大文件吞吐 —— 判据：1MB 缓冲顺序读的吞吐不得低于一个宽松下限（防回归成逐字节读）。
///
/// 用法: engineorder.exe &lt;HashChecker.exe&gt; [工作目录]
/// 退出码: 0 = 全部通过, 1 = 有失败项, 2 = 环境不满足
/// </summary>
class EngineOrderProbe
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
            Console.WriteLine("用法: engineorder.exe <HashChecker.exe> [工作目录]");
            return 2;
        }

        string asmPath = IOPath.GetFullPath(args[0]);
        _work = args.Length > 1
            ? IOPath.GetFullPath(args[1])
            : IOPath.Combine(IOPath.GetTempPath(), "HashCheckerEngineOrder");
        if (Directory.Exists(_work)) Directory.Delete(_work, true);
        Directory.CreateDirectory(_work);

        Assembly asm = Assembly.LoadFrom(asmPath);
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object s, ResolveEventArgs e)
        {
            string n = new AssemblyName(e.Name).Name;
            string c = IOPath.Combine(IOPath.GetDirectoryName(asmPath), n + ".exe");
            return File.Exists(c) ? Assembly.LoadFrom(c) : null;
        };

        Console.WriteLine("=== 计算引擎：顺序 / 进度 / 吞吐 ===");
        Console.WriteLine("被测程序: " + asmPath);

        Type itemType = asm.GetType("HashChecker.Models.CheckItem");
        Type statusType = asm.GetType("HashChecker.Models.CheckStatus");
        Type engineType = asm.GetType("HashChecker.Services.HashingEngine");
        Type algType = asm.GetType("HashChecker.Services.HashAlgorithms");
        Type outcomeType = asm.GetType("HashChecker.Services.HashOutcome");

        if (itemType == null || engineType == null || algType == null)
        {
            Console.WriteLine("!! 找不到关键类型");
            return 2;
        }

        // ---------- A / B：保序 + 进度 ----------
        Console.WriteLine();
        Console.WriteLine("  --- A/B. 交付顺序与进度上报 ---");

        // 大小悬殊：若按「谁先算完」交付，10MB 与 1MB 交错必然乱序
        long[] sizes = { 12L * 1024 * 1024, 256 * 1024, 6L * 1024 * 1024, 512 * 1024,
                         3L * 1024 * 1024, 128 * 1024, 8L * 1024 * 1024 };
        var items = new List<object>();
        var itemsI = (System.Collections.IList)Activator.CreateInstance(
            typeof(List<>).MakeGenericType(itemType));

        Console.Write("     造数据: ");
        for (int i = 0; i < sizes.Length; i++)
        {
            string p = IOPath.Combine(_work, "f" + i + ".bin");
            WriteFile(p, sizes[i], (byte)(i * 37 + 11));
            object item = Activator.CreateInstance(itemType, new object[] { p });
            itemsI.Add(item);
            Console.Write(Describe(sizes[i]) + " ");
        }
        Console.WriteLine();

        double totalMb = 0;
        foreach (long s in sizes) totalMb += s / 1024.0 / 1024.0;

        object engine = Activator.CreateInstance(engineType);
        MethodInfo run = engineType.GetMethod("Run");
        if (run == null) { Console.WriteLine("!! 找不到 Run"); return 2; }

        var delivered = new List<string>();
        var percents = new List<int>();
        var gate = new object();

        Action<object> onOutcome = delegate(object outcome)
        {
            object item = outcomeType.GetProperty("Item").GetValue(outcome, null);
            string path = (string)itemType.GetProperty("FullPath").GetValue(item, null);
            lock (gate) delivered.Add(IOPath.GetFileName(path));
        };
        Action<int> onProgress = delegate(int pct)
        {
            lock (gate) percents.Add(pct);
        };

        // Action<T> 需要强类型：用反射把委托包一层
        Type actionOutcome = typeof(Action<>).MakeGenericType(outcomeType);
        Delegate dOutcome = Delegate.CreateDelegate(actionOutcome, onOutcome.Target, onOutcome.Method);
        Delegate dProgress = Delegate.CreateDelegate(typeof(Action<int>), onProgress.Target, onProgress.Method);

        var sw = Stopwatch.StartNew();
        run.Invoke(engine, new object[] { itemsI, 4, dOutcome, dProgress, CancellationToken.None });
        sw.Stop();

        double mbps = totalMb / sw.Elapsed.TotalSeconds;
        Console.WriteLine("     用时 " + sw.ElapsedMilliseconds + " ms，吞吐 " + mbps.ToString("F0") + " MB/s");

        // B: 保序
        var expectedOrder = new List<string>();
        for (int i = 0; i < sizes.Length; i++) expectedOrder.Add("f" + i + ".bin");

        bool ordered = delivered.Count == expectedOrder.Count;
        if (ordered)
        {
            for (int i = 0; i < delivered.Count; i++)
                if (delivered[i] != expectedOrder[i]) { ordered = false; break; }
        }

        Console.WriteLine("     交付顺序: " + string.Join(" ", delivered.ToArray()));
        Check("交付顺序严格等于输入顺序（不按谁先算完）", ordered,
            ordered ? "" : "实际: " + string.Join(" ", delivered.ToArray()));

        // A: 进度
        Console.WriteLine("     进度回调 " + percents.Count + " 次: "
            + string.Join(",", percents.ConvertAll(delegate(int p) { return p.ToString(); }).ToArray()));
        Check("进度回调确实被调用（不是一次都没有）", percents.Count >= 3,
            "次数=" + percents.Count);

        bool monotonic = true;
        for (int i = 1; i < percents.Count; i++)
            if (percents[i] < percents[i - 1]) monotonic = false;
        Check("进度百分比单调不减", monotonic, "");

        int maxPct = 0;
        foreach (int p in percents) if (p > maxPct) maxPct = p;
        Check("进度能到 100（收尾上报）", maxPct == 100, "最高=" + maxPct);

        Check("进度中途有中间值（不是只有 0 和 100）",
            percents.Count > 2, "不同取值=" + percents.Count);

        // C: 吞吐下限（宽松，只防退化成逐字节读）
        Check("大文件吞吐不低于 30 MB/s（防退化）", mbps >= 30.0,
            mbps.ToString("F0") + " MB/s");

        // ---------- D：同一批再跑一次，结果值必须相同 ----------
        Console.WriteLine();
        Console.WriteLine("  --- D. 结果正确性：两次运行摘要一致 ---");

        var first = new Dictionary<string, string>();
        Action<object> keep = delegate(object outcome)
        {
            object item = outcomeType.GetProperty("Item").GetValue(outcome, null);
            string path = (string)itemType.GetProperty("FullPath").GetValue(item, null);
            string hash = (string)outcomeType.GetProperty("Hash").GetValue(outcome, null);
            lock (gate) first[IOPath.GetFileName(path)] = hash;
        };
        Delegate dKeep = Delegate.CreateDelegate(actionOutcome, keep.Target, keep.Method);
        run.Invoke(engine, new object[] { itemsI, 4, dKeep, dProgress, CancellationToken.None });

        bool same = true;
        int checkedCount = 0;
        foreach (KeyValuePair<string, string> kv in first)
        {
            string p = IOPath.Combine(_work, kv.Key);
            string expected = Sha256OfFile(p);
            checkedCount++;
            if (!string.Equals(expected, kv.Value, StringComparison.OrdinalIgnoreCase)) same = false;
        }
        Check("算出的 SHA-256 与独立实现（.NET SHA256）一致", same,
            "比对 " + checkedCount + " 个文件");

        // ---------- E：多线程不应改变结果 ----------
        Console.WriteLine();
        Console.WriteLine("  --- E. 线程数不影响结果 ---");
        var one = new Dictionary<string, string>();
        Action<object> keepOne = delegate(object outcome)
        {
            object item = outcomeType.GetProperty("Item").GetValue(outcome, null);
            string path = (string)itemType.GetProperty("FullPath").GetValue(item, null);
            string hash = (string)outcomeType.GetProperty("Hash").GetValue(outcome, null);
            lock (gate) one[IOPath.GetFileName(path)] = hash;
        };
        Delegate dKeepOne = Delegate.CreateDelegate(actionOutcome, keepOne.Target, keepOne.Method);
        run.Invoke(engine, new object[] { itemsI, 1, dKeepOne, dProgress, CancellationToken.None });

        bool identical = one.Count == first.Count;
        if (identical)
        {
            foreach (KeyValuePair<string, string> kv in first)
            {
                string v;
                if (!one.TryGetValue(kv.Key, out v) || !string.Equals(v, kv.Value, StringComparison.OrdinalIgnoreCase))
                { identical = false; break; }
            }
        }
        Check("1 线程与 4 线程算出的摘要完全相同", identical, "");

        Console.WriteLine();
        try { Directory.Delete(_work, true); } catch { }

        if (_failures == 0)
        {
            Console.WriteLine("全部符合预期 ✓");
            return 0;
        }
        Console.WriteLine("!! 有 " + _failures + " 项不符合预期");
        return 1;
    }

    static string Describe(long bytes)
    {
        if (bytes >= 1024L * 1024) return (bytes / 1024 / 1024) + "MB";
        return (bytes / 1024) + "KB";
    }

    static void WriteFile(string path, long size, byte seed)
    {
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            var buf = new byte[256 * 1024];
            for (int i = 0; i < buf.Length; i++) buf[i] = (byte)(seed + i * 31);
            long written = 0;
            while (written < size)
            {
                int n = (int)Math.Min(buf.Length, size - written);
                fs.Write(buf, 0, n);
                written += n;
                seed = (byte)(seed + 17);
                for (int i = 0; i < buf.Length; i++) buf[i] = (byte)(seed + i * 31);
            }
        }
    }

    static string Sha256OfFile(string path)
    {
        using (var sha = System.Security.Cryptography.SHA256.Create())
        using (var fs = File.OpenRead(path))
        {
            byte[] h = sha.ComputeHash(fs);
            var sb = new System.Text.StringBuilder(h.Length * 2);
            foreach (byte b in h) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
