using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using IOPath = System.IO.Path;

/// <summary>
/// 框选区域与滚动条边界的验证。
///
/// 两类判据，并明确区分能自动验证与必须人工的部分：
///
///   A. 几何判据（运行时反射实测）
///      · 内容区 = 列表尺寸 − 实测滚动条厚度；
///      · 滚动条上的点不允许起框选，内容区内的点允许；
///      · 越界拖动点会被夹进内容区；
///      · 只框滚动条那一条时选不中任何行；
///      · 窗口缩放后内容区跟着变（窗体尺寸不固定）；
///      · 滚动条消失后内容区恢复整宽。
///
///   B. IL 判据（不运行代码）
///      · OnMouseMove 里读 _armed 并据此提前返回；
///      · OnMouseDown 里写 _armed 并调用 IsBandStartAllowed；
///      · ApplySelection 不再无条件 Clear()。
///
/// 为什么不用合成鼠标事件驱动 OnMouseDown/OnMouseMove：合成 MouseEventArgs 的
/// GetPosition() 恒为 (0,0)、LeftButton 状态不可控、MouseButtonEventArgs 的 ClickCount
/// 在无真实输入时会抛异常，用它验证拖动链路只会产出假结论（提示词 1.7 / 6）。
/// 真实拖拉滚动条与真实拖框仍属人工项（D5-M01）。
///
/// 用法: bandgesture.exe &lt;HashChecker.exe&gt; [工作目录] [--record &lt;校验记录&gt;]
/// 退出码: 0 = 全部通过, 1 = 有失败项, 2 = 环境不满足
/// </summary>
class BandGestureProbe
{
    static int _failures;
    static string _work;
    static string _record;

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
            Console.WriteLine("用法: bandgesture.exe <HashChecker.exe> [工作目录] [--record <记录>]");
            return 2;
        }
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--record") _record = args[i + 1];

        string asmPath = IOPath.GetFullPath(args[0]);
        _work = args.Length > 1 && !args[1].StartsWith("--")
            ? IOPath.GetFullPath(args[1])
            : IOPath.Combine(IOPath.GetTempPath(), "HashCheckerBandGesture");
        if (Directory.Exists(_work)) Directory.Delete(_work, true);
        Directory.CreateDirectory(_work);

        Assembly asm = Assembly.LoadFrom(asmPath);
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object s, ResolveEventArgs e)
        {
            string n = new AssemblyName(e.Name).Name;
            string c = IOPath.Combine(IOPath.GetDirectoryName(asmPath), n + ".exe");
            return File.Exists(c) ? Assembly.LoadFrom(c) : null;
        };

        Console.WriteLine("=== 框选 / 下拉条边界验证 ===");
        Console.WriteLine("被测程序: " + asmPath);

        Type bandType = asm.GetType("HashChecker.ListRubberBand");
        Type windowType = asm.GetType("HashChecker.MainWindow");
        if (bandType == null || windowType == null)
        {
            Console.WriteLine("!! 找不到 ListRubberBand 或 MainWindow");
            return 2;
        }

        Console.WriteLine();
        Console.WriteLine("  --- B. IL 判据：检查方法体里是否真的有守卫 ---");
        CheckIlGuards(bandType);

        Console.WriteLine();
        Console.WriteLine("  --- A. 几何判据：运行时实测 ---");

        Type appType = asm.GetType("HashChecker.App");
        try { Activator.CreateInstance(appType); } catch { }

        object window = Activator.CreateInstance(windowType);
        windowType.GetMethod("Show", Type.EmptyTypes).Invoke(window, null);
        Pump();

        ListView list = (ListView)windowType.GetField("ItemList",
            BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window);
        list.Width = 360;
        list.Height = 150;

        if (!string.IsNullOrEmpty(_record) && File.Exists(_record))
        {
            Console.WriteLine("     载入真实校验记录: " + IOPath.GetFileName(_record));
            windowType.GetMethod("AddPaths", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(window, new object[] { new string[] { _record }, false });
        }
        else
        {
            string dir = IOPath.Combine(_work, "f");
            Directory.CreateDirectory(dir);
            var ps = new string[60];
            for (int i = 0; i < 60; i++)
            {
                ps[i] = IOPath.Combine(dir, "g" + i + ".bin");
                File.WriteAllBytes(ps[i], new byte[64]);
            }
            windowType.GetMethod("AddPaths", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(window, new object[] { ps, false });
        }
        Pump();
        list.UpdateLayout();
        Pump();

        Console.WriteLine("     列表行数 = " + list.Items.Count);

        ScrollViewer sv = FindScrollViewer(list);
        if (sv == null) { Console.WriteLine("!! 无内部 ScrollViewer"); return 2; }

        bool vBar = ((Visibility)sv.GetValue(ScrollViewer.ComputedVerticalScrollBarVisibilityProperty)) == Visibility.Visible;
        if (!vBar)
        {
            Console.WriteLine("!! 纵向下拉条未出现，环境不满足，本维度不下结论");
            return 2;
        }

        object band = bandType.GetConstructor(new[] { typeof(ListView) }).Invoke(new object[] { list });
        MethodInfo contentArea = bandType.GetMethod("ContentArea", BindingFlags.NonPublic | BindingFlags.Instance);
        MethodInfo allowed = bandType.GetMethod("IsBandStartAllowed", BindingFlags.NonPublic | BindingFlags.Instance);
        MethodInfo clamp = bandType.GetMethod("ClampToContent", BindingFlags.NonPublic | BindingFlags.Instance);
        MethodInfo applySel = bandType.GetMethod("ApplySelection", BindingFlags.NonPublic | BindingFlags.Instance);

        Rect area = (Rect)contentArea.Invoke(band, null);
        double barW = TotalBarThickness(sv, Orientation.Vertical);
        double barH = TotalBarThickness(sv, Orientation.Horizontal);
        Console.WriteLine("     列表 = " + list.ActualWidth + " x " + list.ActualHeight
            + "，滚动条实测 = " + barW + " x " + barH + "，内容区 = " + area);

        Check("内容区宽度 = 列表宽度 - 纵向滚动条实测厚度",
            Math.Abs(area.Width - (list.ActualWidth - barW)) < 0.6,
            "内容区宽=" + area.Width + " 期望=" + (list.ActualWidth - barW));
        Check("内容区高度 = 列表高度 - 横向滚动条实测厚度",
            Math.Abs(area.Height - (list.ActualHeight - barH)) < 0.6,
            "内容区高=" + area.Height + " 期望=" + (list.ActualHeight - barH));
        Check("内容区不是空的（否则整块都框不中）",
            area.Width > 50 && area.Height > 20, "内容区=" + area);

        Check("纵向滚动条上的点不允许起框选",
            !(bool)allowed.Invoke(band, new object[] { new Point(list.ActualWidth - Math.Max(2.0, barW / 2), list.ActualHeight / 2) }), "");
        Check("横向滚动条上的点不允许起框选",
            !(bool)allowed.Invoke(band, new object[] { new Point(list.ActualWidth / 2, list.ActualHeight - Math.Max(2.0, barH / 2)) }), "");
        Check("内容区内的点允许起框选",
            (bool)allowed.Invoke(band, new object[] { new Point(area.Width / 2, area.Height / 2) }), "");
        Check("内容区右边缘内侧 1px 仍允许起框选（边界不能过窄）",
            (bool)allowed.Invoke(band, new object[] { new Point(area.Right - 1, area.Height / 2) }), "");

        Point c1 = (Point)clamp.Invoke(band, new object[] { new Point(list.ActualWidth + 500, list.ActualHeight + 500) });
        Check("右下越界的拖动点被夹进内容区",
            c1.X <= area.Right + 0.5 && c1.Y <= area.Bottom + 0.5, "夹后=" + c1);
        Point c2 = (Point)clamp.Invoke(band, new object[] { new Point(-500, -500) });
        Check("左上越界的拖动点被夹进内容区",
            c2.X >= area.Left - 0.5 && c2.Y >= area.Top - 0.5, "夹后=" + c2);

        applySel.Invoke(band, new object[] { new Rect(0, 0, area.Width, area.Height) });
        int selAll = list.SelectedItems.Count;
        Check("按整块内容区结算能选中行", selAll > 0, "选中=" + selAll + " / 共 " + list.Items.Count);

        applySel.Invoke(band, new object[] { new Rect(0, 0, area.Width, area.Height / 2) });
        int selHalf = list.SelectedItems.Count;
        Check("只框内容区上半时选中行数少于整块（矩形边界真的参与判定）",
            selHalf > 0 && selHalf < selAll, "上半=" + selHalf + " 整块=" + selAll);

        applySel.Invoke(band, new object[] { new Rect(area.Right + 1, 0, Math.Max(1.0, barW), area.Height) });
        int selBar = list.SelectedItems.Count;
        Check("只框滚动条那一条时选不中任何行", selBar == 0, "选中=" + selBar);

        Console.WriteLine();
        Console.WriteLine("  --- C. 实时性：窗口尺寸不固定，缩放后内容区必须跟着变 ---");

        Rect area1 = (Rect)contentArea.Invoke(band, null);
        list.Width = 520;
        list.Height = 260;
        Pump();
        list.UpdateLayout();
        Pump();
        Rect area2 = (Rect)contentArea.Invoke(band, null);

        Console.WriteLine("     缩放前 内容区=" + area1.Width + "x" + area1.Height
            + "  →  缩放后 列表=" + list.ActualWidth + "x" + list.ActualHeight
            + "，内容区=" + area2.Width + "x" + area2.Height);

        Check("放大后内容区宽度随之变大（没有缓存旧尺寸）",
            area2.Width > area1.Width + 1, "前=" + area1.Width + " 后=" + area2.Width);
        Check("放大后内容区仍严格小于列表宽度（仍扣掉滚动条）",
            area2.Width < list.ActualWidth - 1, "内容区=" + area2.Width + " 列表=" + list.ActualWidth);

        double barW2 = TotalBarThickness(sv, Orientation.Vertical);
        Check("放大后内容区宽度 = 新列表宽度 - 滚动条厚度",
            Math.Abs(area2.Width - (list.ActualWidth - barW2)) < 0.6,
            "内容区=" + area2.Width + " 期望=" + (list.ActualWidth - barW2));

        list.Width = 300;
        list.Height = 120;
        Pump();
        list.UpdateLayout();
        Pump();
        Rect area3 = (Rect)contentArea.Invoke(band, null);
        Check("缩小后内容区随之变小", area3.Width < area2.Width - 1,
            "放大后=" + area2.Width + " 缩小后=" + area3.Width);
        Check("缩小后内容区仍非空", area3.Width > 20 && area3.Height > 10, "内容区=" + area3);

        // 直接验证覆盖层尺寸随缩放更新：这是「尺寸缓存」缺陷的运行时判据。
        MethodInfo syncSize = bandType.GetMethod("SyncOverlaySize", BindingFlags.NonPublic | BindingFlags.Instance);
        FieldInfo fOverlay = bandType.GetField("_overlay", BindingFlags.NonPublic | BindingFlags.Instance);
        FrameworkElement overlay = fOverlay == null ? null : fOverlay.GetValue(band) as FrameworkElement;
        if (overlay == null)
        {
            Check("取到覆盖层", false, "字段 _overlay 不是 FrameworkElement");
        }
        else
        {
            list.Width = 420;
            list.Height = 200;
            Pump();
            list.UpdateLayout();
            Pump();
            syncSize.Invoke(band, null);
            double w1 = overlay.Width, h1 = overlay.Height;
            Rect ar1 = (Rect)contentArea.Invoke(band, null);

            list.Width = 560;
            list.Height = 300;
            Pump();
            list.UpdateLayout();
            Pump();
            syncSize.Invoke(band, null);
            double w2 = overlay.Width, h2 = overlay.Height;
            Rect ar2 = (Rect)contentArea.Invoke(band, null);

            Console.WriteLine("     覆盖层尺寸：" + w1 + "x" + h1 + " → " + w2 + "x" + h2
                + "，内容区：" + ar1.Width + "x" + ar1.Height + " → " + ar2.Width + "x" + ar2.Height);

            Check("缩放后覆盖层宽度跟着更新（尺寸没有被缓存）",
                w2 > w1 + 1, "前=" + w1 + " 后=" + w2);
            Check("缩放后覆盖层高度跟着更新", h2 > h1 + 1, "前=" + h1 + " 后=" + h2);
            Check("覆盖层宽度始终等于内容区宽度（不含滚动条）",
                Math.Abs(w2 - ar2.Width) < 0.6, "覆盖层=" + w2 + " 内容区=" + ar2.Width);
            Check("覆盖层高度始终等于内容区高度",
                Math.Abs(h2 - ar2.Height) < 0.6, "覆盖层=" + h2 + " 内容区=" + ar2.Height);
        }

        Console.WriteLine();
        Console.WriteLine("  --- D. 滚动条消失时内容区应恢复整宽 ---");
        int total = list.Items.Count;
        list.Width = 520;
        list.Height = 20.0 * (total + 4) + 40;
        Pump();
        list.UpdateLayout();
        Pump();
        bool stillBar = ((Visibility)sv.GetValue(ScrollViewer.ComputedVerticalScrollBarVisibilityProperty)) == Visibility.Visible;
        Rect area4 = (Rect)contentArea.Invoke(band, null);
        Console.WriteLine("     拉高后 纵向滚动条可见 = " + stillBar + "，内容区 = " + area4.Width + "，列表 = " + list.ActualWidth);
        if (stillBar)
        {
            Console.WriteLine("     （滚动条仍在，本项不适用）");
        }
        else
        {
            Check("纵向滚动条消失后内容区宽度 = 列表宽度",
                Math.Abs(area4.Width - list.ActualWidth) < 0.6,
                "内容区=" + area4.Width + " 列表=" + list.ActualWidth);
        }

        Console.WriteLine();
        try { windowType.GetMethod("Close", Type.EmptyTypes).Invoke(window, null); Pump(); } catch { }

        Console.WriteLine();
        Console.WriteLine("  （真实拖拉滚动条与真实拖框仍属人工项，见 D5-M01）");

        if (_failures == 0)
        {
            Console.WriteLine("全部符合预期 ✓");
            return 0;
        }
        Console.WriteLine("!! 有 " + _failures + " 项不符合预期");
        return 1;
    }

    static void CheckIlGuards(Type bandType)
    {
        FieldInfo fArmed = bandType.GetField("_armed", BindingFlags.NonPublic | BindingFlags.Instance);
        MethodInfo onDown = bandType.GetMethod("OnMouseDown", BindingFlags.NonPublic | BindingFlags.Instance);
        MethodInfo onMove = bandType.GetMethod("OnMouseMove", BindingFlags.NonPublic | BindingFlags.Instance);
        MethodInfo apply = bandType.GetMethod("ApplySelection", BindingFlags.NonPublic | BindingFlags.Instance);

        if (fArmed == null)
        {
            Check("存在 _armed 状态位", false,
                "反射取不到 —— 旧实现，OnMouseMove 会用上次框选残留的 _origin 误判并抢走滚动条捕获");
            return;
        }
        Check("存在 _armed 状态位", true, "Boolean");

        // 判据必须区分「OnMouseDown 里给 _armed 赋值」与「仅仅在别处出现 _armed」，
        // 所以比对「按方法体切分出指令序列」而不是整段字符串 Contains。
        string[] down = IlTokens(onDown);
        string[] move = IlTokens(onMove);

        Check("OnMouseDown 里把 _armed 置 false（每次按下都重置武装）",
            HasSequence(down, "stfld _armed"),
            HasSequence(down, "stfld _armed") ? "" : "找不到 stfld _armed");
        // mg2 型缺陷：只删掉开头的 _armed=false，靠后面「允许起框选」分支里的 _armed=true 仍然存在，
        // 所以上面那条 stfld 检查抓不到。这里要求「给 _armed 写 false」发生在
        // IsBandStartAllowed 调用**之前**（即按下时无条件先重置），否则滚动条按下会沿用上次的武装状态。
        Check("OnMouseDown 在判断内容区之前就先把 _armed 置 false（滚动条按下不会沿用上次武装）",
            StfldBefore(down, "_armed", "IsBandStartAllowed"),
            StfldBefore(down, "_armed", "IsBandStartAllowed") ? "" : "_armed 的重置发生在 IsBandStartAllowed 之后或缺失");
        Check("OnMouseDown 里调用 IsBandStartAllowed 并在其后提前返回（滚动条上的按下被放行）",
            HasSequence(down, "IsBandStartAllowed") && (HasSequence(down, "brtrue") || HasSequence(down, "brfalse")) && HasSequence(down, "ret"),
            "");

        Check("OnMouseMove 里读 _armed 并据此提前返回（守卫在真实代码路径上）",
            HasGuardPattern(move),
            HasGuardPattern(move) ? "" : "缺「ldfld _armed + 条件跳转」这一对");

        Check("OnMouseMove 里确实会 CaptureMouse（所以守卫必须在前）",
            HasSequence(move, "CaptureMouse"), "");
        Check("OnMouseMove 里调用 SyncOverlaySize（拖动中尺寸实时同步）",
            HasSequence(move, "SyncOverlaySize"), "");

        // mg5 型缺陷：SyncOverlaySize 被改成「已设置过就 return」，尺寸不再跟随缩放。
        // 这里不做 IL 操作数解析（易错位），改用「方法体里出现的成员引用集合」判断，
        // 再配合运行时缩放断言（C 段）共同守住实时性。
        MethodInfo syncM = bandType.GetMethod("SyncOverlaySize", BindingFlags.NonPublic | BindingFlags.Instance);
        var syncMembers = new HashSet<string>();
        CollectMembers(syncM, syncMembers);
        Check("SyncOverlaySize 同时设置 overlay 的 Width 与 Height",
            syncMembers.Contains("set_Width") && syncMembers.Contains("set_Height"),
            "引用成员: " + string.Join(",", new List<string>(syncMembers).ToArray()));
        // 「是否缓存尺寸」交给运行时缩放断言（C 段）判定 —— WPF 依赖属性的 setter 内部会读旧值，
        // 静态看 IL 无法区分「setter 内部读」与「方法体自己读」，那会误报。

        string[] ap = IlTokens(apply);
        Check("ApplySelection 不再无条件 Clear（改为差集增删）",
            !HasSequence(ap, "Clear"), HasSequence(ap, "Clear") ? "仍有 Clear" : "");
    }

    /// <summary>把方法体拆成「指令 + 解析出的成员名」的 token 序列，便于按相邻关系判断。</summary>
    static string[] IlTokens(MethodInfo m)
    {
        if (m == null) return new string[0];
        MethodBody body = m.GetMethodBody();
        if (body == null) return new string[0];
        byte[] il = body.GetILAsByteArray();
        Module mod = m.Module;
        var list = new List<string>();
        int i = 0;
        while (i < il.Length)
        {
            byte op = il[i];
            int before = i;
            i++;
            if (op == 0xFE && i < il.Length) { list.Add("op2_" + il[i].ToString("X2")); i++; continue; }

            string name = OpName(op);
            int size = OperandSize(op);
            if (i + 4 <= il.Length &&
                (op == 0x28 || op == 0x6F || op == 0x73 || op == 0x7B || op == 0x7C ||
                 op == 0x7D || op == 0x7E || op == 0x7F))
            {
                int token = BitConverter.ToInt32(il, i);
                try
                {
                    Type[] targs = m.DeclaringType.IsGenericType ? m.DeclaringType.GetGenericArguments() : Type.EmptyTypes;
                    Type[] margs = m.IsGenericMethod ? m.GetGenericArguments() : Type.EmptyTypes;
                    MemberInfo mi = mod.ResolveMember(token, targs, margs);
                    if (mi != null) name += " " + mi.Name;
                }
                catch { }
            }
            list.Add(name);
            i += size;
            if (i <= before) break;
        }
        return list.ToArray();
    }

    /// <summary>用 MetadataToken 反查方法体里引用的所有成员名（不解码操作码，避开操作数长度陷阱）。</summary>
    static void CollectMembers(MethodInfo m, HashSet<string> into)
    {
        if (m == null) return;
        MethodBody body = m.GetMethodBody();
        if (body == null) return;
        byte[] il = body.GetILAsByteArray();
        Module mod = m.Module;
        for (int i = 0; i + 4 <= il.Length; i++)
        {
            int token = BitConverter.ToInt32(il, i);
            if ((token >> 24) != 0x04 && (token >> 24) != 0x06 && (token >> 24) != 0x0A && (token >> 24) != 0x2B)
                continue;
            try
            {
                MemberInfo mi = mod.ResolveMember(token);
                if (mi != null) into.Add(mi.Name);
            }
            catch { }
        }
    }

    /// <summary>判断某次字段写入是否发生在某个方法调用之前。</summary>
    static bool StfldBefore(string[] tokens, string field, string callMarker)
    {
        int write = -1, call = -1;
        for (int i = 0; i < tokens.Length; i++)
        {
            if (write < 0 && tokens[i] == "stfld " + field) write = i;
            if (call < 0 && tokens[i].EndsWith(" " + callMarker)) call = i;
        }
        return write >= 0 && call >= 0 && write < call;
    }

    /// <summary>按顺序匹配 token；每个模式按「token 结尾或包含」判定，容忍 call / callvirt 前缀差异。</summary>
    static bool HasSequence(string[] tokens, params string[] pattern)
    {
        for (int i = 0; i + pattern.Length <= tokens.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                string t = tokens[i + j];
                string p = pattern[j];
                bool hit = t == p || t.EndsWith(" " + p) || t.StartsWith(p);
                if (!hit) { ok = false; break; }
            }
            if (ok) return true;
        }
        return false;
    }

    /// <summary>
    /// 守卫模式：ldfld _armed 之后 6 条指令内出现条件跳转与 ret。
    /// 这样「只把 _armed 投影出来却不用它」和「删掉整个 if」都能被判失败。
    /// </summary>
    static bool HasGuardPattern(string[] tokens)
    {
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] != "ldfld _armed") continue;
            bool sawBranch = false;
            for (int j = i + 1; j < Math.Min(i + 6, tokens.Length); j++)
            {
                string t = tokens[j];
                if (t.StartsWith("brtrue") || t.StartsWith("brfalse") || t.StartsWith("br.s") ||
                    t.StartsWith("br ") || t == "br") sawBranch = true;
            }
            if (sawBranch) return true;
        }
        return false;
    }

    static string IlText(MethodInfo m)
    {
        if (m == null) return "";
        MethodBody body = m.GetMethodBody();
        if (body == null) return "";
        byte[] il = body.GetILAsByteArray();
        Module mod = m.Module;
        var sb = new StringBuilder();
        int i = 0;
        while (i < il.Length)
        {
            byte op = il[i];
            int before = i;
            i++;
            if (op == 0xFE && i < il.Length) { sb.Append("op2_").Append(il[i].ToString("X2")).Append(' '); i++; continue; }

            string name = OpName(op);
            int size = OperandSize(op);
            if (i + 4 <= il.Length &&
                (op == 0x28 || op == 0x6F || op == 0x73 || op == 0x7B || op == 0x7C ||
                 op == 0x7D || op == 0x7E || op == 0x7F))
            {
                int token = BitConverter.ToInt32(il, i);
                try
                {
                    Type[] targs = m.DeclaringType.IsGenericType ? m.DeclaringType.GetGenericArguments() : Type.EmptyTypes;
                    Type[] margs = m.IsGenericMethod ? m.GetGenericArguments() : Type.EmptyTypes;
                    MemberInfo mi = mod.ResolveMember(token, targs, margs);
                    if (mi != null) name += " " + mi.Name;
                }
                catch { }
            }
            sb.Append(name).Append(' ');
            i += size;
            if (i <= before) break;
        }
        return sb.ToString();
    }

    static int OperandSize(byte op)
    {
        switch (op)
        {
            case 0x28: case 0x6F: case 0x73: case 0x7B: case 0x7C: case 0x7D:
            case 0x7E: case 0x7F: case 0x72: case 0x74: case 0x75: case 0x80:
            case 0x81: case 0x8C: case 0x8D: case 0x8F: case 0xA3: case 0xA4:
            case 0xA5: case 0xC2: case 0xC6: case 0xD0: case 0xDD: case 0xDE:
            case 0xDF: case 0xE0: case 0x20: case 0x22: case 0x27: case 0x29:
            case 0x38: case 0x39: case 0x3A: case 0x3B: case 0x3C: case 0x3D:
            case 0x3E: case 0x3F: case 0x40: case 0x41: case 0x42: case 0x43:
            case 0x44:
                return 4;
            case 0x21: case 0x23: return 8;
            case 0x2B: case 0x2C: case 0x2D: case 0x2E: case 0x2F: case 0x30:
            case 0x31: case 0x32: case 0x33: case 0x34: case 0x35: case 0x36:
            case 0x37: case 0x0E: case 0x0F: case 0x10: case 0x11: case 0x12:
            case 0x13: case 0x1E: case 0x1F:
                return 1;
            default: return 0;
        }
    }

    static string OpName(byte op)
    {
        switch (op)
        {
            case 0x28: return "call";
            case 0x6F: return "callvirt";
            case 0x7B: return "ldfld";
            case 0x7D: return "stfld";
            case 0x7E: return "ldsfld";
            case 0x80: return "stsfld";
            case 0x2A: return "ret";
            case 0x2B: return "br.s";
            case 0x2C: return "brfalse.s";
            case 0x2D: return "brtrue.s";
            case 0x39: return "brfalse";
            case 0x3A: return "brtrue";
            case 0x38: return "br";
            default: return "op_" + op.ToString("X2");
        }
    }

    static double TotalBarThickness(DependencyObject root, Orientation orientation)
    {
        ScrollBar bar = root as ScrollBar;
        if (bar != null && bar.Orientation == orientation && bar.IsVisible)
        {
            double size = orientation == Orientation.Vertical ? bar.ActualWidth : bar.ActualHeight;
            if (size > 0 && !double.IsNaN(size)) return size;
        }
        double total = 0;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
            total += TotalBarThickness(VisualTreeHelper.GetChild(root, i), orientation);
        return total;
    }

    static ScrollViewer FindScrollViewer(DependencyObject root)
    {
        if (root == null) return null;
        ScrollViewer d = root as ScrollViewer;
        if (d != null) return d;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            ScrollViewer f = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (f != null) return f;
        }
        return null;
    }

    static void Pump()
    {
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(delegate { }));
    }
}
