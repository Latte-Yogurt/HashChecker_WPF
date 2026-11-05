using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

/// <summary>
/// 计算引擎的并发与边界（V04 维度）：线程数越界、取消、被独占占用的文件、空任务、
/// 纯目录任务、一行多算法。
///
/// 守的是 H5（资源释放）、H6（并发与重入）、H14（占用文件、0 与负数线程数）：
/// 引擎跑在后台线程池上，一次任务里出问题会表现为「进度不到 100%」
/// 「整批被一个坏文件拖死」「取消之后还在跑」。
///
/// 注意职责边界：引擎只负责算出结果并放进 HashOutcome，
/// 把结果写回 CheckItem 是窗口的事（MainWindow.ApplyOutcome）。
/// 所以这里断言的是 HashOutcome 的 Error / Digests，不是 CheckItem 的同名属性。
///
/// 用法: engineprobe.exe &lt;被测 exe 路径&gt; [工作目录]
/// 退出码: 0 = 全部符合预期, 1 = 有失败项, 2 = 用法错误
/// </summary>
class EngineProbe
{
    static int _failures;
    static string _work;
    static Type _engineType;
    static Type _outcomeType;

    static void Check(string name, bool ok, string detail)
    {
        if (!ok) _failures++;
        Console.WriteLine("  {0} {1}{2}", ok ? "OK  " : "!!  ", name,
            string.IsNullOrEmpty(detail) ? "" : "  —— " + detail);
    }

    sealed class Counter<T>
    {
        public int Count;
        public T Last;
        public readonly List<T> All = new List<T>();
        public void Callback(T value) { Count++; Last = value; All.Add(value); }
    }

