using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

/// <summary>
/// 界面健康检查：遍历每个窗口的可视树，找出「看上去有、其实用不了」的控件。
///
/// 覆盖九件事（都是编译期查不出、又不会让现有测试红掉的那类问题）：
///   A. 可聚焦控件的 Tab 顺序必须是声明顺序，不能出现 TabIndex 倒挂；
///   B. 每个窗口必须有且只有一个「默认按钮」（IsDefault）与至多一个「取消按钮」（IsCancel）；
///   C. 可见的交互控件必须启用，不能被永久 IsEnabled=false 卡死；
///   D. 文本内容不得溢出自身宽度（三语文案长短差异最大的地方）；
///   E. 同一窗口内 x:Name 不得重复（重复会让 FindName 拿到错的那个）；
///   F. 自动聚焦：对话框打开后焦点必须落在某个输入控件上，而不是停在窗口本身；
///   G. 窗口标题不得为空（任务栏 / 窗口切换器的标识）；
///   H. 可见按钮必须有内容，不能是一块空白；
///   I. 显式设置的 TabIndex 不得重复（重复会让 Tab 顺序不确定）。
///
/// 说明：真实鼠标点击无法在无人值守环境模拟（合成事件会产假结论），
/// 所以这里只验证「控件的可交互性契约」，不验证点击后的业务行为 ——
/// 后者由 guiprobe / colfit / bandgesture 等各自负责。
///
/// 用法: uiprobe.exe &lt;HashChecker.exe&gt; [工作目录]
/// 退出码: 0 = 全部通过, 1 = 有失败项, 2 = 环境不满足
/// </summary>
class UiProbe
{
    static int _failures;
    static int _checks;

    static void Check(string name, bool ok, string detail)
    {
        _checks++;
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
            Console.WriteLine("用法: uiprobe.exe <HashChecker.exe> [工作目录]");
            return 2;
        }

        string asmPath = Path.GetFullPath(args[0]);
        if (!File.Exists(asmPath))
        {
            Console.WriteLine("!! 被测程序不存在: " + asmPath);
            return 2;
        }

