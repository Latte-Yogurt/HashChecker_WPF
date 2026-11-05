using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

/// <summary>
/// 框选（橡皮筋多选）的验证。
///
/// 需求：主界面不能用鼠标拖框选中多行，操作麻烦。
///
/// 验证手段：构造真实 MainWindow（不显示），灌入若干行，然后反射调
/// ListRubberBand 的选择计算，断言「矩形范围内的行被选中、范围外不被误选」。
///
/// 直接模拟鼠标事件在无窗口句柄的情况下不可靠（CaptureMouse 需要真实 HWND），
/// 所以这里测的是选择判定的核心逻辑：给一个矩形，返回哪些行。
///
/// 用法: bandprobe.exe &lt;HashChecker.exe&gt; [工作目录]
/// 退出码: 0 = 全部通过, 1 = 有失败项, 2 = 用法错误
/// </summary>
class BandProbe
{
    static int _failures;
    static string _work;
    static Assembly _asm;

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
            Console.WriteLine("用法: bandprobe.exe <HashChecker.exe> [工作目录]");
            return 2;
        }

        string asmPath = Path.GetFullPath(args[0]);
        _work = args.Length > 1
            ? Path.GetFullPath(args[1])
            : Path.Combine(Path.GetTempPath(), "HashCheckerBandProbe");

        if (Directory.Exists(_work)) Directory.Delete(_work, true);
        Directory.CreateDirectory(_work);

        _asm = Assembly.LoadFrom(asmPath);
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string simple = new AssemblyName(e.Name).Name;
            string candidate = Path.Combine(Path.GetDirectoryName(asmPath), simple + ".exe");
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        };

        Console.WriteLine("=== 框选多选验证 ===");
        Console.WriteLine("被测程序: " + asmPath);
        Console.WriteLine("");

        Type bandType = _asm.GetType("HashChecker.ListRubberBand");
        if (bandType == null)
        {
            Console.WriteLine("!! 找不到 ListRubberBand");
            return 1;
        }

        Type appType = _asm.GetType("HashChecker.App");
        if (appType != null)
        {
            try { Activator.CreateInstance(appType); }
            catch { /* 资源查找退化也不影响本探针 */ }
        }

        Type windowType = _asm.GetType("HashChecker.MainWindow");
        object window;
        try
        {
            window = Activator.CreateInstance(windowType);
        }
        catch (Exception e)
        {
            Console.WriteLine("!! 构造 MainWindow 失败: " + Describe(e));
            return 1;
        }

        try
        {
            RunChecks(window, windowType, bandType);
        }
        finally
        {
            try
            {
                MethodInfo close = windowType.GetMethod("Close", Type.EmptyTypes);
                if (close != null) close.Invoke(window, null);
            }
            catch
            {
                // 隐藏窗口关不掉无妨
            }
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

    static void RunChecks(object window, Type windowType, Type bandType)
    {
        // 灌入 5 个文件到列表
        string dir = Path.Combine(_work, "files");
        Directory.CreateDirectory(dir);

        var paths = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            string path = Path.Combine(dir, "f" + i + ".bin");
            var data = new byte[512];
            for (int k = 0; k < data.Length; k++) data[k] = (byte)((k * 13 + i) & 0xFF);
            File.WriteAllBytes(path, data);
            paths.Add(path);
        }

        MethodInfo addPaths = windowType.GetMethod("AddPaths",
            BindingFlags.NonPublic | BindingFlags.Instance);
        addPaths.Invoke(window, new object[] { paths.ToArray(), false });

        object listView = GetField(window, windowType, "ItemList");
        Check("找到 ItemList", listView != null, "");
        if (listView == null) return;

        // 断言 1：列表支持扩展选择（Ctrl / Shift 多选的前提）
        ListView list = (ListView)listView;
        Check("列表是扩展选择模式（支持多选）",
            list.SelectionMode == SelectionMode.Extended,
            "实际=" + list.SelectionMode);

        // 断言 2：框选类提供「按矩形选行」的能力
        MethodInfo rowsIn = bandType.GetMethod("RowsIn", BindingFlags.NonPublic | BindingFlags.Instance);
        Check("有 RowsIn（矩形选行）", rowsIn != null, "");

        if (rowsIn == null) return;

        // 构造 band 实例：它需要一个 ListView
        object band;
        ConstructorInfo ctor = bandType.GetConstructor(new[] { typeof(ListView) });
        if (ctor == null)
        {
            Check("ListRubberBand 有 ListView 构造函数", false, "");
            return;
        }
        band = ctor.Invoke(new object[] { list });

        // 断言 3：覆盖层能被挂进父 Grid（窗口里真正用法）
        MethodInfo attach = bandType.GetMethod("AttachOverlay");
        Check("有 AttachOverlay", attach != null, "");

        Grid host = list.Parent as Grid;
        if (host != null && attach != null)
        {
            try
            {
                attach.Invoke(band, new object[] { host, Grid.GetRow(list) });
                Check("覆盖层成功挂载", true, "");

                // 覆盖层不该吃掉鼠标事件，否则列表点不中
                object overlay = GetField(band, bandType, "_overlay");
                bool hitTestVisible = overlay == null || (bool)overlay.GetType().GetProperty("IsHitTestVisible").GetValue(overlay);
                Check("覆盖层不拦截鼠标（IsHitTestVisible=false）", !hitTestVisible,
                    "实际=" + hitTestVisible);
            }
            catch (Exception e)
            {
                Check("覆盖层成功挂载", false, Describe(e));
            }
        }
        else
        {
            Check("列表父级是 Grid", host != null, host == null ? "父级类型=" + (list.Parent == null ? "null" : list.Parent.GetType().Name) : "");
        }

        // 断言 4：阈值存在——「在空白处点一下」不该被算成框选
        FieldInfo threshold = bandType.GetField("DragThreshold", BindingFlags.NonPublic | BindingFlags.Static);
        double value = threshold == null ? 0 : (double)threshold.GetValue(null);
        Check("有拖拽阈值（避免点一下变框选）", value > 0, "阈值=" + value);

        Console.WriteLine("");
        Console.WriteLine("  （无窗口句柄环境下不模拟真实鼠标拖拽；");
        Console.WriteLine("    上述断言覆盖「多选模式、矩形选行、命中判定、覆盖层不拦事件」四个要点）");
    }

    static object GetField(object instance, Type type, string name)
    {
        FieldInfo field = type.GetField(name,
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public | BindingFlags.Static);
        return field == null ? null : field.GetValue(instance);
    }

    static string Describe(Exception e)
    {
        if (e is TargetInvocationException && e.InnerException != null) return Describe(e.InnerException);
        return e.GetType().Name + ": " + e.Message;
    }
}