    static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("用法: engineprobe.exe <被测 exe 路径> [工作目录]");
            return 2;
        }

        string asmPath = Path.GetFullPath(args[0]);
        _work = args.Length > 1
            ? Path.GetFullPath(args[1])
            : Path.Combine(Path.GetTempPath(), "HashCheckerEngineProbe");

        if (Directory.Exists(_work)) Directory.Delete(_work, true);
        Directory.CreateDirectory(_work);

        Assembly asm = Assembly.LoadFrom(asmPath);
        _engineType = asm.GetType("HashChecker.Services.HashingEngine");
        _outcomeType = asm.GetType("HashChecker.Services.HashOutcome");
        Type itemType = asm.GetType("HashChecker.Models.CheckItem");
        if (_engineType == null || _outcomeType == null || itemType == null)
        {
            Console.WriteLine("!! 找不到 HashingEngine / HashOutcome / CheckItem");
            return 1;
        }

        Console.WriteLine("=== 计算引擎并发与边界验证 ===");
        Console.WriteLine("");

        try
        {
            RunAll(asm, itemType);
        }
        catch (Exception ex)
        {
            Console.WriteLine("!! 探针异常: " + Describe(ex));
            _failures++;
        }

        Console.WriteLine("");
        if (_failures == 0)
        {
            Console.WriteLine("全部符合预期 ✓");
            return 0;
        }
        Console.WriteLine("失败 " + _failures + " 项");
        return 1;
    }

    static void RunAll(Assembly asm, Type itemType)
    {
        // ---------------------------------------------------------------- 百分比换算
        MethodInfo toPercent = _engineType.GetMethod("ToPercent",
            BindingFlags.Public | BindingFlags.Static);
        if (toPercent == null)
        {
            Check("存在 ToPercent", false, "找不到 ToPercent");
        }
        else
        {
            // (done, total, 期望值, 说明)。total <= 0 时约定返回 100：
            // 没有可处理的字节就等于「已经做完」，这样进度条不会卡在 0。
            object[][] cases =
            {
                new object[] { 0L, 0L, 100, "总数为 0 → 视为已完成" },
                new object[] { 5L, 0L, 100, "总数为 0 → 视为已完成" },
                new object[] { 11L, 10L, 100, "超出总数时封顶 100" },
                new object[] { -5L, 10L, 0, "负数进度夹到 0" },
                new object[] { 0L, 10L, 0, "起点是 0" },
                new object[] { 10L, 10L, 100, "完成是 100" },
            };
            foreach (object[] c in cases)
            {
                try
                {
                    int v = (int)toPercent.Invoke(null, new object[] { (long)c[0], (long)c[1] });
                    Check(string.Format("ToPercent({0}, {1}) == {2}（{3}）",
                        c[0], c[1], c[2], c[3]), v == (int)c[2], "实际=" + v);
                }
                catch (Exception ex)
                {
                    Check(string.Format("ToPercent({0}, {1}) 不抛异常", c[0], c[1]),
                        false, Describe(ex));
                }
            }
        }

        // ---------------------------------------------------------------- 素材
        string small = Path.Combine(_work, "small.bin");
        File.WriteAllBytes(small, Encoding.UTF8.GetBytes("hello hashchecker"));
        string big = Path.Combine(_work, "big.bin");
        var buffer = new byte[3 * 1024 * 1024];
        new Random(7).NextBytes(buffer);
        File.WriteAllBytes(big, buffer);
        string folder = Path.Combine(_work, "folder");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a.bin"), "aaa");
        File.WriteAllText(Path.Combine(folder, "b.bin"), "bbb");

        // ---------------------------------------------------------------- 空任务
        try
        {
            Result r = RunEngine(asm, itemType, new string[0], 4, CancellationToken.None);
            Check("空任务不抛异常、无结果回调", r.Outcomes == 0,
                string.Format("结果 {0} 个、进度回调 {1} 次", r.Outcomes, r.ProgressReports));
        }
        catch (Exception ex)
        {
            Check("空任务不抛异常", false, Describe(ex));
        }

        // ---------------------------------------------------------------- 线程数边界
        foreach (int threads in new[] { 0, -1, 1, 64 })
        {
            try
            {
                Result r = RunEngine(asm, itemType, new[] { small, big }, threads, CancellationToken.None);
                Check(string.Format("线程数={0}：两个文件都算完、进度到 100%%", threads),
                    r.Outcomes == 2 && r.LastPercent == 100 && r.ErrorOutcomes == 0,
                    string.Format("结果 {0} 个、末次进度 {1}%、失败 {2} 个",
                        r.Outcomes, r.LastPercent, r.ErrorOutcomes));
            }
            catch (Exception ex)
            {
                Check(string.Format("线程数={0} 不抛异常", threads), false, Describe(ex));
            }
        }

        // ---------------------------------------------------------------- 取消
        try
        {
            var cts = new CancellationTokenSource();
            cts.Cancel();
            bool canceled;
            try
            {
                RunEngine(asm, itemType, new[] { big, big, big }, 2, cts.Token);
                canceled = false;
            }
            catch (TargetInvocationException ex)
            {
                canceled = ex.InnerException is OperationCanceledException;
                if (!canceled) Console.WriteLine("      取消路径抛了: " + Describe(ex));
            }
            Check("已取消的 token 让 Run 抛 OperationCanceledException", canceled,
                canceled ? "按契约抛出" : "未按契约抛出（取消语义可能被吞）");
        }
        catch (Exception ex)
        {
            Check("取消路径", false, Describe(ex));
        }

        // ---------------------------------------------------------------- 被独占占用的文件
        try
        {
            string locked = Path.Combine(_work, "locked.bin");
            File.WriteAllBytes(locked, Encoding.UTF8.GetBytes("locked content"));

            using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Result r = RunEngine(asm, itemType, new[] { locked, small }, 2, CancellationToken.None);
                Check("被独占占用的文件不打断整批：进度仍到 100%",
                    r.Outcomes == 2 && r.LastPercent == 100,
                    string.Format("结果 {0} 个、末次进度 {1}%", r.Outcomes, r.LastPercent));
                Check("被独占占用的文件：该文件的结果带 Error（而不是静默成功）",
                    r.ErrorOutcomes >= 1,
                    string.Format("带 Error 的结果 {0} 个；另一行成功 {1} 个",
                        r.ErrorOutcomes, r.Outcomes - r.ErrorOutcomes));
            }
        }
        catch (Exception ex)
        {
            Check("被独占占用的文件", false, Describe(ex));
        }

        // ---------------------------------------------------------------- 纯目录任务
        try
        {
            Result r = RunEngine(asm, itemType, new[] { folder }, 2, CancellationToken.None);
            Check("只有目录的任务：不抛异常、进度到 100%、不产生结果",
                r.LastPercent == 100 && r.Outcomes == 0,
                string.Format("结果 {0} 个、末次进度 {1}%", r.Outcomes, r.LastPercent));
        }
        catch (Exception ex)
        {
            Check("只有目录的任务", false, Describe(ex));
        }

        // ---------------------------------------------------------------- 一行多算法
        try
        {
            object item = Activator.CreateInstance(itemType, small);
            // 64 位值对应 SHA-256 / SHA3-256 / BLAKE2s 多个候选，引擎应按
            // RequiredAlgorithms 把这几个都算出来。这里刻意**不设** SelectedAlgorithm：
            // 一旦显式选过算法，RequiredAlgorithms 就只返回那一个（设计如此）。
            itemType.GetProperty("ExpectedHash").SetValue(item,
                "0000000000000000000000000000000000000000000000000000000000000000", null);

            Result r = RunEngineItems(asm, new[] { item }, 1, CancellationToken.None);
            Dictionary<string, string> digests = r.DigestsOf[0];
            Check("一行有多候选时 Digests 覆盖全部候选算法",
                digests != null && digests.Count >= 2,
                digests == null
                    ? "Digests=null"
                    : string.Format("得到 {0} 个：{1}", digests.Count, Keys(digests)));
            Check("每个算法值都非空", digests != null && AllNonEmpty(digests),
                digests == null ? "Digests=null" : "逐项检查通过");
            Check("HashOutcome.Algorithm 与其中一个键一致",
                r.OutcomeAlgorithms.Count > 0 && digests != null
                && digests.ContainsKey(r.OutcomeAlgorithms[0]),
                "Algorithm=" + (r.OutcomeAlgorithms.Count > 0 ? r.OutcomeAlgorithms[0] : "(空)"));
        }
        catch (Exception ex)
        {
            Check("一行多算法", false, Describe(ex));
        }
    }

    static string Keys(Dictionary<string, string> d)
    {
        return string.Join(",", new List<string>(d.Keys).ToArray());
    }

    sealed class Result
    {
        public int Outcomes;
        public int ProgressReports;
        public int LastPercent;
        public int ErrorOutcomes;
        public readonly List<Dictionary<string, string>> DigestsOf = new List<Dictionary<string, string>>();
        public readonly List<string> OutcomeAlgorithms = new List<string>();
    }

    static Result RunEngine(Assembly asm, Type itemType, string[] paths, int threads,
        CancellationToken token)
    {
        var items = new object[paths.Length];
        for (int i = 0; i < paths.Length; i++)
        {
            items[i] = Activator.CreateInstance(itemType, paths[i]);
        }
        return RunEngineItems(asm, items, threads, token);
    }

    static Result RunEngineItems(Assembly asm, object[] items, int threads, CancellationToken token)
    {
        Type itemType = items.Length > 0
            ? items[0].GetType()
            : asm.GetType("HashChecker.Models.CheckItem");

        var listType = typeof(List<>).MakeGenericType(itemType);
        object list = Activator.CreateInstance(listType);
        MethodInfo add = listType.GetMethod("Add");
        foreach (object item in items) add.Invoke(list, new[] { item });

        Type actionOutcome = typeof(Action<>).MakeGenericType(_outcomeType);
        Type counterType = typeof(Counter<>).MakeGenericType(_outcomeType);
        object counter = Activator.CreateInstance(counterType);
        Delegate onOutcome = Delegate.CreateDelegate(actionOutcome, counter,
            counterType.GetMethod("Callback"));

        var progressCounter = new Counter<int>();
        Delegate onProgress = Delegate.CreateDelegate(typeof(Action<int>), progressCounter,
            typeof(Counter<int>).GetMethod("Callback"));

        object engine = Activator.CreateInstance(_engineType, true);

        MethodInfo run = null;
        foreach (MethodInfo m in _engineType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (m.Name == "Run" && m.GetParameters().Length == 5) { run = m; break; }
        }
        if (run == null) throw new MissingMethodException("HashingEngine.Run");

        run.Invoke(engine, new object[] { list, threads, onOutcome, onProgress, token });

        var result = new Result
        {
            Outcomes = (int)counterType.GetField("Count").GetValue(counter),
            ProgressReports = progressCounter.Count,
            LastPercent = progressCounter.Last,
        };

        PropertyInfo errorProp = _outcomeType.GetProperty("Error");
        PropertyInfo digestsProp = _outcomeType.GetProperty("Digests");
        PropertyInfo algoProp = _outcomeType.GetProperty("Algorithm");
        foreach (object outcome in (System.Collections.IEnumerable)counterType.GetField("All").GetValue(counter))
        {
            if (errorProp.GetValue(outcome, null) != null) result.ErrorOutcomes++;
            result.DigestsOf.Add(ToDict(digestsProp.GetValue(outcome, null)));
            object algo = algoProp.GetValue(outcome, null);
            if (algo != null)
            {
                result.OutcomeAlgorithms.Add((string)algo.GetType().GetProperty("Id").GetValue(algo, null));
            }
        }
        return result;
    }

    static Dictionary<string, string> ToDict(object sortedDictionary)
    {
        if (sortedDictionary == null) return null;
        var dict = sortedDictionary as System.Collections.IDictionary;
        if (dict == null) return null;
        var outd = new Dictionary<string, string>();
        foreach (System.Collections.DictionaryEntry e in dict)
        {
            outd[(string)e.Key] = (string)e.Value;
        }
        return outd;
    }

    static bool AllNonEmpty(Dictionary<string, string> d)
    {
        foreach (KeyValuePair<string, string> kv in d)
        {
            if (string.IsNullOrEmpty(kv.Value)) return false;
        }
        return d.Count > 0;
    }

    static string Describe(Exception ex)
    {
        Exception inner = ex is TargetInvocationException ? ex.InnerException : ex;
        return inner.GetType().Name + ": " + inner.Message;
    }
}
