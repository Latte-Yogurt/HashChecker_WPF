using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;

/// <summary>
/// 「同一文件先后载入两份不同算法的校验记录」的验证（对应 bug 2）。
///
/// 用户描述的现象：先拖入 .sha256 校验文件会立刻自动计算比对（这是好设计，要保留），
/// 之后再拖入同一文件的 .sha3-512 校验文件，界面既不自动计算、算法也不更新。
///
/// 根因在 MainWindow.AddRecordTargets：目标文件已在列表里时走的是「只补期望值」的
/// 早退分支，既没有更新 ExpectedAlgorithm，也没有把「更新了几行」报给上层，
/// 上层因此认为「没有载入任何记录」，于是不触发自动计算。
///
/// 本探针在 STA 线程里构造真实的 MainWindow（不显示窗口），反射调用 AddPaths 两次，
/// 然后检查那一行的算法与待计算状态。
///
/// 用法: addrecordprobe.exe &lt;HashChecker.exe&gt; [工作目录]
/// 退出码: 0 = 全部通过, 1 = 有失败项, 2 = 用法错误
/// </summary>
class AddRecordProbe
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
            Console.WriteLine("用法: addrecordprobe.exe <HashChecker.exe> [工作目录]");
            return 2;
        }

        string asmPath = Path.GetFullPath(args[0]);
        _work = args.Length > 1
            ? Path.GetFullPath(args[1])
            : Path.Combine(Path.GetTempPath(), "HashCheckerAddRecordProbe");

        if (Directory.Exists(_work)) Directory.Delete(_work, true);
        Directory.CreateDirectory(_work);

        var asm = Assembly.LoadFrom(asmPath);
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string simple = new AssemblyName(e.Name).Name;
            string candidate = Path.Combine(Path.GetDirectoryName(asmPath), simple + ".exe");
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        };

        Console.WriteLine("=== 连续载入两份记录的验证 ===");
        Console.WriteLine("被测程序: " + asmPath);
        Console.WriteLine("工作目录: " + _work);
        Console.WriteLine("");

        Type mainWindowType = asm.GetType("HashChecker.MainWindow");
        if (mainWindowType == null)
        {
            Console.WriteLine("!! 找不到 HashChecker.MainWindow");
            return 1;
        }

        // WPF 窗口必须在 STA 线程上创建；Application 也必须有，否则资源查找会出问题
        object app = null;
        Type appType = asm.GetType("HashChecker.App");
        if (appType != null)
        {
            try
            {
                app = Activator.CreateInstance(appType);
            }
            catch (Exception e)
            {
                Console.WriteLine("  (无法创建 App: " + e.Message + "，继续)");
            }
        }

        object window;
        try
        {
            window = Activator.CreateInstance(mainWindowType);
        }
        catch (Exception e)
        {
            Console.WriteLine("!! 构造 MainWindow 失败: " + Describe(e));
            return 1;
        }

        try
        {
            RunChecks(window, mainWindowType, asm);
        }
        finally
        {
            TryDispose(window);
        }

        Console.WriteLine("");
        if (_failures == 0)
        {
            Console.WriteLine("全部符合预期 ✓");
            return 0;
        }
        Console.WriteLine("!! 有 " + _failures + " 项不符合预期");
        return 1;
    }

    static void RunChecks(object window, Type windowType, Assembly asm)
    {
        // 准备素材：一个文件 + 两份不同算法的记录
        string target = Path.Combine(_work, "payload.bin");
        Data(target);

        string sha256 = ComputeDigest(asm, "sha256", target);
        string sha3 = ComputeDigest(asm, "sha3-512", target);

        if (sha256 == null || sha3 == null)
        {
            Check("能算出两种算法的值", false, "内核不可用");
            return;
        }

        string record256 = target + ".sha256";
        string record3512 = target + ".sha3-512";
        File.WriteAllText(record256, sha256 + " *payload.bin\r\n", new System.Text.UTF8Encoding(false));
        File.WriteAllText(record3512, sha3 + " *payload.bin\r\n", new System.Text.UTF8Encoding(false));

        MethodInfo addPaths = windowType.GetMethod("AddPaths",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (addPaths == null)
        {
            Check("找到 AddPaths", false, "反射失败");
            return;
        }

        // 第一次：只传 .sha256 记录
        addPaths.Invoke(window, new object[] { new[] { record256 }, false });
        object item = FindItem(window, windowType, target);

        Check("第一次载入后列表里有该文件", item != null, item == null ? "没找到行" : "");
        if (item == null) return;

        string firstAlgo = AlgorithmId(item);
        Check("第一次算法为 sha256", string.Equals(firstAlgo, "sha256", StringComparison.OrdinalIgnoreCase),
            "实际=" + firstAlgo);

        // 第二次：传同一文件的 .sha3-512 记录
        addPaths.Invoke(window, new object[] { new[] { record3512 }, false });

        item = FindItem(window, windowType, target);
        string secondAlgo = AlgorithmId(item);

        Check("第二次算法更新为 sha3-512",
            string.Equals(secondAlgo, "sha3-512", StringComparison.OrdinalIgnoreCase),
            "实际=" + secondAlgo);

        string expected = (string)GetProperty(item, "ExpectedHash");
        Check("第二次保存值换成 sha3-512 的值",
            expected != null && expected.Length == 128, "长度=" + (expected == null ? 0 : expected.Length));

        // 关键：第二次要把「已有结果」清掉，否则不会重算
        string current = (string)GetProperty(item, "CurrentHash");
        Check("第二次清掉了旧的计算结果（会触发重算）", string.IsNullOrEmpty(current),
            current == null ? "(已清空)" : "残留 " + current.Length + " 位");

        // 下拉框要跟随记录算法
        string picker = SelectedPickerAlgorithm(window, windowType);
        Check("下拉框跟随记录切到 SHA3-512",
            string.Equals(picker, "sha3-512", StringComparison.OrdinalIgnoreCase),
            "实际=" + picker);
    }

    /// <summary>取界面上算法下拉框当前选中的算法 id。</summary>
    static string SelectedPickerAlgorithm(object window, Type windowType)
    {
        try
        {
            object picker = GetField(window, windowType, "AlgorithmPicker")
                            ?? GetField(window, windowType, "algorithmPicker");

            if (picker == null)
            {
                // XAML 生成字段是 internal，需要放宽可见性
                FieldInfo f = windowType.GetField("AlgorithmPicker",
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                if (f != null) picker = f.GetValue(window);
            }

            if (picker == null) return "(找不到下拉框)";

            object selected = picker.GetType().GetProperty("SelectedItem").GetValue(picker);
            if (selected == null) return "(未选中)";

            return (string)selected.GetType().GetProperty("Id").GetValue(selected);
        }
        catch (Exception e)
        {
            return "(异常: " + e.Message + ")";
        }
    }

    static object GetField(object instance, Type type, string name)
    {
        FieldInfo field = type.GetField(name,
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
        return field == null ? null : field.GetValue(instance);
    }

    /// <summary>在列表里按路径找行。</summary>
    static object FindItem(object window, Type windowType, string fullPath)
    {
        object items = GetField(window, windowType, "_allItems");
        if (items == null) return null;

        foreach (object item in (System.Collections.IEnumerable)items)
        {
            string path = (string)GetProperty(item, "FullPath");
            if (path != null && path.Equals(fullPath, StringComparison.OrdinalIgnoreCase)) return item;
        }
        return null;
    }

    /// <summary>取一行的算法 id：优先实际算出的算法，其次期望值算法。</summary>
    static string AlgorithmId(object item)
    {
        object computed = GetProperty(item, "ComputedAlgorithm");
        object expected = GetProperty(item, "ExpectedAlgorithm");

        object chosen = computed ?? expected;
        if (chosen == null) return "(null)";

        return (string)GetProperty(chosen, "Id");
    }

    static object GetProperty(object instance, string name)
    {
        if (instance == null) return null;
        PropertyInfo p = instance.GetType().GetProperty(name);
        return p == null ? null : p.GetValue(instance);
    }

    static byte[] Data(string path)
    {
        var data = new byte[3000];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)((i * 59 + 7) & 0xFF);
        File.WriteAllBytes(path, data);
        return data;
    }

    /// <summary>用产品自己的内核算一个值。</summary>
    static string ComputeDigest(Assembly asm, string algorithmId, string path)
    {
        try
        {
            Type algorithms = asm.GetType("HashChecker.Services.HashAlgorithms");
            MethodInfo byId = algorithms.GetMethod("ById", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(string) }, null);
            object info = byId.Invoke(null, new object[] { algorithmId });

            Delegate factory = (Delegate)info.GetType().GetProperty("CreateCore").GetValue(info);
            object core = factory.DynamicInvoke(new object[0]);

            byte[] data = File.ReadAllBytes(path);
            core.GetType().GetMethod("Initialize").Invoke(core, null);
            core.GetType().GetMethod("Append").Invoke(core, new object[] { data, 0, data.Length });
            byte[] digest = (byte[])core.GetType().GetMethod("Finish").Invoke(core, null);

            var sb = new System.Text.StringBuilder(digest.Length * 2);
            foreach (byte b in digest) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
        catch (Exception e)
        {
            Console.WriteLine("!! 计算 " + algorithmId + " 失败: " + Describe(e));
            _failures++;
            return null;
        }
    }

    static void TryDispose(object window)
    {
        try
        {
            MethodInfo close = window.GetType().GetMethod("Close", Type.EmptyTypes);
            if (close != null) close.Invoke(window, null);
        }
        catch
        {
            // 窗口本来就是隐藏的，关不掉也不影响探针结论
        }
    }

    static string Describe(Exception e)
    {
        if (e is TargetInvocationException && e.InnerException != null) return Describe(e.InnerException);
        return e.GetType().Name + ": " + e.Message;
    }
}
