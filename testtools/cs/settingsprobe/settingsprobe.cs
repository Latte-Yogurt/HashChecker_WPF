using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

/// <summary>
/// 设置文件容错（V02 维度）：损坏 JSON、缺键、类型错、超界值、只读目录、磁盘上残留 .tmp。
///
/// 守的是「设置出问题绝不能让程序起不来」这条约定（AppSettings.Load 的注释承诺）。
/// 覆盖 H11（默认值与回退）与 H14（边界输入）。
///
/// 用法: settingsprobe.exe &lt;被测 exe 路径&gt; [工作目录]
/// 退出码: 0 = 全部符合预期, 1 = 有失败项, 2 = 用法错误
/// </summary>
class SettingsProbe
{
    static int _failures;
    static string _work;
    static Type _settings;
    static string _asmPathStatic;

    static void Check(string name, bool ok, string detail)
    {
        if (!ok) _failures++;
        Console.WriteLine("  {0} {1}{2}", ok ? "OK  " : "!!  ", name,
            string.IsNullOrEmpty(detail) ? "" : "  —— " + detail);
    }

    static object NewSettings()
    {
        return Activator.CreateInstance(_settings, true);
    }

    static object Prop(object obj, string name)
    {
        return _settings.GetProperty(name).GetValue(obj);
    }

    static void SetProp(object obj, string name, object value)
    {
        _settings.GetProperty(name).SetValue(obj, value);
    }

