using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

/// <summary>
/// 需求验证探针：
/// A. 拖入普通文件时，新行的算法必须跟随「当前下拉框选择」，不能锁死在 SHA-256；
/// B. 默认写出的校验文件必须是不带 BOM 的 UTF-8（兼容大多数工具）；
/// C. 带 BOM 的校验文件仍能被读回（宽容解码不能被 BOM 默认值改动破坏）。
///
/// 用法: defaultcheck.exe &lt;HashChecker.exe&gt; [工作目录]
/// 退出码: 0 = 全部通过, 1 = 有失败项, 2 = 用法错误
/// </summary>
class DefaultCheck
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
            Console.WriteLine("用法: defaultcheck.exe <HashChecker.exe> [工作目录]");
            return 2;
        }

        string asmPath = Path.GetFullPath(args[0]);
        _work = args.Length > 1
            ? Path.GetFullPath(args[1])
            : Path.Combine(Path.GetTempPath(), "HashCheckerDefaultCheck");

        if (Directory.Exists(_work)) Directory.Delete(_work, true);
        Directory.CreateDirectory(_work);

        _asm = Assembly.LoadFrom(asmPath);
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string simple = new AssemblyName(e.Name).Name;
            string candidate = Path.Combine(Path.GetDirectoryName(asmPath), simple + ".exe");
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        };

        Console.WriteLine("=== 默认值与算法同步验证 ===");
        Console.WriteLine("被测程序: " + asmPath);
        Console.WriteLine("");

        CheckBomDefault();
        CheckWritesNoBom();
        CheckReadsBomAnyway();
        CheckNoIniMigration();
        CheckAlgorithmFollowsPicker();

        Console.WriteLine("");
        if (_failures == 0)
        {
            Console.WriteLine("全部符合预期 ✓");
            return 0;
        }
        Console.WriteLine("!! 有 " + _failures + " 项不符合预期");
        return 1;
    }

    /// <summary>A. 默认配置里 WriteBom 必须是 false。</summary>
    static void CheckBomDefault()
    {
        Console.WriteLine("【WriteBom 默认值】");

        Type settings = _asm.GetType("HashChecker.Services.AppSettings");
        if (settings == null)
        {
            Check("找到 AppSettings", false, "");
            return;
        }

        object fresh = Activator.CreateInstance(settings);
        object value = settings.GetProperty("WriteBom").GetValue(fresh);
        Check("新建设置的 WriteBom 默认为 false", value is bool && !(bool)value, "实际=" + value);
    }

    /// <summary>B. 用 ChecksumFile.Write 默认参数写出，文件不应含 BOM 字节。</summary>
    static void CheckWritesNoBom()
    {
        Console.WriteLine("");
        Console.WriteLine("【默认写出的字节】");

        Type checksumFile = _asm.GetType("HashChecker.Services.ChecksumFile");
        if (checksumFile == null)
        {
            Check("找到 ChecksumFile", false, "");
            return;
        }

        MethodInfo write = checksumFile.GetMethod("Write", BindingFlags.Public | BindingFlags.Static,
            null, new[] { typeof(string), typeof(IEnumerable<string>), typeof(bool) }, null);

        if (write == null)
        {
            Check("找到 ChecksumFile.Write", false, "");
            return;
        }

        string path = Path.Combine(_work, "no-bom.sha256");
        var lines = new List<string> { "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef *payload.bin" };

        write.Invoke(null, new object[] { path, lines, false });

        byte[] bytes = File.ReadAllBytes(path);
        bool startsWithBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

        Check("Write(false) 不写 BOM", !startsWithBom,
            startsWithBom ? "文件以 EF BB BF 开头" : "首字节 0x" + bytes[0].ToString("X2"));

        // 带 BOM 的写法仍应可用（选项里能开）
        string withBom = Path.Combine(_work, "with-bom.sha256");
        write.Invoke(null, new object[] { withBom, lines, true });
        byte[] bomBytes = File.ReadAllBytes(withBom);
        bool hasBom = bomBytes.Length >= 3 && bomBytes[0] == 0xEF && bomBytes[1] == 0xBB && bomBytes[2] == 0xBF;
        Check("Write(true) 仍写 BOM（选项可开启）", hasBom, hasBom ? "" : "没有 BOM");
    }

    /// <summary>C. 带 BOM 的记录必须还能被解析——BOM 默认值改了，但读取宽容性不能退。</summary>
    static void CheckReadsBomAnyway()
    {
        Console.WriteLine("");
        Console.WriteLine("【BOM 记录的读取】");

        Type checksumFile = _asm.GetType("HashChecker.Services.ChecksumFile");
        MethodInfo parse = checksumFile.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static,
            null, new[] { typeof(string) }, null);

        if (parse == null)
        {
            Check("找到 ChecksumFile.Parse", false, "");
            return;
        }

        string digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        string path = Path.Combine(_work, "bom.sha256");

        // 手工写入带 BOM 的内容
        File.WriteAllText(path, digest + " *payload.bin\r\n", new UTF8Encoding(true));

        object document = parse.Invoke(null, new object[] { path });
        object entries = document == null ? null : GetProperty(document, "Entries");
        Array array = entries as Array;

        bool ok = array != null && array.Length == 1;
        string hash = ok ? (string)GetProperty(array.GetValue(0), "Hash") : null;

        Check("带 BOM 的记录解析出 1 条", ok, ok ? "" : "条目=" + (array == null ? 0 : array.Length));
        Check("BOM 后的值解析正确", hash != null && hash == digest, hash ?? "(null)");

        // 不带 BOM 的也应正常
        string plain = Path.Combine(_work, "plain.sha256");
        File.WriteAllText(plain, digest + " *payload.bin\r\n", new UTF8Encoding(false));
        object doc2 = parse.Invoke(null, new object[] { plain });
        object entries2 = doc2 == null ? null : GetProperty(doc2, "Entries");
        Array arr2 = entries2 as Array;
        Check("无 BOM 的记录解析出 1 条", arr2 != null && arr2.Length == 1,
            "条目=" + (arr2 == null ? 0 : arr2.Length));
    }

    /// <summary>
    /// E. INI 迁移已经整个移除：设置只认 settings.json。
    ///
    /// 这条断言守的是「别把兼容代码又加回来」。旧 ini 迁移是上一版的兼容层，
    /// 现在产品已不再需要，留着它只会让人误以为还支持 ini。
    /// 三件事：迁移用的公开接口必须不存在；存在 ini 时不得被读取；
    /// 存在的 ini 也不该被程序偷偷删掉（程序根本不该碰它）。
    /// </summary>
    static void CheckNoIniMigration()
    {
        Console.WriteLine("");
        Console.WriteLine("【INI 迁移已移除】");

        Type settings = _asm.GetType("HashChecker.Services.AppSettings");
        if (settings == null)
        {
            Check("找到 AppSettings", false, "");
            return;
        }

        // 1. 迁移相关成员必须已经不存在
        bool legacyPathGone = settings.GetProperty("LegacyPath",
            BindingFlags.Public | BindingFlags.Static) == null;
        Check("公开的 LegacyPath 已移除", legacyPathGone, legacyPathGone ? "" : "仍存在");

        bool legacyIniGone = settings.GetMethod("ApplyLegacyIni",
            BindingFlags.NonPublic | BindingFlags.Instance) == null;
        Check("ApplyLegacyIni 已移除", legacyIniGone, legacyIniGone ? "" : "仍存在");

        // 2. 放一个 ini 在设置目录里，Load() 不该从它取值
        string folder = (string)settings.GetProperty("SettingsFolder",
            BindingFlags.Public | BindingFlags.Static).GetValue(null);

        Directory.CreateDirectory(folder);

        string iniPath = Path.Combine(folder, "settings.ini");
        File.WriteAllText(iniPath,
            "Threads=3\r\nUpperCase=true\r\nAutoStart=true\r\nAlgorithm=sha3-512\r\n",
            new UTF8Encoding(false));

        // 同时清掉 json，确保 ini 是唯一可能来源
        string jsonPath = (string)settings.GetProperty("SettingsPath",
            BindingFlags.Public | BindingFlags.Static).GetValue(null);
        if (File.Exists(jsonPath)) File.Delete(jsonPath);

        object loaded = settings.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)
            .Invoke(null, null);

        int threads = (int)GetProperty(loaded, "Threads");
        bool upperCase = (bool)GetProperty(loaded, "UpperCase");
        bool autoStart = (bool)GetProperty(loaded, "AutoStartAfterAdd");
        string algorithm = (string)GetProperty(loaded, "AlgorithmId");

        Check("ini 的 Threads=3 未被采用", threads != 3, "实际=" + threads);
        Check("ini 的 UpperCase=true 未被采用", !upperCase, "实际=" + upperCase);
        Check("ini 的 AutoStart=true 未被采用", !autoStart, "实际=" + autoStart);
        Check("ini 的 Algorithm=sha3-512 未被采用",
            !string.Equals(algorithm, "sha3-512", StringComparison.OrdinalIgnoreCase),
            "实际=" + algorithm);

        // 3. 程序不该顺手删掉这个 ini
        Check("程序没有擅自删除 ini", File.Exists(iniPath),
            File.Exists(iniPath) ? "" : "被删掉了");

        File.Delete(iniPath);

        // 收尾：恢复一份干净配置，避免影响后面的步骤与用户首启体验
        if (File.Exists(jsonPath)) File.Delete(jsonPath);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    /// <summary>
    /// D. 拖入普通文件时，新行的算法必须跟随下拉框。
    ///
    /// 做法：构造真实 MainWindow（不显示），把下拉框切到 SHA3-512，
    /// 再反射调 AddPaths 传入一个没有任何校验记录的文件，
    /// 断言那一行的算法是 sha3-512 而不是 sha256。
    /// </summary>
    static void CheckAlgorithmFollowsPicker()
    {
        Console.WriteLine("");
        Console.WriteLine("【拖入文件跟随下拉框算法】");

        Type appType = _asm.GetType("HashChecker.App");
        if (appType != null)
        {
            try { Activator.CreateInstance(appType); }
            catch { /* 没有 App 也能继续，资源查找最多退化 */ }
        }

        Type windowType = _asm.GetType("HashChecker.MainWindow");
        object window;
        try
        {
            window = Activator.CreateInstance(windowType);
        }
        catch (Exception e)
        {
            Check("构造 MainWindow", false, Describe(e));
            return;
        }

        try
        {
            // 准备一个干净的文件（同级目录不放任何校验记录）
            string dir = Path.Combine(_work, "clean");
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, "lonely.bin");

            var data = new byte[2048];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)((i * 41 + 9) & 0xFF);
            File.WriteAllBytes(target, data);

            // 把下拉框切到 SHA3-512
            object picker = GetField(window, windowType, "AlgorithmPicker");
            object sha3 = FindAlgorithm("sha3-512");
            if (picker == null || sha3 == null)
            {
                Check("拿到下拉框与 SHA3-512", false, "picker=" + (picker != null) + " algo=" + (sha3 != null));
                return;
            }

            picker.GetType().GetProperty("SelectedItem").SetValue(picker, sha3);

            MethodInfo addPaths = windowType.GetMethod("AddPaths",
                BindingFlags.NonPublic | BindingFlags.Instance);

            addPaths.Invoke(window, new object[] { new[] { target }, false });

            object item = FindItem(window, windowType, target);
            Check("拖入后列表里有该文件", item != null, item == null ? "没找到行" : "");
            if (item == null) return;

            object selected = GetProperty(item, "SelectedAlgorithm");
            string id = selected == null ? "(null)" : (string)GetProperty(selected, "Id");

            Check("新行算法跟随下拉框（sha3-512）",
                string.Equals(id, "sha3-512", StringComparison.OrdinalIgnoreCase),
                "实际=" + id);

            object required = GetProperty(item, "RequiredAlgorithms");
            string firstRequired = null;
            if (required is System.Collections.IEnumerable)
            {
                foreach (object info in (System.Collections.IEnumerable)required)
                {
                    firstRequired = (string)GetProperty(info, "Id");
                    break;
                }
            }

            Check("实际要算的算法也是 sha3-512",
                string.Equals(firstRequired, "sha3-512", StringComparison.OrdinalIgnoreCase),
                "实际=" + (firstRequired ?? "(null)"));

            // 再切到 MD5，拖另一个文件，验证不是只对第一个文件生效
            object md5 = FindAlgorithm("md5");
            picker.GetType().GetProperty("SelectedItem").SetValue(picker, md5);

            string second = Path.Combine(dir, "second.bin");
            File.WriteAllBytes(second, data);
            addPaths.Invoke(window, new object[] { new[] { second }, false });

            object item2 = FindItem(window, windowType, second);
            string id2 = item2 == null ? "(null)" : (string)GetProperty(GetProperty(item2, "SelectedAlgorithm"), "Id");
            Check("第二个文件跟随切换后的 MD5",
                string.Equals(id2, "md5", StringComparison.OrdinalIgnoreCase), "实际=" + id2);
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
                // 窗口是隐藏的，关不掉不影响结论
            }
        }
    }

    static object FindAlgorithm(string id)
    {
        Type algorithms = _asm.GetType("HashChecker.Services.HashAlgorithms");
        MethodInfo byId = algorithms.GetMethod("ById", BindingFlags.Public | BindingFlags.Static,
            null, new[] { typeof(string) }, null);
        return byId.Invoke(null, new object[] { id });
    }

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

    static object GetField(object instance, Type type, string name)
    {
        FieldInfo field = type.GetField(name,
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
        return field == null ? null : field.GetValue(instance);
    }

    static object GetProperty(object instance, string name)
    {
        if (instance == null) return null;
        PropertyInfo p = instance.GetType().GetProperty(name);
        return p == null ? null : p.GetValue(instance);
    }

    static string Describe(Exception e)
    {
        if (e is TargetInvocationException && e.InnerException != null) return Describe(e.InnerException);
        return e.GetType().Name + ": " + e.Message;
    }
}
