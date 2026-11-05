using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

/// <summary>
/// 算法 ↔ 校验文件后缀的契约验证（对应需求 5）。
///
/// 需求 5 的症状是「存了个 SHA3-256 的校验文件，后缀却还是 .sha256」，
/// 把它传回来时程序按后缀认成 SHA-256，自然对不上。
///
/// 这里查三件事：
/// 1. 每个算法的首选后缀与算法一一对应，不重复、不落到别的算法头上；
/// 2. 用该后缀写出的值，再按后缀解析回来，认到的算法必须还是原来那个；
/// 3. 后缀与值长度冲突时，解析以值长度为准（需求 4）。
///
/// 用法: suffixprobe.exe &lt;HashChecker.exe&gt; [工作目录]
/// 退出码: 0 = 全部通过, 1 = 有失败项, 2 = 用法错误
/// </summary>
class SuffixProbe
{
    static int _failures;
    static string _work;

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
            Console.WriteLine("用法: suffixprobe.exe <HashChecker.exe> [工作目录]");
            return 2;
        }

        string asmPath = Path.GetFullPath(args[0]);
        _work = args.Length > 1
            ? Path.GetFullPath(args[1])
            : Path.Combine(Path.GetTempPath(), "HashCheckerSuffixProbe");

        if (Directory.Exists(_work)) Directory.Delete(_work, true);
        Directory.CreateDirectory(_work);

        var asm = Assembly.LoadFrom(asmPath);
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string simple = new AssemblyName(e.Name).Name;
            string candidate = Path.Combine(Path.GetDirectoryName(asmPath), simple + ".exe");
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        };

        Type algorithms = asm.GetType("HashChecker.Services.HashAlgorithms");
        Type checksumFile = asm.GetType("HashChecker.Services.ChecksumFile");
        if (algorithms == null || checksumFile == null)
        {
            Console.WriteLine("!! 找不到 HashAlgorithms / ChecksumFile");
            return 1;
        }

        Console.WriteLine("=== 算法 ↔ 校验文件后缀契约验证 ===");
        Console.WriteLine("被测程序: " + asmPath);
        Console.WriteLine("工作目录: " + _work);
        Console.WriteLine("");

        CheckSuffixUniqueness(algorithms);
        CheckRoundTrip(algorithms, checksumFile);
        CheckSuffixLengthConflict(algorithms, checksumFile);

        Console.WriteLine("");
        if (_failures == 0)
        {
            Console.WriteLine("全部符合预期 ✓");
            return 0;
        }
        Console.WriteLine("!! 有 " + _failures + " 项不符合预期");
        return 1;
    }

    /// <summary>每个算法的首选后缀必须唯一，且解析回来指向同一个算法。</summary>
    static void CheckSuffixUniqueness(Type algorithms)
    {
        Console.WriteLine("【后缀唯一性】");

        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (object item in All(algorithms))
        {
            string id = (string)Get(item, "Id");
            string extension = (string)Get(item, "Extension");
            string display = (string)Get(item, "DisplayName");

            if (string.IsNullOrEmpty(extension))
            {
                Check(id + " 有首选后缀", false, "Extension 为空");
                continue;
            }

            if (seen.ContainsKey(extension))
            {
                Check(id + " 后缀不与他人重复", false,
                    extension + " 已被 " + seen[extension] + " 占用");
                continue;
            }

            seen[extension] = id;

            // 用该后缀反查，必须回到同一个算法
            object resolved = Invoke(algorithms, "ByExtension", "record" + extension);
            string resolvedId = resolved == null ? null : (string)Get(resolved, "Id");

            Check(display + " 后缀 " + extension + " 反查回自己",
                string.Equals(resolvedId, id, StringComparison.OrdinalIgnoreCase),
                "反查得到 " + (resolvedId ?? "(null)"));
        }

        Console.WriteLine("  共 " + seen.Count + " 个算法各有唯一后缀");
    }

    /// <summary>
    /// 往返：用算法算出值 → 写成以该算法后缀命名的记录 → 解析回来，
    /// 认到的算法必须还是原来那个，值也必须一模一样。
    /// 这正是「SHA3-256 存成 .sha256 就再也对不上」的反面。
    /// </summary>
    static void CheckRoundTrip(Type algorithms, Type checksumFile)
    {
        Console.WriteLine("");
        Console.WriteLine("【保存 → 重新解析 往返】");

        string dir = Path.Combine(_work, "round");
        Directory.CreateDirectory(dir);
        string target = Path.Combine(dir, "payload.bin");

        byte[] data = new byte[4096];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)((i * 53 + 7) & 0xFF);
        File.WriteAllBytes(target, data);

        foreach (object item in All(algorithms))
        {
            string id = (string)Get(item, "Id");
            string extension = (string)Get(item, "Extension");

            string digest = ComputeDigest(item, data);
            if (digest == null) continue;

            string recordPath = target + extension;
            File.WriteAllText(recordPath,
                digest + " *" + Path.GetFileName(target) + "\r\n",
                new System.Text.UTF8Encoding(false));

            object document = Invoke(checksumFile, "Parse", recordPath);
            object algorithm = document == null ? null : Get(document, "Algorithm");
            string parsedId = algorithm == null ? null : (string)Get(algorithm, "Id");

            object entries = document == null ? null : Get(document, "Entries");
            Array entryArray = entries as Array;
            string parsedHash = null;
            if (entryArray != null && entryArray.Length > 0)
            {
                parsedHash = (string)Get(entryArray.GetValue(0), "Hash");
            }

            Check(id + " 往返：算法一致",
                string.Equals(parsedId, id, StringComparison.OrdinalIgnoreCase),
                "写入 " + extension + "，解析成 " + (parsedId ?? "(null)"));

            Check(id + " 往返：值一致",
                parsedHash != null && string.Equals(parsedHash, digest, StringComparison.Ordinal),
                parsedHash == null ? "没解析出值" : parsedHash);

            File.Delete(recordPath);
        }
    }

    /// <summary>后缀与值长度冲突时，必须以值长度为准（需求 4 的修复点）。</summary>
    static void CheckSuffixLengthConflict(Type algorithms, Type checksumFile)
    {
        Console.WriteLine("");
        Console.WriteLine("【后缀与值长度冲突】");

        string dir = Path.Combine(_work, "conflict");
        Directory.CreateDirectory(dir);

        // 一个真实文件，算出 MD5（32 位十六进制）
        string target = Path.Combine(dir, "file.bin");
        byte[] data = new byte[2048];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)((i * 29 + 3) & 0xFF);
        File.WriteAllBytes(target, data);

        object md5 = Invoke(algorithms, "ById", "md5");
        string md5Value = md5 == null ? null : ComputeDigest(md5, data);
        if (md5Value == null)
        {
            Check("能算出 MD5 基准值", false, "MD5 内核不可用");
            return;
        }

        // 故意把它写进 .sha256 命名的文件
        string wrong = Path.Combine(dir, "conflict.sha256");
        File.WriteAllText(wrong, md5Value + " *" + Path.GetFileName(target) + "\r\n",
            new System.Text.UTF8Encoding(false));

        object document = Invoke(checksumFile, "Parse", wrong);
        object algorithm = document == null ? null : Get(document, "Algorithm");
        string parsedId = algorithm == null ? null : (string)Get(algorithm, "Id");

        Check("后缀 .sha256 装 MD5 值 → 按 MD5 认",
            string.Equals(parsedId, "md5", StringComparison.OrdinalIgnoreCase),
            "解析成 " + (parsedId ?? "(null，按候选比对)"));

        // 反向：SHA-256 的值写进 .md5 命名的文件
        object sha256 = Invoke(algorithms, "ById", "sha256");
        string shaValue = sha256 == null ? null : ComputeDigest(sha256, data);
        if (shaValue == null) return;

        string wrong2 = Path.Combine(dir, "conflict2.md5");
        File.WriteAllText(wrong2, shaValue + " *" + Path.GetFileName(target) + "\r\n",
            new System.Text.UTF8Encoding(false));

        object document2 = Invoke(checksumFile, "Parse", wrong2);
        object algorithm2 = document2 == null ? null : Get(document2, "Algorithm");
        string parsedId2 = algorithm2 == null ? null : (string)Get(algorithm2, "Id");

        Check("后缀 .md5 装 SHA-256 值 → 不锁死成 MD5",
            parsedId2 == null || string.Equals(parsedId2, "sha256", StringComparison.OrdinalIgnoreCase),
            "解析成 " + (parsedId2 ?? "(null，按候选比对)"));
    }

    /// <summary>用某个算法算一段数据的十六进制值。</summary>
    static string ComputeDigest(object algorithmInfo, byte[] data)
    {
        try
        {
            Delegate factory = (Delegate)algorithmInfo.GetType().GetProperty("CreateCore").GetValue(algorithmInfo);
            object core = factory.DynamicInvoke(new object[0]);

            Type coreType = core.GetType();
            coreType.GetMethod("Initialize").Invoke(core, null);
            coreType.GetMethod("Append").Invoke(core, new object[] { data, 0, data.Length });
            byte[] digest = (byte[])coreType.GetMethod("Finish").Invoke(core, null);

            var sb = new System.Text.StringBuilder(digest.Length * 2);
            foreach (byte b in digest) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
        catch (Exception e)
        {
            Console.WriteLine("!! 计算 " + algorithmInfo + " 失败: " + e.Message);
            _failures++;
            return null;
        }
    }

    static IEnumerable<object> All(Type algorithms)
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
        if (info == null)
        {
            Console.WriteLine("!! 找不到 " + type.Name + "." + method);
            _failures++;
            return null;
        }

        try
        {
            return info.Invoke(null, new object[] { arg });
        }
        catch (TargetInvocationException e)
        {
            Console.WriteLine("!! " + type.Name + "." + method + " 抛异常: " + e.InnerException);
            _failures++;
            return null;
        }
    }
}