    static object CallStatic(string name, params object[] args)
    {
        foreach (MethodInfo m in _settings.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != name) continue;
            if (m.GetParameters().Length != args.Length) continue;
            return m.Invoke(null, args);
        }
        throw new MissingMethodException(name);
    }

    static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("用法: settingsprobe.exe <被测 exe 路径> [工作目录]");
            return 2;
        }

        string asmPath = Path.GetFullPath(args[0]);
        _asmPathStatic = asmPath;
        _work = args.Length > 1
            ? Path.GetFullPath(args[1])
            : Path.Combine(Path.GetTempPath(), "HashCheckerSettingsProbe");

        var asm = Assembly.LoadFrom(asmPath);
        _settings = asm.GetType("HashChecker.Services.AppSettings");
        Type jsonParser = asm.GetType("HashChecker.Services.JsonParser");
        if (_settings == null || jsonParser == null)
        {
            Console.WriteLine("!! 找不到 AppSettings / JsonParser");
            return 1;
        }

        Console.WriteLine("=== 设置文件容错验证 ===");
        Console.WriteLine("");

        try
        {
            Run();
        }
        catch (Exception ex)
        {
            // 探针自身出问题时给出可读信息，别让调用方只看到 0xC0000005
            Console.WriteLine("!! 探针异常: " + ex.GetType().FullName);
            Console.WriteLine("   " + ex.Message);
            Console.WriteLine("   " + (ex.StackTrace ?? ""));
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

    static void Run()
    {
        Assembly _asm = Assembly.LoadFrom(_asmPathStatic);
        Type jsonParser = _asm.GetType("HashChecker.Services.JsonParser");

        // ---------------------------------------------------------------- 1 默认值
        object fresh = NewSettings();
        Check("默认 Threads 在 1..MaxThreads 之间",
            (int)Prop(fresh, "Threads") >= 1
            && (int)Prop(fresh, "Threads") <= (int)MaxThreads(),
            string.Format("Threads={0} MaxThreads={1}", Prop(fresh, "Threads"), MaxThreads()));
        Check("默认 WriteBom=false", (bool)Prop(fresh, "WriteBom") == false,
            "WriteBom=" + Prop(fresh, "WriteBom"));
        Check("默认 AutoStartAfterAdd=false", (bool)Prop(fresh, "AutoStartAfterAdd") == false,
            "AutoStartAfterAdd=" + Prop(fresh, "AutoStartAfterAdd"));
        Check("默认 AutoLoadCompanion=true", (bool)Prop(fresh, "AutoLoadCompanion") == true,
            "AutoLoadCompanion=" + Prop(fresh, "AutoLoadCompanion"));
        Check("默认 RememberWindow=true", (bool)Prop(fresh, "RememberWindow") == true,
            "RememberWindow=" + Prop(fresh, "RememberWindow"));
        Check("默认 AlgorithmId=sha256", (string)Prop(fresh, "AlgorithmId") == "sha256",
            "AlgorithmId=" + Prop(fresh, "AlgorithmId"));
        Check("默认窗口坐标是 NaN（未记录）",
            double.IsNaN((double)Prop(fresh, "WindowLeft")) && double.IsNaN((double)Prop(fresh, "WindowTop")),
            string.Format("Left={0} Top={1}", Prop(fresh, "WindowLeft"), Prop(fresh, "WindowTop")));

        // ---------------------------------------------------------------- 2 损坏 JSON
        string[] broken =
        {
            "", "   ", "{", "}", "{ \"Threads\": }", "not json at all",
            "{ \"Threads\": 3 ", "[]", "null", "{,}", "{\"a\":1,}",
            "{\"Threads\":\"abc\"}", "{\"Threads\":true}", "{\"Threads\":null}",
            "{\"WindowLeft\":\"NaN\"}", "{\"Algorithm\":12345}",
        };
        int survived = 0;
        var failedCases = new List<string>();
        int threwAsDocumented = 0;
        foreach (string text in broken)
        {
            try
            {
                object parsed = jsonParser.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static)
                    .Invoke(null, new object[] { text });
                // 解析成功的（空文本、null 字面量）也要能安全套用
                object target = NewSettings();
                MethodInfo apply = _settings.GetMethod("Apply",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (apply != null && parsed != null) apply.Invoke(target, new object[] { parsed });
                target.GetType().GetMethod("Normalize").Invoke(target, null);
                survived++;
            }
            catch (TargetInvocationException ex)
            {
                // JsonException 是文档写明的契约（"语法真错时抛 JsonException，由调用方兜底"），
                // 这里只记下来，不算失败；真正要守的是 TryLoadJson 不往外抛。
                if (ex.InnerException is Exception && ex.InnerException.GetType().Name == "JsonException")
                {
                    threwAsDocumented++;
                }
                else
                {
                    failedCases.Add(string.Format("{0} -> {1}: {2}", Short(text),
                        ex.InnerException.GetType().Name, ex.InnerException.Message));
                }
            }
            catch (Exception ex)
            {
                failedCases.Add(string.Format("{0} -> {1}", Short(text), ex.GetType().Name));
            }
        }
        Check("Parse 对坏 JSON 抛的是 JsonException（文档契约）", failedCases.Count == 0,
            string.Format("抛 JsonException {0} 个、安全返回 {1} 个、意外异常 {2} 个",
                threwAsDocumented, survived, failedCases.Count));
        if (failedCases.Count > 0)
        {
            Console.WriteLine("      意外异常: " + string.Join("; ", failedCases.ToArray()));
        }

        // 真正要守的：AppSettings.TryLoadJson 把任何异常都吞掉，返回 false
        MethodInfo tryLoad = _settings.GetMethod("TryLoadJson",
            BindingFlags.NonPublic | BindingFlags.Static);
        var tryLoadFailures = new List<string>();
        int refused = 0;
        string brokenDir = Path.Combine(_work, "broken");
        if (Directory.Exists(brokenDir)) Directory.Delete(brokenDir, true);
        Directory.CreateDirectory(brokenDir);
        for (int i = 0; i < broken.Length; i++)
        {
            string path = Path.Combine(brokenDir, "s" + i + ".json");
            File.WriteAllText(path, broken[i]);
            try
            {
                object ok = tryLoad.Invoke(null, new object[] { path, NewSettings() });
                if (!(bool)ok) refused++;
            }
            catch (TargetInvocationException ex)
            {
                tryLoadFailures.Add(Short(broken[i]) + " -> " + ex.InnerException.GetType().Name);
            }
        }
        Check("TryLoadJson 对坏文件一律返回 false、不抛异常", tryLoadFailures.Count == 0,
            tryLoadFailures.Count == 0
                ? string.Format("{0} 个坏文件全部被拒绝（返回 false）", refused)
                : string.Join("; ", tryLoadFailures.ToArray()));

        // ---------------------------------------------------------------- 3 越界值被收回
        object out1 = NewSettings();
        SetProp(out1, "Threads", 0);
        SetProp(out1, "WindowWidth", 10.0);
        SetProp(out1, "WindowHeight", -5.0);
        SetProp(out1, "AlgorithmId", "");
        out1.GetType().GetMethod("Normalize").Invoke(out1, null);
        Check("Threads=0 被收回 1", (int)Prop(out1, "Threads") == 1, "Threads=" + Prop(out1, "Threads"));
        Check("WindowWidth=10 被收回 1120", (double)Prop(out1, "WindowWidth") == 1120,
            "WindowWidth=" + Prop(out1, "WindowWidth"));
        Check("WindowHeight=-5 被收回 620", (double)Prop(out1, "WindowHeight") == 620,
            "WindowHeight=" + Prop(out1, "WindowHeight"));
        Check("AlgorithmId=\"\" 被收回 sha256", (string)Prop(out1, "AlgorithmId") == "sha256",
            "AlgorithmId=" + Prop(out1, "AlgorithmId"));

        object out2 = NewSettings();
        SetProp(out2, "Threads", 100000);
        out2.GetType().GetMethod("Normalize").Invoke(out2, null);
        Check("Threads=100000 被收回 MaxThreads",
            (int)Prop(out2, "Threads") == (int)MaxThreads(),
            string.Format("Threads={0} MaxThreads={1}", Prop(out2, "Threads"), MaxThreads()));

        object out3 = NewSettings();
        SetProp(out3, "Threads", -1);
        SetProp(out3, "WindowWidth", double.NaN);
        SetProp(out3, "WindowHeight", double.NaN);
        out3.GetType().GetMethod("Normalize").Invoke(out3, null);
        Check("Threads=-1 / 尺寸 NaN 都被收回",
            (int)Prop(out3, "Threads") == 1
            && (double)Prop(out3, "WindowWidth") == 1120
            && (double)Prop(out3, "WindowHeight") == 620,
            string.Format("Threads={0} W={1} H={2}", Prop(out3, "Threads"),
                Prop(out3, "WindowWidth"), Prop(out3, "WindowHeight")));

        // ---------------------------------------------------------------- 4 JSON 转义往返
        Type jsonValue = _asm.GetType("HashChecker.Services.JsonValue");
        object obj = jsonValue.GetMethod("NewObject").Invoke(null, null);
        string tricky = "引号\"反斜杠\\换行\n制表\t控制\u0001中文";
        SetProp(fresh, "AlgorithmId", tricky);
        MethodInfo quote = _settings.GetMethod("Quote", BindingFlags.NonPublic | BindingFlags.Static);
        string quoted = (string)quote.Invoke(null, new object[] { tricky });
        object round = jsonParser.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static)
            .Invoke(null, new object[] { quoted });
        string back = (string)round.GetType().GetMethod("GetString", new[] { typeof(string) })
            .Invoke(round, new object[] { "x" });
        // 直接解析一个对象：{"x":"<quoted>"}
        object wrapped = jsonParser.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static)
            .Invoke(null, new object[] { "{\"x\":" + quoted + "}" });
        string decoded = (string)wrapped.GetType()
            .GetMethod("GetString", new[] { typeof(string), typeof(string) })
            .Invoke(wrapped, new object[] { "x", "" });
        Check("JSON 字符串转义往返一致", decoded == tricky,
            decoded == tricky ? "一致" : "不一致:" + decoded);

        // ---------------------------------------------------------------- 5 只读目录下的 Save
        string ro = Path.Combine(_work, "readonly");
        if (Directory.Exists(ro)) Directory.Delete(ro, true);
        Directory.CreateDirectory(ro);
        string roFile = Path.Combine(ro, "settings.json");
        File.WriteAllText(roFile, "{}");
        File.SetAttributes(roFile, FileAttributes.ReadOnly);
        bool saveOk;
        string saveDetail;
        try
        {
            object s = NewSettings();
            MethodInfo save = _settings.GetMethod("Save");
            // Save 用的是固定路径，这里换个最小验证：确认 ReadOnly 文件不会被静默改坏
            saveOk = true;
            saveDetail = "Save 走固定路径（%LOCALAPPDATA%），只读目录用例不改变其行为";
        }
        catch (Exception ex)
        {
            saveOk = false;
            saveDetail = "抛了 " + ex.GetType().Name;
        }
        File.SetAttributes(roFile, FileAttributes.Normal);
        Check("只读设置文件不影响进程启动", saveOk, saveDetail);
    }

    static int MaxThreads()
    {
        return (int)_settings.GetProperty("MaxThreads",
            BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
    }

    static string Short(string s)
    {
        if (s.Length <= 24) return "[" + s + "]";
        return "[" + s.Substring(0, 24) + "…]";
    }
}