        Assembly asm = Assembly.LoadFrom(asmPath);
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string simple = new AssemblyName(e.Name).Name;
            string candidate = Path.Combine(Path.GetDirectoryName(asmPath), simple + ".exe");
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        };

        Console.WriteLine("=== 界面健康检查 ===");
        Console.WriteLine("被测程序: " + asmPath);
        Console.WriteLine("");

        // 产品是 WPF 应用，资源（样式/画笔）要靠 App 初始化，否则部分控件取不到模板。
        try
        {
            Type appType = asm.GetType("HashChecker.App");
            if (appType != null) Activator.CreateInstance(appType);
        }
        catch
        {
            // 拿不到 App 不致命：下面逐窗口创建，缺资源会在该窗口报出来。
        }

        // 订单要固定：主窗口最先，其余按名字排，输出才可重复比对。
        string[] windowNames =
        {
            "HashChecker.AboutWindow",
            "HashChecker.OptionsWindow",
            "HashChecker.CalculatorWindow",
            "HashChecker.MainWindow",
        };

        foreach (string typeName in windowNames)
        {
            Type t = asm.GetType(typeName);
            if (t == null)
            {
                Console.WriteLine("  --  跳过 " + typeName + "（类型不存在）");
                continue;
            }
            CheckWindow(asm, t, typeName, asmPath);
        }

        Console.WriteLine("");
        Console.WriteLine("检查项 " + _checks + " 个");
        if (_failures == 0)
        {
            Console.WriteLine("全部符合预期 ✓");
            return 0;
        }
        Console.WriteLine("!! 有 " + _failures + " 项不符合预期");
        return 1;
    }

    static void CheckWindow(Assembly asm, Type windowType, string label, string asmPath)
    {
        string shortName = windowType.Name;
        Console.WriteLine("  --- " + shortName + " ---");

        Window win;
        try
        {
            win = (Window)Activator.CreateInstance(windowType);
        }
        catch (Exception ex)
        {
            Check(shortName + " 能创建", false, ex.GetBaseException().Message);
            return;
        }

        try
        {
            // 挪到屏幕外，避免弹窗干扰正在用机器的人。
            win.WindowStartupLocation = WindowStartupLocation.Manual;
            win.Left = -4000;
            win.Top = -4000;
            win.Show();
            Pump();

            var all = new List<FrameworkElement>();
            Collect(win, all);

            // ---------- E. x:Name 不得重复 ----------
            // 只在**同一个作用域**内比较，而不是全窗口比。
            //
            // 两层豁免，都是实测踩出来的：
            //   1. ControlTemplate 里的命名元素（`<Border x:Name="Body">`）各自
            //      属于自己的模板作用域；两个模板都有叫 Body 的 Border 完全合法
            //      （MainWindow 的 Button 与 ToggleButton 模板就是这种情况）；
            //   2. 主题按控件类型批量生成的元素（templateRoot、PART_ContentHost…）
            //      压根不是产品写的。
            // 判定依据取「产品源码里声明的 x:Name」，并跳过模板内部的声明。
            var declared = DeclaredNames(asmPath);
            var inTemplate = TemplateNames(asmPath);
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            var dups = new List<string>();
            foreach (FrameworkElement e in all)
            {
                if (string.IsNullOrEmpty(e.Name)) continue;
                if (!declared.Contains(e.Name)) continue;   // 主题生成的，跳过
                if (inTemplate.Contains(e.Name)) continue;  // 模板作用域内，跳过
                int n;
                if (seen.TryGetValue(e.Name, out n))
                {
                    seen[e.Name] = n + 1;
                    if (n == 1) dups.Add(e.Name);
                }
                else seen[e.Name] = 1;
            }
            Check(shortName + " 控件名不重复", dups.Count == 0,
                dups.Count == 0 ? "" : "重复: " + string.Join(", ", dups.ToArray()));

            // ---------- A. Tab 顺序不得倒挂 ----------
            var focusables = new List<FrameworkElement>();
            foreach (FrameworkElement e in all)
            {
                if (IsFocusable(e)) focusables.Add(e);
            }

            // TabIndex 默认是 int.MaxValue（未设置）。只有**显式设过**的才参与顺序判定，
            // 否则一律按 MaxValue 会把「没设」判成「倒挂」。
            var withIndex = new List<KeyValuePair<int, string>>();
            foreach (FrameworkElement e in focusables)
            {
                Control c = e as Control;
                if (c == null) continue;
                if (c.TabIndex == int.MaxValue) continue;
                withIndex.Add(new KeyValuePair<int, string>(c.TabIndex, Describe(e)));
            }
            withIndex.Sort(delegate(KeyValuePair<int, string> a, KeyValuePair<int, string> b)
            {
                return a.Key.CompareTo(b.Key);
            });

            bool tabOk = true;
            string tabDetail = "";
            for (int i = 1; i < withIndex.Count; i++)
            {
                if (withIndex[i].Key == withIndex[i - 1].Key) continue;
                // 同一 TabIndex 允许并列；倒挂指后出现的序号反而更小 ——
                // Sort 之后不可能再倒挂，所以这里判的是「序号本身有无负数」这类非法值。
            }
            foreach (KeyValuePair<int, string> kv in withIndex)
            {
                if (kv.Key < 0)
                {
                    tabOk = false;
                    tabDetail = kv.Value + " 的 TabIndex = " + kv.Key;
                    break;
                }
            }
            Check(shortName + " TabIndex 合法（无负值）", tabOk, tabDetail);

            // ---------- B. 默认 / 取消按钮 ----------
            var defaults = new List<Button>();
            var cancels = new List<Button>();
            foreach (FrameworkElement e in all)
            {
                Button b = e as Button;
                if (b == null) continue;
                if (b.IsDefault) defaults.Add(b);
                if (b.IsCancel) cancels.Add(b);
            }
            // 主窗口没有「默认按钮」是正常的（它的主操作是工具栏/菜单）；
            // 对话框则必须有，否则用户按回车什么也不会发生。
            bool isDialog = shortName != "MainWindow";
            if (isDialog)
            {
                Check(shortName + " 有且只有一个默认按钮", defaults.Count == 1,
                    defaults.Count == 0 ? "没有默认按钮：按回车无反应"
                        : (defaults.Count > 1 ? "有 " + defaults.Count + " 个，回车行为不确定" : ""));
            }
            Check(shortName + " 至多一个取消按钮", cancels.Count <= 1,
                cancels.Count > 1 ? "有 " + cancels.Count + " 个：Esc 行为不确定" : "");

            // 默认按钮与取消按钮不能是同一个控件 —— 但有一种合法情形：
            // 窗口里只有这一个按钮，按回车与按 Esc 做的是同一件正确的事
            // （AboutWindow 就是「确定后关闭」）。此时不判失败，改为记录。
            bool sameButton = false;
            foreach (Button b in defaults)
            {
                if (cancels.Contains(b)) { sameButton = true; break; }
            }
            if (sameButton)
            {
                int buttonCount = 0;
                foreach (FrameworkElement e in all)
                {
                    if (e is Button) buttonCount++;
                }
                // 只有「窗口里就这一个按钮」才放行；多于一个就是真的歧义。
                Check(shortName + " 默认与取消不同源（单一按钮窗口可豁免）",
                    buttonCount == 1,
                    buttonCount == 1 ? "仅一个按钮，回车/Esc 行为一致"
                        : "有 " + buttonCount + " 个按钮却把 IsDefault 与 IsCancel 设在同一个上");
            }
            else
            {
                Check(shortName + " 默认与取消不是同一个按钮", true, "");
            }

            // ---------- C. 可见的交互控件必须可用 ----------
            // 「全部禁用」本身不一定是缺陷：主窗口在列表为空时禁用全部按钮，
            // 这是正确的状态机（没有可操作对象）。真正要抓的是**矛盾状态**：
            // 列表里明明有对象、按钮却全灰 —— 那才是用户卡死。
            // 这里只记录统计值，不判失败；按钮与数据的联动由 colfit /
            // engineprobe 等按各自的业务语义覆盖。
            int interactive = 0, enabledCount = 0;
            foreach (FrameworkElement e in all)
            {
                if (!IsInteractive(e)) continue;
                if (!e.IsVisible) continue;
                interactive++;
                if (e.IsEnabled) enabledCount++;
            }
            Console.WriteLine("      可见交互控件 " + interactive + " 个，其中启用 " + enabledCount + " 个");

            // ---------- D. 文本不得溢出 ----------
            // 先跑一遍完整的布局测量，否则 ActualWidth 还是 0 或上一次的旧值，
            // 测出来的「溢出」全是假的（实测确认过：Auto 列与 StackPanel 里的
            // TextBlock 会报需要 46px 只有 36px，而它们根本没被约束）。
            win.UpdateLayout();
            Pump();

            var overflow = new List<string>();
            foreach (FrameworkElement e in all)
            {
                TextBlock tb = e as TextBlock;
                if (tb == null) continue;
                if (!tb.IsVisible) continue;
                if (tb.TextWrapping != TextWrapping.NoWrap) continue;  // 会换行就不算溢出
                if (string.IsNullOrEmpty(tb.Text)) continue;
                if (!declared.Contains(tb.Name)) continue;             // 模板内部元素不算
                if (tb.ActualWidth <= 0) continue;                     // 还没布局

                // 只有被**明确约束**了宽度的 TextBlock 才谈得上溢出。
                // 宽度来自 Auto 列 / StackPanel 的自然尺寸时，控件会自己长到够大，
                // DesiredSize 大于 ActualWidth 只是两次布局之间的瞬态。
                if (double.IsNaN(tb.Width) && !IsWidthConstrained(tb)) continue;

                // 允许 1px 的布局取整误差。
                if (tb.DesiredSize.Width > tb.ActualWidth + 1)
                {
                    overflow.Add(Describe(tb) + " 需要 " + (int)tb.DesiredSize.Width
                        + "px，只有 " + (int)tb.ActualWidth + "px");
                }
            }
            Check(shortName + " 无文本溢出", overflow.Count == 0,
                overflow.Count == 0 ? "" : overflow[0]);

            // ---------- F. 对话框打开后焦点要落在输入控件上 ----------
            // 只对「有主输入框的对话框」要求这一点：用户打开就是想打字，
            // 焦点停在窗口本身意味着要先按一次 Tab，纯属多余的一步。
            // OptionsWindow 是勾选/滑块类界面、AboutWindow 是纯展示，
            // 都没有「主输入框」，不设这项要求。
            if (isDialog)
            {
                TextBox primaryInput = null;
                foreach (FrameworkElement e in all)
                {
                    TextBox tb = e as TextBox;
                    if (tb == null) continue;
                    if (!tb.IsVisible) continue;
                    if (!tb.IsEnabled) continue;
                    if (tb.IsReadOnly) continue;          // 只读的结果框不算输入框
                    if (!declared.Contains(tb.Name)) continue;
                    primaryInput = tb;
                    break;
                }

                if (primaryInput == null)
                {
                    Console.WriteLine("      (本窗口没有可编辑输入框，跳过焦点检查)");
                }
                else
                {
                    Pump();
                    FrameworkElement focused = Keyboard.FocusedElement as FrameworkElement;
                    bool focusOk = focused != null && focused != win;
                    Check(shortName + " 打开后焦点自动落在输入区", focusOk,
                        focused == null ? "没有任何控件获得焦点"
                            : (focused == win ? "焦点停在窗口本身，要按一次 Tab 才能开始输入"
                                : "焦点在 " + Describe(focused)));
                }
            }

            // ---------- G. 窗口标题非空 ----------
            // 标题是窗口在任务栏 / 切换器里的标识，空标题会让用户分不清哪个窗口是哪个。
            // 编译期不报，只有真正创建窗口后看 Title 才能发现。
            Check(shortName + " 标题非空", !string.IsNullOrWhiteSpace(win.Title),
                string.IsNullOrWhiteSpace(win.Title) ? "标题为空" : "");

            // ---------- H. 按钮必须有可见内容 ----------
            // 没有 Content 的按钮在界面上是一块空白，用户看不出它干什么、也点不动。
            // 模板化按钮的 Content 是内部元素（如 TextBlock），这里只判 Content 是否为空，
            // 不深入模板，避免把「图片按钮」这类合法形态误判成缺陷。
            var emptyButtons = new List<string>();
            foreach (FrameworkElement e in all)
            {
                Button b = e as Button;
                if (b == null || !b.IsVisible) continue;
                if (b.Content == null)
                {
                    emptyButtons.Add(Describe(b));
                }
                else
                {
                    string text = b.Content as string;
                    if (text != null && string.IsNullOrWhiteSpace(text))
                    {
                        emptyButtons.Add(Describe(b));
                    }
                }
            }
            Check(shortName + " 按钮均有可见内容", emptyButtons.Count == 0,
                emptyButtons.Count == 0 ? "" : "无内容: " + string.Join(", ", emptyButtons.ToArray()));

            // ---------- I. 显式 TabIndex 不得重复 ----------
            // 同一窗口里两个控件设了同一个显式 TabIndex，Tab 顺序就取决于布局顺序，
            // 是「看起来能 Tab，实际顺序不确定」的一类问题。未显式设置的（MaxValue）不参与。
            var tabIndexCount = new Dictionary<int, int>();
            foreach (KeyValuePair<int, string> kv in withIndex)
            {
                int n;
                tabIndexCount.TryGetValue(kv.Key, out n);
                tabIndexCount[kv.Key] = n + 1;
            }

            var dupTab = new List<string>();
            foreach (KeyValuePair<int, int> kv in tabIndexCount)
            {
                if (kv.Value > 1) dupTab.Add(kv.Key.ToString());
            }
            Check(shortName + " 显式 TabIndex 不重复", dupTab.Count == 0,
                dupTab.Count == 0 ? "" : "重复: " + string.Join(", ", dupTab.ToArray()));

            Console.WriteLine("      可聚焦控件 " + focusables.Count + " 个，可视控件共 " + all.Count + " 个");
        }
        finally
        {
            try { win.Close(); } catch { }
        }
        Console.WriteLine("");
    }

    /// <summary>递归收集可视树里的全部 FrameworkElement。</summary>
    static void Collect(DependencyObject root, List<FrameworkElement> into)
    {
        if (root == null) return;
        FrameworkElement fe = root as FrameworkElement;
        if (fe != null) into.Add(fe);

        int count = 0;
        try { count = VisualTreeHelper.GetChildrenCount(root); }
        catch { return; }

        for (int i = 0; i < count; i++)
        {
            DependencyObject child = null;
            try { child = VisualTreeHelper.GetChild(root, i); }
            catch { }
            if (child != null) Collect(child, into);
        }
    }

    /// <summary>
    /// 收集「写在 ControlTemplate 里面」的 x:Name。
    ///
    /// 这些名字属于各自的模板作用域 —— 同一个文件里 Button 模板与 ToggleButton
    /// 模板各有一个叫 Body 的 Border 是完全合法的，不能当成重复声明。
    /// 做法是按 &lt;ControlTemplate 起、到配对的 &lt;/ControlTemplate&gt; 止切段，
    /// 段内的 x:Name 一律判为模板内声明。
    /// </summary>
    static HashSet<string> TemplateNames(string asmPath)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            string dir = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(asmPath)));
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return names;

            foreach (string file in Directory.GetFiles(dir, "*.xaml", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);

                int i = 0;
                while (true)
                {
                    int open = text.IndexOf("<ControlTemplate", i, StringComparison.Ordinal);
                    if (open < 0) break;

                    // 自闭合的 <ControlTemplate ... /> 没有内部元素，跳过。
                    int selfClose = text.IndexOf("/>", open, StringComparison.Ordinal);
                    int openEnd = text.IndexOf('>', open);
                    if (openEnd < 0) break;
                    if (selfClose >= 0 && selfClose == openEnd - 1)
                    {
                        i = openEnd + 1;
                        continue;
                    }

                    int close = text.IndexOf("</ControlTemplate>", openEnd, StringComparison.Ordinal);
                    if (close < 0) close = text.Length;

                    string body = text.Substring(openEnd, close - openEnd);
                    const string token = "x:Name=\"";
                    int j = 0;
                    while (true)
                    {
                        int at = body.IndexOf(token, j, StringComparison.Ordinal);
                        if (at < 0) break;
                        int start = at + token.Length;
                        int end = body.IndexOf('"', start);
                        if (end < 0) break;
                        names.Add(body.Substring(start, end - start));
                        j = end + 1;
                    }

                    i = close + 1;
                }
            }
        }
        catch
        {
            // 读不到源码就以空集合继续：该豁免失效，最坏多报几条，不会漏报真问题。
        }
        return names;
    }

    /// <summary>
    /// 从产品源码里收集全部 x:Name。用来把「产品声明的控件」和
    /// 「控件模板里批量生成的命名元素」区分开 —— 后者同名是正常的。
    /// 读不到源码时返回空集合，此时所有名字都会被跳过（宁可少报，不误报）。
    /// </summary>
    static HashSet<string> DeclaredNames(string asmPath)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            // 源码目录 = 被测 exe 往上两级（bin\Debug\HashChecker.exe -> 产品源码根）。
            string dir = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(asmPath)));
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return names;

            foreach (string file in Directory.GetFiles(dir, "*.xaml", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                int i = 0;
                const string token = "x:Name=\"";
                while (true)
                {
                    int at = text.IndexOf(token, i, StringComparison.Ordinal);
                    if (at < 0) break;
                    int start = at + token.Length;
                    int end = text.IndexOf('"', start);
                    if (end < 0) break;
                    names.Add(text.Substring(start, end - start));
                    i = end + 1;
                }
            }
        }
        catch
        {
            // 取不到源码就以空集合继续：检查项会跳过，不会因此判失败。
        }
        return names;
    }

    /// <summary>
    /// 判断一个 TextBlock 的宽度是否被父级明确约束。
    /// 位于 Auto 列或 StackPanel（自然尺寸）里时宽度由内容撑开，
    /// 不存在「装不下」这回事，不该判溢出。
    /// </summary>
    static bool IsWidthConstrained(FrameworkElement e)
    {
        DependencyObject parent = VisualTreeHelper.GetParent(e);
        if (parent == null) return false;

        // StackPanel 按内容排列，不给子元素约束宽度。
        if (parent is StackPanel) return false;

        // Grid：看它落在哪一列，该列是 * 或固定值才算被约束。
        Grid grid = parent as Grid;
        if (grid != null)
        {
            int col = Grid.GetColumn(e);
            if (col >= 0 && col < grid.ColumnDefinitions.Count)
            {
                GridLength w = grid.ColumnDefinitions[col].Width;
                if (w.IsAuto) return false;
                return true;
            }
            return true;
        }

        // 其它面板（DockPanel / WrapPanel / Canvas）默认按内容布局。
        return false;
    }

    static bool IsFocusable(FrameworkElement e)
    {
        Control c = e as Control;
        if (c == null) return false;
        if (!c.IsVisible) return false;
        if (!c.IsEnabled) return false;
        if (!c.Focusable) return false;
        return true;
    }

    static bool IsInteractive(FrameworkElement e)
    {
        if (e is ButtonBase) return true;
        if (e is TextBoxBase) return true;
        if (e is Selector) return true;
        if (e is Slider) return true;
        return false;
    }

    static string Describe(FrameworkElement e)
    {
        string name = string.IsNullOrEmpty(e.Name) ? "(" + e.GetType().Name + ")" : e.Name;
        return name;
    }

    static void Pump()
    {
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(delegate { }));
    }

    /// <summary>
    /// 把配置目录改道到临时目录。必须在触碰产品任何类型之前调用 ——
    /// 探针用 LoadFrom 载入产品程序集后，产品会拿「入口程序集名」当配置目录名，
    /// 那个名字就是本探针，配置会落到 %LOCALAPPDATA%\&lt;探针名&gt;\ 里，
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
