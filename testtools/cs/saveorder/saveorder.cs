using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using IOPath = System.IO.Path;

/// <summary>
/// 第二次验证新增覆盖维度 D16：记录写出的**行序契约**。
///
/// 为什么此前从未被测过
/// --------------------
/// `BuildChecksumLines` 用 `items.OrderBy(x =&gt; x.Sequence)` 排序后写出，
/// `Sequence` 由 `CheckItem` 构造时从静态计数器取号，**只读**。
/// 现有全部探针里，`suffixprobe` / `saveprobe` / `writeprobe` 关心的都是
/// 「后缀对不对」「BOM 有没有」「转义对不对」，
/// **没有任何一项检查行的先后顺序**。
/// 也就是说：把 `OrderBy` 删掉，现有测试**全都不会发现** ——
/// 因为正常界面操作里「加入顺序」恰好等于「Sequence 顺序」，两种实现看不出差别。
///
/// 本探针制造**解耦**：先按 A、B、C 的次序构造三行（于是 Sequence 是 A&lt;B&lt;C），
/// 再按 **C、A、B** 的次序加入列表（于是集合顺序 ≠ Sequence 顺序）。
/// 此时两种实现给出不同答案：
///   * 有 OrderBy  -&gt; A, B, C
///   * 无 OrderBy  -&gt; C, A, B
/// 因此这一项对「删掉排序」是**有变异保护**的。
///
/// 用法: saveorder.exe &lt;HashChecker.exe&gt; [工作目录]
/// 退出码: 0 = 全部通过, 1 = 有失败项, 2 = 环境不满足
/// </summary>
class SaveOrderProbe
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
        RedirectSettingsToTemp();
        if (args.Length < 1)
        {
            Console.WriteLine("用法: saveorder.exe <HashChecker.exe> [工作目录]");
            return 2;
        }

        string asmPath = IOPath.GetFullPath(args[0]);
        _work = args.Length > 1
            ? IOPath.GetFullPath(args[1])
            : IOPath.Combine(IOPath.GetTempPath(), "HashCheckerSaveOrder");
        if (Directory.Exists(_work)) Directory.Delete(_work, true);
        Directory.CreateDirectory(_work);

        Assembly asm = Assembly.LoadFrom(asmPath);
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object s, ResolveEventArgs e)
        {
            string n = new AssemblyName(e.Name).Name;
            string c = IOPath.Combine(IOPath.GetDirectoryName(asmPath), n + ".exe");
            return File.Exists(c) ? Assembly.LoadFrom(c) : null;
        };

        Console.WriteLine("=== 记录写出的行序契约（D16）===");
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

        FieldInfo fAll = windowType.GetField("_allItems", BindingFlags.NonPublic | BindingFlags.Instance);
        if (fAll == null) { Console.WriteLine("!! 找不到 _allItems"); return 2; }
        IList items = fAll.GetValue(window) as IList;

        Type itemType = asm.GetType("HashChecker.Models.CheckItem");
        if (itemType == null) { Console.WriteLine("!! 找不到 CheckItem"); return 2; }

        MethodInfo build = windowType.GetMethod("BuildChecksumLines",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (build == null) { Console.WriteLine("!! 找不到 BuildChecksumLines"); return 2; }

        PropertyInfo pSeq = itemType.GetProperty("Sequence");
        PropertyInfo pCurrentHash = itemType.GetProperty("CurrentHash");
        if (pSeq == null || pCurrentHash == null)
        {
            Console.WriteLine("!! CheckItem 缺少 Sequence / CurrentHash");
            return 2;
        }

        string[] names = { "alpha.bin", "beta.bin", "gamma.bin", "delta.bin", "epsilon.bin", "zeta.bin" };
        foreach (string n in names) WriteFile(IOPath.Combine(_work, n), 4096, 3);

        // =====================================================================
        // A. 解耦：构造顺序 alpha,beta,gamma（Sequence 递增）
        //    但加入顺序 gamma,alpha,beta（集合顺序 != Sequence 顺序）
        // =====================================================================
        Console.WriteLine();
        Console.WriteLine("  --- A. 集合顺序与 Sequence 顺序解耦 ---");
        try
        {
            var a1 = MakeItem(itemType, "alpha.bin", "aa1");
            var a2 = MakeItem(itemType, "beta.bin", "bb2");
            var a3 = MakeItem(itemType, "gamma.bin", "cc3");

            Console.WriteLine("     Sequence: alpha=" + pSeq.GetValue(a1, null)
                + " beta=" + pSeq.GetValue(a2, null) + " gamma=" + pSeq.GetValue(a3, null));

            items.Add(a3);   // gamma 先入
            items.Add(a1);   // alpha 次之
            items.Add(a2);   // beta 最后
            Pump();

            string[] got = ExtractNames(items, build, window);
            Console.WriteLine("     集合顺序: gamma, alpha, beta");
            Console.WriteLine("     写出顺序: " + string.Join(", ", got));

            Check("写出顺序按 Sequence 升序（alpha, beta, gamma）",
                got.Length == 3 && got[0] == "alpha.bin" && got[1] == "beta.bin" && got[2] == "gamma.bin",
                "实际=" + string.Join(",", got));
            Check("写出顺序**不等于**集合顺序（证明排序真的发生了）",
                !(got.Length == 3 && got[0] == "gamma.bin" && got[1] == "alpha.bin"),
                "集合顺序=gamma,alpha,beta");
        }
        catch (Exception ex)
        {
            Check("A 段执行不抛异常", false, Describe(ex));
        }
        ResetItems(items, window);

        // =====================================================================
        // B. 换一组、换一种打乱方式，重复验证（防止偶然）
        // =====================================================================
        Console.WriteLine();
        Console.WriteLine("  --- B. 第二组：构造 delta,epsilon,zeta，加入 zeta,delta,epsilon ---");
        try
        {
            var b1 = MakeItem(itemType, "delta.bin", "dd4");
            var b2 = MakeItem(itemType, "epsilon.bin", "ee5");
            var b3 = MakeItem(itemType, "zeta.bin", "ff6");

            Console.WriteLine("     Sequence: delta=" + pSeq.GetValue(b1, null)
                + " epsilon=" + pSeq.GetValue(b2, null) + " zeta=" + pSeq.GetValue(b3, null));

            items.Add(b3);
            items.Add(b1);
            items.Add(b2);
            Pump();

            string[] got = ExtractNames(items, build, window);
            Console.WriteLine("     集合顺序: zeta, delta, epsilon");
            Console.WriteLine("     写出顺序: " + string.Join(", ", got));

            Check("第二组写出顺序仍是 delta, epsilon, zeta",
                got.Length == 3 && got[0] == "delta.bin" && got[1] == "epsilon.bin" && got[2] == "zeta.bin",
                "实际=" + string.Join(",", got));
        }
        catch (Exception ex)
        {
            Check("B 段执行不抛异常", false, Describe(ex));
        }
        ResetItems(items, window);

        // =====================================================================
        // C. 没有当前值的行不得写出
        // =====================================================================
        Console.WriteLine();
        Console.WriteLine("  --- C. 没有当前值的行不得写出 ---");
        try
        {
            var c1 = MakeItem(itemType, "alpha.bin", "aa7");
            var c2 = MakeItem(itemType, "beta.bin", null);      // 无当前值
            var c3 = MakeItem(itemType, "gamma.bin", "cc8");
            items.Add(c1); items.Add(c2); items.Add(c3);
            Pump();

            string[] got = ExtractNames(items, build, window);
            Console.WriteLine("     写出顺序: " + string.Join(", ", got));

            Check("清空 CurrentHash 的那一行不写出",
                got.Length == 2 && Array.IndexOf(got, "beta.bin") < 0,
                "实际=" + string.Join(",", got));
            Check("剩下两行仍按 Sequence 升序（alpha 然后 gamma）",
                got.Length == 2 && got[0] == "alpha.bin" && got[1] == "gamma.bin",
                "实际=" + string.Join(",", got));
        }
        catch (Exception ex)
        {
            Check("C 段执行不抛异常", false, Describe(ex));
        }
        ResetItems(items, window);

        // =====================================================================
        // D. 空列表
        // =====================================================================
        Console.WriteLine();
        Console.WriteLine("  --- D. 空列表不抛异常 ---");
        try
        {
            // 必须是 List<CheckItem>：BuildChecksumLines 的形参是 IEnumerable<CheckItem>，
            // 传 List<object> 会在反射绑定阶段就抛 ArgumentException（那是探针的错，不是产品的错）。
            Type typedList = typeof(List<>).MakeGenericType(itemType);
            object empty = build.Invoke(window, new object[] { Activator.CreateInstance(typedList) });
            int count = ((IList)empty).Count;
            Check("空列表返回 0 行、不抛异常", count == 0, "行数=" + count);
        }
        catch (Exception ex)
        {
            Check("空列表返回 0 行、不抛异常", false, Describe(ex));
        }

        // =====================================================================
        // E. 只写基名，不写完整路径
        // =====================================================================
        Console.WriteLine();
        Console.WriteLine("  --- E. 只写基名 ---");
        try
        {
            var e1 = MakeItem(itemType, "alpha.bin", "aa9");
            items.Add(e1);
            Pump();
            object lines = build.Invoke(window, new object[] { items });
            string line = Convert.ToString(((IList)lines)[0]);
            Console.WriteLine("     写出的一行: " + line);
            Check("行内只有基名、没有目录分隔符", line.IndexOf(_work) < 0, "行=" + line);
            Check("行格式是「hash + 空格 + * + 基名」", line.IndexOf(" *alpha.bin") > 0, "行=" + line);
        }
        catch (Exception ex)
        {
            Check("E 段执行不抛异常", false, Describe(ex));
        }
        ResetItems(items, window);

        try { win.Close(); } catch { }
        Pump();

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "全部符合预期 ✓" : ("失败 " + _failures + " 项"));
        return _failures == 0 ? 0 : 1;
    }

    static object MakeItem(Type itemType, string fileName, string currentHash)
    {
        string full = IOPath.Combine(_work, fileName);
        object item = Activator.CreateInstance(itemType, new object[] { full });
        if (currentHash != null)
        {
            itemType.GetProperty("CurrentHash").SetValue(item, currentHash, null);
        }
        return item;
    }

    static void ResetItems(IList items, object window)
    {
        items.Clear();
        Pump();
    }

    static string[] ExtractNames(IList items, MethodInfo build, object window)
    {
        object lines = build.Invoke(window, new object[] { items });
        var list = new List<string>();
        foreach (object line in (IList)lines)
        {
            string s = Convert.ToString(line);
            int idx = s.IndexOf(" *");
            if (idx >= 0) { list.Add(s.Substring(idx + 2).Trim()); continue; }
            string[] parts = s.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            list.Add(parts.Length > 1 ? parts[parts.Length - 1] : s);
        }
        return list.ToArray();
    }

    static void WriteFile(string path, int bytes, int seed)
    {
        byte[] buf = new byte[bytes];
        for (int i = 0; i < bytes; i++) buf[i] = (byte)((i * 31 + seed) & 0xFF);
        File.WriteAllBytes(path, buf);
    }

    static void Pump()
    {
        DispatcherFrame frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(delegate { frame.Continue = false; }));
        Dispatcher.PushFrame(frame);
    }

    static string Describe(Exception ex)
    {
        while ((ex is TargetInvocationException || ex is System.Reflection.TargetException)
               && ex.InnerException != null) ex = ex.InnerException;
        try { return ex.GetType().Name + ": " + ex.Message; }
        catch { return ex.GetType().Name; }
    }

    /// <summary>把产品配置改道到 %TEMP%，不碰真实用户配置。</summary>
    static void RedirectSettingsToTemp()
    {
        string name = IOPath.GetFileNameWithoutExtension(Environment.GetCommandLineArgs()[0]);
        string dir = IOPath.Combine(IOPath.GetTempPath(), "HashChecker", "settings-" + name);
        try { Directory.CreateDirectory(dir); } catch { }
        Environment.SetEnvironmentVariable("HASHCHECKER_SETTINGS_DIR", dir);
    }
}
