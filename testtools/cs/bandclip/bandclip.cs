using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

/// <summary>
/// 框选与滚动条（下拉条）的边界验证。
///
/// 需求：框选生效时下拉条不能被框选盖住、也不能被框选抢走鼠标，
/// 否则用户只能框选、拖不动下拉条。预期框选区域**不包含**下拉条。
///
/// 验证手段：构造真实 MainWindow 并短暂显示（需要真实布局才能拿到滚动条几何与行容器），
/// 灌入足够多的行让下拉条出现，然后反射检查：
///   1. 覆盖层尺寸不含下拉条宽度；
///   2. 内容区（ContentArea）不含下拉条；
///   3. 下拉条区域内的按下点不允许起框选，内容区内的允许；
///   4. 同一矩形重复结算选择不产生新的 SelectionChanged（增量更新）。
///
/// 本探针不做合成鼠标输入（见提示词【1.7】），真实拖拽仍属人工项。
///
/// 用法: bandclip.exe &lt;HashChecker.exe&gt; [工作目录]
/// 退出码: 0 = 全部通过, 1 = 有失败项, 2 = 环境不满足（拿不到滚动条几何）
/// </summary>
class BandClipProbe
{
    static int _failures;
    static string _work;
    static Assembly _asm;
    static string _section = "all";
    static string _record = null;

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
            Console.WriteLine("用法: bandclip.exe <HashChecker.exe> [工作目录] [--section all|v06|v09] [--record <校验记录>]");
            return 2;
        }
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--section") _section = args[i + 1];
            if (args[i] == "--record") _record = args[i + 1];
        }

        string asmPath = Path.GetFullPath(args[0]);
        _work = args.Length > 1
            ? Path.GetFullPath(args[1])
            : Path.Combine(Path.GetTempPath(), "HashCheckerBandClip");

        if (Directory.Exists(_work)) Directory.Delete(_work, true);
        Directory.CreateDirectory(_work);

        _asm = Assembly.LoadFrom(asmPath);
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object s, ResolveEventArgs e)
        {
            string simple = new AssemblyName(e.Name).Name;
            string candidate = Path.Combine(Path.GetDirectoryName(asmPath), simple + ".exe");
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        };

        Console.WriteLine("=== 框选 / 下拉条边界验证 ===");
        Console.WriteLine("被测程序: " + asmPath);
        Console.WriteLine("");

        Type bandType = _asm.GetType("HashChecker.ListRubberBand");
        Type windowType = _asm.GetType("HashChecker.MainWindow");
        if (bandType == null || windowType == null)
        {
            Console.WriteLine("!! 找不到 ListRubberBand 或 MainWindow");
            return 2;
        }

        Type appType = _asm.GetType("HashChecker.App");
        if (appType != null)
        {
            try { Activator.CreateInstance(appType); }
            catch { }
        }

        object window;
        try { window = Activator.CreateInstance(windowType); }
        catch (Exception e)
        {
            Console.WriteLine("!! 构造 MainWindow 失败: " + Describe(e));
            return 2;
        }

        int code = 2;
        try
        {
            code = RunChecks(window, windowType, bandType);
        }
        catch (Exception e)
        {
            Console.WriteLine("!! 探针异常: " + Describe(e));
            code = 2;
        }
        finally
        {
            try
            {
                MethodInfo close = windowType.GetMethod("Close", Type.EmptyTypes);
                if (close != null) close.Invoke(window, null);
                Pump();
            }
            catch { }
        }

        Console.WriteLine("");
        if (code == 2)
        {
            Console.WriteLine("!! 环境不满足：拿不到滚动条几何，本次不作结论");
            return 2;
        }
        if (_failures == 0)
        {
            Console.WriteLine("全部符合预期 ✓");
            return 0;
        }
        Console.WriteLine("!! 有 " + _failures + " 项不符合预期");
        return 1;
    }

    static int RunChecks(object window, Type windowType, Type bandType)
    {
        // 短暂显示窗口：不显示就没有真实布局，ItemsControl 不会生成行容器、
        // ScrollViewer 也不会给出 ComputedScrollBarVisibility。本探针不做合成输入。
        try
        {
            MethodInfo show = windowType.GetMethod("Show", Type.EmptyTypes);
            if (show != null) show.Invoke(window, null);
            Pump();
        }
        catch (Exception e)
        {
            Console.WriteLine("!! 显示窗口失败: " + Describe(e));
            return 2;
        }

        object listObj = GetMember(window, windowType, "ItemList");
        if (listObj == null)
        {
            Console.WriteLine("!! 找不到 ItemList");
            return 2;
        }
        ListView list = (ListView)listObj;
        list.Width = 360;
        list.Height = 150;

        MethodInfo addPaths = windowType.GetMethod("AddPaths",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (addPaths == null)
        {
            Console.WriteLine("!! 找不到 AddPaths");
            return 2;
        }

        if (!string.IsNullOrEmpty(_record))
        {
            // 真实用例：直接载入用户给的校验记录（多文件记录会一次产生很多行）
            if (!File.Exists(_record))
            {
                Console.WriteLine("!! 校验记录不存在: " + _record);
                return 2;
            }
            Console.WriteLine("  载入真实校验记录: " + _record);
            addPaths.Invoke(window, new object[] { new string[] { _record }, false });
        }
        else
        {
            string dir = Path.Combine(_work, "files");
            Directory.CreateDirectory(dir);
            var paths = new List<string>();
            for (int i = 0; i < 60; i++)
            {
                string path = Path.Combine(dir, "f" + i + ".bin");
                var data = new byte[64];
                for (int k = 0; k < data.Length; k++) data[k] = (byte)((k * 7 + i) & 0xFF);
                File.WriteAllBytes(path, data);
                paths.Add(path);
            }
            addPaths.Invoke(window, new object[] { paths.ToArray(), false });
        }
        Pump();
        list.UpdateLayout();
        Pump();

        Console.WriteLine("  列表行数 = " + list.Items.Count);

        bool vBar = ((Visibility)list.GetValue(ScrollViewer.ComputedVerticalScrollBarVisibilityProperty)) == Visibility.Visible;
        bool hBar = ((Visibility)list.GetValue(ScrollViewer.ComputedHorizontalScrollBarVisibilityProperty)) == Visibility.Visible;
        Console.WriteLine("  列表尺寸 = " + list.ActualWidth + " x " + list.ActualHeight
            + "，纵向下拉条可见 = " + vBar + "，横向下拉条可见 = " + hBar);
        Console.WriteLine("");

        if (!vBar)
        {
            Console.WriteLine("  （纵向下拉条没有出现，无法验证本维度）");
            return 2;
        }

        // 挂覆盖层（窗口里真正的用法）
        object band;
        ConstructorInfo ctor = bandType.GetConstructor(new[] { typeof(ListView) });
        if (ctor == null)
        {
            Console.WriteLine("!! ListRubberBand 没有 ListView 构造函数");
            return 2;
        }
        band = ctor.Invoke(new object[] { list });

        Grid host = list.Parent as Grid;
        if (host == null)
        {
            Console.WriteLine("!! 列表父级不是 Grid");
            return 2;
        }
        bandType.GetMethod("AttachOverlay").Invoke(band, new object[] { host, Grid.GetRow(list) });
        Pump();

        double barWidth = SystemParameters.VerticalScrollBarWidth;
        double barHeight = SystemParameters.HorizontalScrollBarHeight;
        double listWidth = list.ActualWidth;
        double listHeight = list.ActualHeight;

        MethodInfo contentArea = bandType.GetMethod("ContentArea", BindingFlags.NonPublic | BindingFlags.Instance);
        MethodInfo allowed = bandType.GetMethod("IsBandStartAllowed", BindingFlags.NonPublic | BindingFlags.Instance);
        MethodInfo showOverlay = bandType.GetMethod("ShowOverlay", BindingFlags.NonPublic | BindingFlags.Instance);

        if (_section == "all" || _section == "v06")
        {
            // --- V06-1 覆盖层尺寸元数据不含下拉条 ---
            if (showOverlay == null)
            {
                Check("有 ShowOverlay", false, "反射取不到");
            }
            else
            {
                showOverlay.Invoke(band, null);
                object overlay = GetMember(band, bandType, "_overlay");
                FrameworkElement fe = overlay as FrameworkElement;
                if (fe == null)
                {
                    Check("取到覆盖层", false, "字段 _overlay 不是 FrameworkElement");
                }
                else
                {
                    double w = fe.Width;
                    Check("覆盖层宽度不含下拉条",
                        w <= listWidth - barWidth + 0.5,
                        "覆盖层宽 = " + w + "，列表宽 = " + listWidth + "，下拉条宽 = " + barWidth);
                    Check("覆盖层高度不超过列表",
                        fe.Height <= listHeight + 0.5,
                        "覆盖层高 = " + fe.Height + "，列表高 = " + listHeight);
                }
            }

            // --- V06-2/3 内容区与起框选判定 ---
            if (contentArea == null)
            {
                Check("有内容区计算 ContentArea（框选区域不含下拉条）", false,
                    "反射取不到 —— 当前实现把整个列表都当成框选区域");
            }
            else
            {
                Rect area = (Rect)contentArea.Invoke(band, null);
                Check("内容区宽度不含下拉条",
                    area.Width <= listWidth - barWidth + 0.5,
                    "内容区宽 = " + area.Width + "，列表宽 = " + listWidth);
                Check("内容区起点在列表左上",
                    Math.Abs(area.X) < 0.5 && Math.Abs(area.Y) < 0.5,
                    "内容区 = " + area);
            }

            if (allowed == null)
            {
                Check("有「是否允许起框选」判定 IsBandStartAllowed", false,
                    "反射取不到 —— 下拉条上的按下会被框选接管，鼠标捕获会被抢走");
            }
            else
            {
                Point onBar = new Point(listWidth - barWidth / 2.0, listHeight / 2.0);
                Point inContent = new Point(Math.Min(20.0, listWidth / 4.0), listHeight / 2.0);
                bool barAllowed = (bool)allowed.Invoke(band, new object[] { onBar });
                bool contentAllowed = (bool)allowed.Invoke(band, new object[] { inContent });
                Check("下拉条上的按下不允许起框选", !barAllowed,
                    "点 " + onBar + " 判定 = " + barAllowed);
                Check("内容区内的按下允许起框选", contentAllowed,
                    "点 " + inContent + " 判定 = " + contentAllowed);
            }

            // --- V06-4 同一矩形重复结算不产生新的 SelectionChanged ---
            MethodInfo applySelection = bandType.GetMethod("ApplySelection", BindingFlags.NonPublic | BindingFlags.Instance);
            if (applySelection == null)
            {
                Check("有 ApplySelection", false, "反射取不到");
            }
            else
            {
                double contentWidth = listWidth - barWidth;
                Rect whole = new Rect(0, 0, Math.Max(1.0, contentWidth), Math.Max(1.0, listHeight));
                int events = 0;
                SelectionChangedEventHandler handler = delegate(object s, SelectionChangedEventArgs e) { events++; };
                list.SelectionChanged += handler;
                try
                {
                    applySelection.Invoke(band, new object[] { whole });
                    int first = events;
                    events = 0;
                    applySelection.Invoke(band, new object[] { whole });
                    int second = events;
                    Console.WriteLine("  第一次结算触发 SelectionChanged = " + first + " 次，第二次 = " + second + " 次");
                    Check("同一矩形重复结算不再触发 SelectionChanged",
                        second == 0,
                        second == 0 ? "" : "第二次仍触发 " + second + " 次 —— 每次移动都清空重建选择，行多时卡顿");
                }
                finally
                {
                    list.SelectionChanged -= handler;
                }
            }
        }

        if (_section == "all" || _section == "v09")
        {
            // --- V09 横向滚动条下内容区高度的扣减 ---
            if (!hBar)
            {
                Console.WriteLine("  （横向下拉条没有出现，V09 本维度不适用）");
            }
            else if (contentArea == null)
            {
                Check("横向下拉条下内容区高度扣除下拉条", false,
                    "反射取不到 ContentArea");
            }
            else
            {
                Rect area = (Rect)contentArea.Invoke(band, null);
                Check("内容区高度扣除横向下拉条",
                    area.Height <= listHeight - barHeight + 0.5,
                    "内容区高 = " + area.Height + "，列表高 = " + listHeight + "，横向下拉条高 = " + barHeight);
                Check("内容区右下角不越过横向下拉条",
                    area.Bottom <= listHeight - barHeight + 0.5,
                    "内容区 = " + area);
                if (showOverlay != null)
                {
                    showOverlay.Invoke(band, null);
                    FrameworkElement fe = GetMember(band, bandType, "_overlay") as FrameworkElement;
                    if (fe != null)
                    {
                        Check("覆盖层高度不超过内容区高度",
                            fe.Height <= area.Height + 0.5,
                            "覆盖层高 = " + fe.Height + "，内容区高 = " + area.Height);
                    }
                }
            }
        }

        Console.WriteLine("");
        Console.WriteLine("  （真实鼠标拖拽与真实点击下拉条仍属人工项，见 D5）");
        return 0;
    }

    static void Pump()
    {
        Dispatcher d = Dispatcher.CurrentDispatcher;
        d.Invoke(DispatcherPriority.Background, new Action(delegate { }));
    }

    static object GetMember(object instance, Type type, string name)
    {
        FieldInfo field = type.GetField(name,
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public | BindingFlags.Static);
        if (field != null) return field.GetValue(instance);
        PropertyInfo prop = type.GetProperty(name,
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public | BindingFlags.Static);
        return prop == null ? null : prop.GetValue(instance, null);
    }

    static string Describe(Exception e)
    {
        if (e is TargetInvocationException && e.InnerException != null) return Describe(e.InnerException);
        return e.GetType().Name + ": " + e.Message;
    }
}
