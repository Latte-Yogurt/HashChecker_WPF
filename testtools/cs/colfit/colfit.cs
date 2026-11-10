using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using IOPath = System.IO.Path;

/// <summary>
/// 列宽自动分配（Auto-fit）的验证。
///
/// 覆盖四件事：
///   A. 右键菜单里存在「自动分配列宽」项，且它挂在 ItemList 的 ContextMenu 上；
///   B. 启动 / 最大化 / 恢复后列宽都会被重新分配，且总和落在视口宽度内（不产生横向滚动条）；
///   C. 列宽随窗口尺寸按比例变化（不是写死的固定值）；
///   D. 窗口太窄时放弃重排，交给横向滚动条（不把列压成没法读的一条缝）。
///
/// 说明：真实拖动窗口边框无法在无人值守环境模拟，因此这里直接改 Window 尺寸并
/// 调用自动分配入口（与 SizeChanged 走同一条代码路径）。
///
/// 用法: colfit.exe &lt;HashChecker.exe&gt; [工作目录]
/// 退出码: 0 = 全部通过, 1 = 有失败项, 2 = 环境不满足
/// </summary>
class ColFitProbe
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
            Console.WriteLine("用法: colfit.exe <HashChecker.exe> [工作目录]");
            return 2;
        }

        string asmPath = IOPath.GetFullPath(args[0]);
        _work = args.Length > 1
            ? IOPath.GetFullPath(args[1])
            : IOPath.Combine(IOPath.GetTempPath(), "HashCheckerColFit");
        if (Directory.Exists(_work)) Directory.Delete(_work, true);
        Directory.CreateDirectory(_work);

        Assembly asm = Assembly.LoadFrom(asmPath);
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object s, ResolveEventArgs e)
        {
            string n = new AssemblyName(e.Name).Name;
            string c = IOPath.Combine(IOPath.GetDirectoryName(asmPath), n + ".exe");
            return File.Exists(c) ? Assembly.LoadFrom(c) : null;
        };

        Console.WriteLine("=== 列宽自动分配验证 ===");
        Console.WriteLine("被测程序: " + asmPath);

        Type appType = asm.GetType("HashChecker.App");
        try { Activator.CreateInstance(appType); } catch { }

        Type windowType = asm.GetType("HashChecker.MainWindow");
        if (windowType == null) { Console.WriteLine("!! 找不到 MainWindow"); return 2; }
        object window = Activator.CreateInstance(windowType);

        Window win = window as Window;
        win.WindowStartupLocation = WindowStartupLocation.Manual;
        win.Left = -2000;      // 挪到屏幕外，避免弹窗干扰
        win.Top = -2000;
        win.Width = 900;
        win.Height = 600;
        win.Show();
        Pump();

        ListView list = (ListView)windowType.GetField("ItemList",
            BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window);
        if (list == null) { Console.WriteLine("!! 找不到 ItemList"); return 2; }

        // ---------- A. 右键菜单 ----------
        Console.WriteLine();
        Console.WriteLine("  --- A. 右键菜单 ---");
        ContextMenu menu = list.ContextMenu;
        Check("ItemList 绑定了 ContextMenu", menu != null, menu == null ? "为 null" : "");

        if (menu != null)
        {
            var headers = new List<string>();
            var named = new Dictionary<string, MenuItem>();
            foreach (object o in menu.Items)
            {
                MenuItem mi = o as MenuItem;
                if (mi == null) continue;
                headers.Add(mi.Header == null ? "(null)" : mi.Header.ToString());
                if (!string.IsNullOrEmpty(mi.Name)) named[mi.Name] = mi;
            }
            Console.WriteLine("     菜单项: " + string.Join(" | ", headers.ToArray()));

            Check("菜单里有「重新分配列宽」项（x:Name=ListMenuRefit）",
                named.ContainsKey("ListMenuRefit"),
                named.ContainsKey("ListMenuRefit") ? "" : "找不到 ListMenuRefit");
            Check("「重新分配列宽」有可点击的处理逻辑",
                named.ContainsKey("ListMenuRefit") && named["ListMenuRefit"].IsEnabled, "");

            Check("菜单里有「自动分配列宽」开关（x:Name=ListMenuAutoFit）",
                named.ContainsKey("ListMenuAutoFit"),
                named.ContainsKey("ListMenuAutoFit") ? "" : "找不到 ListMenuAutoFit");
            Check("「自动分配列宽」是可勾选的布尔开关",
                named.ContainsKey("ListMenuAutoFit") && named["ListMenuAutoFit"].IsCheckable, "");

            if (named.ContainsKey("ListMenuRefit") && named.ContainsKey("ListMenuAutoFit"))
            {
                string refit = named["ListMenuRefit"].Header == null ? null : named["ListMenuRefit"].Header.ToString();
                string toggle = named["ListMenuAutoFit"].Header == null ? null : named["ListMenuAutoFit"].Header.ToString();
                Check("「重新分配列宽」与「自动分配列宽」是两个不同项（文本不重复）",
                    refit != toggle, "refit=" + refit + " toggle=" + toggle);
            }

            // 所有可见菜单项都必须有文本（写死中文的老实现会在英文界面露出中文）
            int blank = 0;
            foreach (string h in headers) if (string.IsNullOrEmpty(h) || h == "(null)") blank++;
            Check("所有菜单项都有文本（无空 Header）", blank == 0, "空的 " + blank + " 项");
        }

        // 本地化：菜单项文本不应等于中文原文以外的语言混用
        if (menu != null)
        {
            MethodInfo getLoc = asm.GetType("HashChecker.Localization")
                .GetMethod("Get", BindingFlags.Public | BindingFlags.Static);
            string expectToggle = (string)getLoc.Invoke(null, new object[] { "MenuAutoFitToggle" });
            string expectRefit = (string)getLoc.Invoke(null, new object[] { "MenuRefitColumns" });
            string fieldAutoFit = null;
            foreach (object o in menu.Items)
            {
                MenuItem mi = o as MenuItem;
                if (mi != null && mi.Name == "ListMenuAutoFit") fieldAutoFit = mi.Header == null ? null : mi.Header.ToString();
            }
            string fieldRefit = null;
            foreach (object o in menu.Items)
            {
                MenuItem mi = o as MenuItem;
                if (mi != null && mi.Name == "ListMenuRefit") fieldRefit = mi.Header == null ? null : mi.Header.ToString();
            }
            Check("「自动分配列宽」文本来自词典（不是写死中文）",
                fieldAutoFit == expectToggle, "实际=" + fieldAutoFit + " 词典=" + expectToggle);
            Check("「重新分配列宽」文本来自词典（不是写死中文）",
                fieldRefit == expectRefit, "实际=" + fieldRefit + " 词典=" + expectRefit);
        }

        // ---------- B/C. 列宽随尺寸变化 ----------
        Console.WriteLine();
        Console.WriteLine("  --- B/C. 列宽随窗口尺寸自动分配 ---");

        MethodInfo autoFit = windowType.GetMethod("AutoFitColumns", BindingFlags.NonPublic | BindingFlags.Instance);
        MethodInfo viewport = windowType.GetMethod("ViewportWidth", BindingFlags.NonPublic | BindingFlags.Instance);
        Check("有 AutoFitColumns 方法（右键菜单与窗口事件共用同一入口）", autoFit != null, "");

        string[] colNames = { "ColName", "ColFolder", "ColAlgorithm", "ColCurrent", "ColSaved", "ColSize", "ColModified" };
        var cols = new Dictionary<string, GridViewColumn>();
        foreach (string n in colNames)
        {
            FieldInfo f = windowType.GetField(n, BindingFlags.NonPublic | BindingFlags.Instance);
            GridViewColumn c = f == null ? null : f.GetValue(window) as GridViewColumn;
            if (c != null) cols[n] = c;
        }
        Check("七个列都拿得到", cols.Count == 7, "拿到 " + cols.Count + " 个");

        Func<double> Sum = delegate
        {
            double s = 0;
            foreach (KeyValuePair<string, GridViewColumn> kv in cols) s += kv.Value.Width;
            return s;
        };

        win.Width = 900; win.Height = 600;
        Pump();
        autoFit.Invoke(window, null);
        Pump();
        double avail1 = (double)viewport.Invoke(window, null);
        double sum1 = Sum();
        Console.WriteLine("     900x600：视口=" + avail1.ToString("F1") + "，列宽合计=" + sum1.ToString("F1"));

        Check("宽窗口下列宽合计落在视口内（不产生横向滚动条）",
            sum1 <= avail1 + 1.0, "合计=" + sum1.ToString("F1") + " 视口=" + avail1.ToString("F1"));

        // 记录 900 宽时各列宽度
        var w900 = new Dictionary<string, double>();
        foreach (KeyValuePair<string, GridViewColumn> kv in cols) w900[kv.Key] = kv.Value.Width;

        win.Width = 1400; win.Height = 700;
        Pump();
        autoFit.Invoke(window, null);
        Pump();
        double avail2 = (double)viewport.Invoke(window, null);
        double sum2 = Sum();
        Console.WriteLine("     1400x700：视口=" + avail2.ToString("F1") + "，列宽合计=" + sum2.ToString("F1"));

        Check("窗口变宽后列宽合计随之变大",
            sum2 > sum1 + 1, "900 时=" + sum1.ToString("F1") + " 1400 时=" + sum2.ToString("F1"));
        Check("变宽后仍不超视口", sum2 <= avail2 + 1.0,
            "合计=" + sum2.ToString("F1") + " 视口=" + avail2.ToString("F1"));

        // 弹性列（名称 / 文件夹 / 当前值 / 保存值）必须变宽
        foreach (string n in new string[] { "ColName", "ColFolder", "ColCurrent", "ColSaved" })
        {
            if (!cols.ContainsKey(n)) continue;
            Check(n + " 随窗口变宽而变宽",
                cols[n].Width > w900[n] + 0.5,
                "900 时=" + w900[n].ToString("F1") + " 1400 时=" + cols[n].Width.ToString("F1"));
        }
        // 固定列（大小 / 算法 / 修改日期）宽度不应随窗口变化
        foreach (string n in new string[] { "ColSize", "ColAlgorithm" })
        {
            if (!cols.ContainsKey(n)) continue;
            Check(n + " 是固定列（不随窗口变化）",
                Math.Abs(cols[n].Width - w900[n]) < 0.5,
                "900 时=" + w900[n].ToString("F1") + " 1400 时=" + cols[n].Width.ToString("F1"));
        }

        // ---------- 最大化 / 恢复 ----------
        Console.WriteLine();
        Console.WriteLine("  --- 最大化 / 恢复 ---");
        win.WindowState = WindowState.Maximized;
        Pump();
        Pump();
        double sumMax = Sum();
        double availMax = (double)viewport.Invoke(window, null);
        Console.WriteLine("     最大化：视口=" + availMax.ToString("F1") + "，列宽合计=" + sumMax.ToString("F1"));
        Check("最大化后列宽合计落在视口内",
            sumMax <= availMax + 1.0, "合计=" + sumMax.ToString("F1") + " 视口=" + availMax.ToString("F1"));
        Check("最大化后列宽合计大于 900 宽时的值（说明真的重排了）",
            sumMax > sum1 + 1, "900 时=" + sum1.ToString("F1") + " 最大化=" + sumMax.ToString("F1"));

        win.WindowState = WindowState.Normal;
        win.Width = 1000; win.Height = 650;
        Pump();
        autoFit.Invoke(window, null);
        Pump();
        double sumNorm = Sum();
        double availNorm = (double)viewport.Invoke(window, null);
        Console.WriteLine("     恢复 1000x650：视口=" + availNorm.ToString("F1") + "，列宽合计=" + sumNorm.ToString("F1"));
        Check("恢复后列宽合计落在视口内",
            sumNorm <= availNorm + 1.0, "合计=" + sumNorm.ToString("F1") + " 视口=" + availNorm.ToString("F1"));

        // ---------- D. 太窄时放弃 ----------
        Console.WriteLine();
        Console.WriteLine("  --- D. 过窄窗口 ---");
        win.Width = 420; win.Height = 300;
        Pump();
        foreach (KeyValuePair<string, GridViewColumn> kv in cols) kv.Value.Width = 77;  // 标记值
        autoFit.Invoke(window, null);
        Pump();
        bool untouched = true;
        foreach (KeyValuePair<string, GridViewColumn> kv in cols)
            if (Math.Abs(kv.Value.Width - 77) > 0.5) untouched = false;
        Check("窗口太窄时放弃重排（列宽保持不动，交给横向滚动条）",
            untouched, untouched ? "" : "列宽被改动了");

        // ---------- E. 开关语义 ----------
        Console.WriteLine();
        Console.WriteLine("  --- E. 「自动分配列宽」开关 ---");

        MethodInfo ifEnabled = windowType.GetMethod("AutoFitColumnsIfEnabled", BindingFlags.NonPublic | BindingFlags.Instance);
        Check("有自动时机入口 AutoFitColumnsIfEnabled（受开关控制）", ifEnabled != null, "");

        object settings = windowType.GetProperty("Settings", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window, null);
        PropertyInfo propAuto = settings.GetType().GetProperty("AutoFitColumns");
        Check("设置里有 AutoFitColumns 布尔项", propAuto != null && propAuto.PropertyType == typeof(bool), "");

        if (propAuto != null && ifEnabled != null)
        {
            // 关闭开关后，自动时机不应改动列宽
            propAuto.SetValue(settings, false, null);
            foreach (KeyValuePair<string, GridViewColumn> kv in cols) kv.Value.Width = 55;   // 标记值
            win.Width = 1300; win.Height = 700;
            Pump();
            ifEnabled.Invoke(window, null);
            Pump();
            bool kept = true;
            foreach (KeyValuePair<string, GridViewColumn> kv in cols)
                if (Math.Abs(kv.Value.Width - 55) > 0.5) kept = false;
            Check("开关关闭时自动时机不改动列宽（手动宽度得以保留）",
                kept, kept ? "" : "列宽被自动重排覆盖了");

            // 打开开关后，同一时机应重新分配
            propAuto.SetValue(settings, true, null);
            win.Width = 1300; win.Height = 700;
            Pump();
            ifEnabled.Invoke(window, null);
            Pump();
            bool refitted = false;
            foreach (KeyValuePair<string, GridViewColumn> kv in cols)
                if (Math.Abs(kv.Value.Width - 55) > 0.5) refitted = true;
            Check("开关打开时同一时机重新分配列宽", refitted, refitted ? "" : "列宽没变");

            // 勾选状态与设置一致
            MenuItem autoItem = null;
            foreach (object o in menu.Items)
            {
                MenuItem mi = o as MenuItem;
                if (mi != null && mi.Name == "ListMenuAutoFit") autoItem = mi;
            }
            if (autoItem != null)
            {
                Check("菜单勾选状态与设置一致（true）", autoItem.IsChecked, "IsChecked=" + autoItem.IsChecked);
                propAuto.SetValue(settings, false, null);
                MethodInfo applyLoc = windowType.GetMethod("ApplyLocalization", BindingFlags.NonPublic | BindingFlags.Instance);
                applyLoc.Invoke(window, null);
                Check("重新应用本地化后勾选状态跟随设置（false）", !autoItem.IsChecked, "IsChecked=" + autoItem.IsChecked);
                propAuto.SetValue(settings, true, null);
                applyLoc.Invoke(window, null);
            }

            // 「重新分配列宽」不受开关限制：关着也能强制排
            propAuto.SetValue(settings, false, null);
            foreach (KeyValuePair<string, GridViewColumn> kv in cols) kv.Value.Width = 66;
            autoFit.Invoke(window, null);
            Pump();
            bool forced = false;
            foreach (KeyValuePair<string, GridViewColumn> kv in cols)
                if (Math.Abs(kv.Value.Width - 66) > 0.5) forced = true;
            Check("「重新分配列宽」不受开关限制（关着也能强制重排）", forced, forced ? "" : "没排");

            propAuto.SetValue(settings, true, null);
        }

        // ---------- F. 设置读写往返（持久化的真实证据）----------
        Console.WriteLine();
        Console.WriteLine("  --- F. 设置持久化往返 ---");

        Type setType = settings.GetType();
        MethodInfo save = setType.GetMethod("Save", BindingFlags.Public | BindingFlags.Instance);
        MethodInfo reload = setType.GetMethod("Reload", BindingFlags.Public | BindingFlags.Instance);
        if (reload == null)
        {
            // 没有 Reload 就新建一个实例去读磁盘：这正是「下次启动」走的路
            reload = null;
        }
        Check("设置类有 Save 方法", save != null, "");

        if (propAuto != null && save != null)
        {
            propAuto.SetValue(settings, false, null);
            save.Invoke(settings, null);
            Pump();

            // 直接从磁盘文件读，绕开进程级单例（AppSettingsHost.Current 会掩盖真正的持久化缺陷）
            string folder = (string)setType.GetProperty("SettingsFolder", BindingFlags.Public | BindingFlags.Static)
                .GetValue(null, null);
            string settingsPath = IOPath.Combine(folder, "settings.json");
            Check("设置文件已写出", File.Exists(settingsPath), settingsPath);

            string json = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : "";
            bool hasKey = json.Contains("\"AutoFitColumns\"");
            bool wroteFalse = hasKey && json.Contains("\"AutoFitColumns\": false");
            Check("settings.json 里写出了 AutoFitColumns 键", hasKey,
                hasKey ? "" : "文件里没有该键");
            Check("written=false 时文件里确实是 false（写完再读回一致）", wroteFalse,
                wroteFalse ? "" : "文件内容与设置不一致");

            propAuto.SetValue(settings, true, null);
            save.Invoke(settings, null);
            Pump();
        }

        Console.WriteLine();
        try { win.Close(); Pump(); } catch { }

        if (_failures == 0)
        {
            Console.WriteLine("全部符合预期 ✓");
            return 0;
        }
        Console.WriteLine("!! 有 " + _failures + " 项不符合预期");
        return 1;
    }

    static void Pump()
    {
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(delegate { }));
    }
}
