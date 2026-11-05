using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

/// <summary>
/// 保存路径后缀纠正的验证（对应 bug「SHA3-512 存出来变成 .sha3-512.sha256」）。
///
/// 直接反射调 MainWindow.NormalizeChecksumPath，覆盖四类输入：
/// 1. 后缀已正确 → 原样返回；
/// 2. 后缀完全不是算法后缀（x.txt / 无后缀）→ 换成该算法首选后缀；
/// 3. 双后缀（x.bin.sha3-512.sha256）→ 纠正成 x.bin.sha3-512；
/// 4. 内容是算法 A、名字却写着算法 B → 纠正成 A。
///
/// 另外验证过滤器覆盖了全部算法后缀（SaveFileDialog 的 AddExtension 依赖它）。
///
/// 用法: saveprobe.exe &lt;HashChecker.exe&gt; [工作目录]
/// 退出码: 0 = 全部通过, 1 = 有失败项, 2 = 用法错误
/// </summary>
class SaveProbe
{
    static int _failures;

    static void Check(string name, bool ok, string detail)
    {
        if (!ok) _failures++;
        Console.WriteLine("  {0} {1}{2}", ok ? "OK  " : "!!  ", name,
            string.IsNullOrEmpty(detail) ? "" : "  —— " + detail);
    }

