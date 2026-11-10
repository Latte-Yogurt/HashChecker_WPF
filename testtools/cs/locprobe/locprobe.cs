using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Diagnostics;
using System.Reflection;

/// <summary>
/// 本地化词典的**运行时**验证（与静态的 localization_check.py / locargs.py 互为交叉手段）。
///
/// 覆盖两点静态工具看不到的东西：
///   1. 三语词典在运行时展开后键集合是否真的一致、值是否非空、有没有裸键名；
///   2. Format 的运行时契约：占位符替换是否真的发生、少传参数是否按契约抛 FormatException、
///      缺失键是否回退成键名而不是抛异常。
///
/// 探针进程启动时把 UI 语言设成 fr-FR（非支持语言），因此还能验证「回退到 en-US」这条设计。
///
/// 用法: locprobe.exe &lt;HashChecker.exe&gt; [工作目录]
/// 退出码: 0 = 全部通过, 1 = 有失败项, 2 = 环境不满足
/// </summary>
class LocProbe
{
    static int _failures;
    static string _section = "all";

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
            Console.WriteLine("用法: locprobe.exe <HashChecker.exe> [工作目录] [--section all|v07|v08|v10]");
            return 2;
        }
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--section") _section = args[i + 1];
        }

        // 必须在触碰 Localization 类型之前改语言：_currentLanguage 是 static readonly，只算一次。
        try { CultureInfo.CurrentUICulture = new CultureInfo("fr-FR"); }
        catch { }

        string asmPath = Path.GetFullPath(args[0]);
        Assembly asm;
        try { asm = Assembly.LoadFrom(asmPath); }
        catch (Exception e)
        {
            Console.WriteLine("!! 加载程序集失败: " + e.Message);
            return 2;
        }

        Type loc = asm.GetType("HashChecker.Localization");
        if (loc == null)
        {
            Console.WriteLine("!! 找不到 HashChecker.Localization");
            return 2;
        }

        Console.WriteLine("=== 本地化词典运行时验证（UI 语言 = fr-FR，非支持语言）===");
        Console.WriteLine("被测程序: " + asmPath);
        Console.WriteLine("");

        var texts = ReadTexts(loc);
        if (texts == null)
        {
            Console.WriteLine("!! 反射取不到三语词典 Texts");
            return 2;
        }

        // ---- V07 词典运行时完整性 ----
        if (_section == "all" || _section == "v07")
        {
        Check("词典包含 3 种语言", texts.Count == 3, "实际 = " + texts.Count + "（" + Join(texts.Keys) + "）");

        var keySets = new List<HashSet<string>>();
        foreach (KeyValuePair<string, Dictionary<string, string>> kv in texts)
            keySets.Add(new HashSet<string>(kv.Value.Keys));

        bool same = true;
        string diff = "";
        for (int i = 1; i < keySets.Count; i++)
        {
            var onlyA = new HashSet<string>(keySets[0]);
            onlyA.ExceptWith(keySets[i]);
            var onlyB = new HashSet<string>(keySets[i]);
            onlyB.ExceptWith(keySets[0]);
            if (onlyA.Count > 0 || onlyB.Count > 0)
            {
                same = false;
                diff += "[" + onlyA.Count + "/" + onlyB.Count + "]";
            }
        }
        Check("三种语言键集合完全一致", same, same ? "" : "差集 = " + diff);

        int empty = 0, bare = 0;
        foreach (KeyValuePair<string, Dictionary<string, string>> kv in texts)
        {
            foreach (KeyValuePair<string, string> t in kv.Value)
            {
                if (string.IsNullOrEmpty(t.Value)) empty++;
                if (t.Value == t.Key) bare++;
            }
        }
        Check("没有空文本", empty == 0, "空值条数 = " + empty);
        Check("没有「值等于键名」的裸键", bare == 0, "裸键条数 = " + bare);

        string current = ReadStaticString(loc, "_currentLanguage");
        Check("非支持语言回退到 en-US", current == "en-US", "_currentLanguage = " + current);

        string menu = (string)loc.GetMethod("Get", BindingFlags.Public | BindingFlags.Static)
            .Invoke(null, new object[] { "MenuFile" });
        Check("Get 在 fr-FR 下返回英文文本（非空且非键名）",
            !string.IsNullOrEmpty(menu) && menu != "MenuFile", "MenuFile = " + menu);

        string missing = (string)loc.GetMethod("Get", BindingFlags.Public | BindingFlags.Static)
            .Invoke(null, new object[] { "__no_such_key__" });
        Check("缺失键回退成键名本身", missing == "__no_such_key__", "实际 = " + missing);
        }

        Console.WriteLine("");
        if (_section == "all" || _section == "v08")
        {
        Console.WriteLine("  --- Format 运行时契约 ---");

        // ---- V08 Format 契约 ----
        MethodInfo format = loc.GetMethod("Format", BindingFlags.Public | BindingFlags.Static);
        if (format == null)
        {
            Console.WriteLine("!! 找不到 Localization.Format");
            return 2;
        }

        string body = Invoke(format, "AboutBody", new object[] { "9.9.9" });
        Check("Format 真的做了占位符替换", body != null && body.Contains("9.9.9"), "结果 = " + body);

        bool threw = false;
        try { Invoke(format, "AboutBody", new object[0]); }
        catch (TargetInvocationException e)
        {
            threw = (e.InnerException is FormatException);
        }
        Check("少传参数按契约抛 FormatException", threw, "是否抛 FormatException = " + threw);

        bool threwMissing = false;
        try
        {
            string r = Invoke(format, "__no_such_key__", new object[0]);
            if (r != "__no_such_key__") threwMissing = true;
        }
        catch { threwMissing = true; }
        Check("缺失键走 Format 不抛异常", !threwMissing, "");

        // 带占位符的键：按 {n} 个数补齐参数后必须能格式化
        int checkedKeys = 0, badKeys = 0;
        foreach (KeyValuePair<string, Dictionary<string, string>> kv in texts)
        {
            foreach (KeyValuePair<string, string> t in kv.Value)
            {
                int n = CountPlaceholders(t.Value);
                if (n == 0) continue;
                checkedKeys++;
                var filler = new object[n];
                for (int i = 0; i < n; i++) filler[i] = "x" + i;
                try { Invoke(format, t.Key, filler); }
                catch { badKeys++; }
            }
            break; // 第一语言即可，三语键集合已断言一致
        }
        Check("带占位符的键按占位符个数格式化均成功",
            badKeys == 0, "检查 " + checkedKeys + " 个键，失败 " + badKeys + " 个");
        }

        if (_section == "all" || _section == "v10")
        {
            // ---- V10 发布元数据：程序集版本 ----
            Console.WriteLine("");
            Console.WriteLine("  --- 程序集版本 ---");
            Version asmVer = asm.GetName().Version;
            Check("程序集版本不低于 1.1.0.0（本次功能版）",
                asmVer != null && asmVer >= new Version(1, 1, 0, 0),
                "实际 = " + asmVer);
            try
            {
                FileVersionInfo vi = FileVersionInfo.GetVersionInfo(asmPath);
                Check("文件版本与程序集版本一致",
                    vi.FileVersion == asmVer.ToString(),
                    "文件版本 = " + vi.FileVersion + "，程序集版本 = " + asmVer);
                Check("产品版本与程序集版本一致",
                    vi.ProductVersion == asmVer.ToString(),
                    "产品版本 = " + vi.ProductVersion);
            }
            catch (Exception e)
            {
                Check("读取文件版本信息", false, e.Message);
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

    static string Invoke(MethodInfo format, string key, object[] extra)
    {
        // Format(string key, params object[] args)：params 必须以「单个数组元素」传进去
        return (string)format.Invoke(null, new object[] { key, extra });
    }

    static int CountPlaceholders(string s)
    {
        int max = -1;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '{') continue;
            int j = s.IndexOf('}', i + 1);
            if (j < 0) break;
            int idx;
            if (int.TryParse(s.Substring(i + 1, j - i - 1), out idx))
            {
                if (idx > max) max = idx;
            }
            i = j;
        }
        return max + 1;
    }

    static Dictionary<string, Dictionary<string, string>> ReadTexts(Type loc)
    {
        FieldInfo f = loc.GetField("Texts", BindingFlags.NonPublic | BindingFlags.Static);
        if (f == null) return null;
        return f.GetValue(null) as Dictionary<string, Dictionary<string, string>>;
    }

    static string ReadStaticString(Type loc, string name)
    {
        FieldInfo f = loc.GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
        return f == null ? null : (string)f.GetValue(null);
    }

    static string Join(IEnumerable<string> items)
    {
        string s = "";
        foreach (string i in items) s += (s.Length == 0 ? "" : ",") + i;
        return s;
    }
}