    static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("用法: saveprobe.exe <HashChecker.exe> [工作目录]");
            return 2;
        }

        string asmPath = Path.GetFullPath(args[0]);
        var asm = Assembly.LoadFrom(asmPath);
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string simple = new AssemblyName(e.Name).Name;
            string candidate = Path.Combine(Path.GetDirectoryName(asmPath), simple + ".exe");
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        };

        Type mainWindow = asm.GetType("HashChecker.MainWindow");
        Type algorithms = asm.GetType("HashChecker.Services.HashAlgorithms");
        Type checksumFile = asm.GetType("HashChecker.Services.ChecksumFile");
        Type localization = asm.GetType("HashChecker.Localization");

        if (mainWindow == null || algorithms == null || checksumFile == null)
        {
            Console.WriteLine("!! 找不到 MainWindow / HashAlgorithms / ChecksumFile");
            return 1;
        }

        Console.WriteLine("=== 保存路径后缀纠正验证 ===");
        Console.WriteLine("被测程序: " + asmPath);
        Console.WriteLine("");

        MethodInfo normalize = mainWindow.GetMethod("NormalizeChecksumPath",
            BindingFlags.NonPublic | BindingFlags.Static);

        if (normalize == null)
        {
            Console.WriteLine("!! 找不到 NormalizeChecksumPath");
            return 1;
        }

        algorithmsById = algorithms;

        const string folder = @"C:\temp";

        CheckSingle(normalize, folder, "data.bin.sha3-512", "sha3-512", "data.bin.sha3-512");
        CheckSingle(normalize, folder, "data.bin.sha256", "sha256", "data.bin.sha256");
        CheckSingle(normalize, folder, "data.bin.md5", "md5", "data.bin.md5");

        // 双后缀：这正是用户报的那个
        CheckSingle(normalize, folder, "data.bin.sha3-512.sha256", "sha3-512", "data.bin.sha3-512");
        CheckSingle(normalize, folder, "data.bin.md5.sha256", "md5", "data.bin.md5");
        CheckSingle(normalize, folder, "data.bin.sha3-256.sha256", "sha3-256", "data.bin.sha3-256");

        // 后缀不是算法后缀
        CheckSingle(normalize, folder, "data.bin.txt", "sha3-512", "data.bin.sha3-512");
        // 无算法后缀时按 Path.ChangeExtension 的语义处理：把最后一段扩展名换成算法后缀。
        //   data.bin    -> data.sha3-512（.bin 被替换）
        //   data        -> data.sha3-512（无后缀，直接追加）
        // 这是「文件名不带算法后缀」的正常情形，不是 bug。
        CheckSingle(normalize, folder, "data.bin", "sha3-512", "data.sha3-512");
        CheckSingle(normalize, folder, "data", "sha3-512", "data.sha3-512");

        // 名字写着 B、内容是 A
        CheckSingle(normalize, folder, "data.bin.sha256", "sha3-512", "data.bin.sha3-512");

        // 全部算法都过一遍：用该算法的值生成记录，路径故意给双后缀
        Console.WriteLine("");
        Console.WriteLine("【全部算法：双后缀纠正】");
        foreach (object info in All(algorithms))
        {
            string id = (string)Get(info, "Id");
            string ext = (string)Get(info, "Extension");

            string digest = SampleDigest(checksumFile, info);
            if (digest == null) continue;

            var lines = new List<string> { digest + " *payload.bin" };

            // 模拟 SaveFileDialog 把 .sha3-512 追成了 .sha3-512.sha256
            string broken = Path.Combine(folder, "payload.bin" + ext + ".sha256");
            string fixedPath = (string)normalize.Invoke(null, new object[] { broken, info });

            string expected = Path.Combine(folder, "payload.bin" + ext);
            Check(id + " 双后缀纠正",
                string.Equals(fixedPath, expected, StringComparison.OrdinalIgnoreCase),
                "得到 " + Path.GetFileName(fixedPath) + "（期望 " + Path.GetFileName(expected) + "）");
        }

        // 过滤器覆盖性
        Console.WriteLine("");
        Console.WriteLine("【保存过滤器覆盖】");
        if (localization != null)
        {
            MethodInfo get = localization.GetMethod("Get", BindingFlags.Public | BindingFlags.Static);
            string filter = get == null ? null : (string)get.Invoke(null, new object[] { "MsgSaveListFilter" });

            if (filter == null)
            {
                Check("能取到过滤器", false, "Get 返回 null");
            }
            else
            {
                int missing = 0;
                foreach (object info in All(algorithms))
                {
                    string ext = (string)Get(info, "Extension");
                    if (!filter.Contains("*" + ext)) missing++;
                }
                Check("过滤器覆盖全部算法后缀", missing == 0, "缺失 " + missing + " 个");
            }
        }
        else
        {
            Check("找到 Localization 类型", false, "");
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

    /// <summary>用指定算法生成一个样例值，拼成一行记录文本。</summary>
    static string SampleDigest(Type checksumFile, object algorithmInfo)
    {
        try
        {
            Delegate factory = (Delegate)algorithmInfo.GetType().GetProperty("CreateCore").GetValue(algorithmInfo);
            object core = factory.DynamicInvoke(new object[0]);

            byte[] data = new byte[1024];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)((i * 31 + 5) & 0xFF);

            core.GetType().GetMethod("Initialize").Invoke(core, null);
            core.GetType().GetMethod("Append").Invoke(core, new object[] { data, 0, data.Length });
            byte[] digest = (byte[])core.GetType().GetMethod("Finish").Invoke(core, null);

            var sb = new System.Text.StringBuilder(digest.Length * 2);
            foreach (byte b in digest) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
        catch (Exception e)
        {
            Console.WriteLine("!! 生成 " + algorithmInfo + " 样例值失败: " + e.Message);
            _failures++;
            return null;
        }
    }

    static void CheckSingle(MethodInfo normalize, string folder, string fileName,
        string algorithmId, string expectedName)
    {
        object info = Invoke(algorithmsById, "ById", algorithmId);
        if (info == null)
        {
            Check("找到算法 " + algorithmId, false, "");
            return;
        }

        string digest = SampleDigestFor(info, "0");
        if (digest == null) return;

        var lines = new List<string> { digest + " *payload.bin" };
        string path = Path.Combine(folder, fileName);

        string result = (string)normalize.Invoke(null, new object[] { path, info });
        string expected = Path.Combine(folder, expectedName);

        Check(fileName + " → " + expectedName,
            string.Equals(result, expected, StringComparison.OrdinalIgnoreCase),
            "得到 " + Path.GetFileName(result));
    }

    static Type algorithmsById;

    static string SampleDigestFor(object algorithmInfo, string hexLengthText)
    {
        try
        {
            Delegate factory = (Delegate)algorithmInfo.GetType().GetProperty("CreateCore").GetValue(algorithmInfo);
            object core = factory.DynamicInvoke(new object[0]);

            byte[] data = new byte[512];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)((i * 17 + 3) & 0xFF);

            core.GetType().GetMethod("Initialize").Invoke(core, null);
            core.GetType().GetMethod("Append").Invoke(core, new object[] { data, 0, data.Length });
            byte[] digest = (byte[])core.GetType().GetMethod("Finish").Invoke(core, null);

            var sb = new System.Text.StringBuilder(digest.Length * 2);
            foreach (byte b in digest) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
        catch (Exception e)
        {
            Console.WriteLine("!! 生成样例值失败: " + e.Message);
            _failures++;
            return null;
        }
    }

    static System.Collections.IEnumerable All(Type algorithms)
    {
        object list = algorithms.GetProperty("All", BindingFlags.Public | BindingFlags.Static).GetValue(null);
        foreach (object item in (System.Collections.IEnumerable)list) yield return item;
    }

    static object Get(object instance, string property)
    {
        if (instance == null) return null;
        return instance.GetType().GetProperty(property).GetValue(instance);
    }

    static object Invoke(Type type, string method, string arg)
    {
        MethodInfo info = type.GetMethod(method, BindingFlags.Public | BindingFlags.Static,
            null, new[] { typeof(string) }, null);
        if (info == null) return null;
        return info.Invoke(null, new object[] { arg });
    }
}
