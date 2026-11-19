using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

/// <summary>
/// 端到端：BLAKE3 的目录登记、校验记录往返、算法判定与双向反查。
///
/// 内核本身的正确性由 hashprobe 的【BLAKE3 树形边界】负责；这里守的是另外几件事 ——
/// 它在算法目录里登记成什么样、后缀认不认得出、记录能不能原样读回来、反查找不找得到文件。
/// 内核算对了但目录写错了，用户看到的仍然是一个「永远校验失败」的算法。
///
/// 用法: blake3probe.exe &lt;HashChecker.exe&gt; [工作目录]
/// 退出码: 0 = 全部通过, 1 = 有失败项, 2 = 用法错误
/// </summary>
class Blake3Probe
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
        RedirectSettingsToTemp();
        if (args.Length < 1)
        {
            Console.WriteLine("用法: blake3probe.exe <HashChecker.exe> [工作目录]");
            return 2;
        }

        string asmPath = Path.GetFullPath(args[0]);
        _work = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(Path.GetTempPath(), "HashCheckerBlake3Probe");

        if (Directory.Exists(_work)) Directory.Delete(_work, true);
        Directory.CreateDirectory(_work);

        var asm = Assembly.LoadFrom(asmPath);
        Type algorithms = asm.GetType("HashChecker.Services.HashAlgorithms");
        Type checksumFile = asm.GetType("HashChecker.Services.ChecksumFile");
        Type resolver = asm.GetType("HashChecker.Services.ChecksumResolver");

        Console.WriteLine("=== BLAKE3 端到端（记录读写 / 算法判定 / 反查） ===");
        Console.WriteLine("被测程序: " + asmPath);
        Console.WriteLine("");

        // 目录登记
        object blake3 = Invoke(algorithms, "ById", "blake3");
        Check("目录里有 blake3", blake3 != null, "");
        if (blake3 == null) return 1;

        Check("显示名是 BLAKE3", (string)Get(blake3, "DisplayName") == "BLAKE3", (string)Get(blake3, "DisplayName"));
        Check("首选后缀是 .blake3", (string)Get(blake3, "Extension") == ".blake3", (string)Get(blake3, "Extension"));
        Check("摘要是 32 字节", (int)Get(blake3, "DigestBytes") == 32, "");

        Check("ByExtension(.blake3) 回到 blake3",
            IdOf(Invoke(algorithms, "ByExtension", "x.bin.blake3")) == "blake3",
            IdOf(Invoke(algorithms, "ByExtension", "x.bin.blake3")));

        foreach (string alias in new[] { "blake3", "BLAKE3", "Blake-3", "blake-3", "b3" })
        {
            string got = IdOf(Invoke(algorithms, "ByName", alias));
            Check("别名 " + alias + " 认成 blake3", got == "blake3", "得到 " + (got ?? "(null)"));
        }

        // 与 BLAKE2b 不能混：两者的首选后缀必须不同
        object blake2b = Invoke(algorithms, "ById", "blake2b-256");
        Check("BLAKE3 与 BLAKE2b-256 后缀不冲突",
            (string)Get(blake3, "Extension") != (string)Get(blake2b, "Extension"), "");

        // 值与记录往返
        string dir = Path.Combine(_work, "round");
        Directory.CreateDirectory(dir);
        string target = Path.Combine(dir, "payload.bin");
        var data = new byte[5000];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 251);
        File.WriteAllBytes(target, data);

        string digest = ComputeDigest(blake3, data);
        Check("BLAKE3 值长度为 64", digest != null && digest.Length == 64, "长度=" + (digest == null ? 0 : digest.Length));

        string recordPath = target + ".blake3";
        File.WriteAllText(recordPath, digest + " *payload.bin\r\n", new UTF8Encoding(false));

        object document = Invoke(checksumFile, "Parse", recordPath);
        Check("记录按后缀认成 blake3", IdOf(Get(document, "Algorithm")) == "blake3", IdOf(Get(document, "Algorithm")));

        object entry = FirstEntry(document);
        Check("记录里解析出的值一致",
            entry != null && (string)Get(entry, "Hash") == digest,
            entry == null ? "没有条目" : (string)Get(entry, "Hash"));

        // 后缀与值长度**冲突**时以长度为准：.blake3 里装了 MD5 的 32 位值 → 必须判成 MD5。
        // 注意同长度不算冲突：.blake3 里装 BLAKE2b-256 的值（都是 64 位）仍按后缀认 blake3，
        // 这与 .sha256 装 SHA3-256 值时按 SHA-256 认是同一条规则，不是 BLAKE3 的例外。
        string wrong = Path.Combine(dir, "wrong.blake3");
        string md5Value = ComputeDigest(Invoke(algorithms, "ById", "md5"), data);
        File.WriteAllText(wrong, md5Value + " *payload.bin\r\n", new UTF8Encoding(false));

        object wrongDoc = Invoke(checksumFile, "Parse", wrong);
        string wrongId = IdOf(Get(wrongDoc, "Algorithm"));
        Check("长度冲突时以值长度为准（.blake3 装 MD5 值 → md5）", wrongId == "md5",
            "判定=" + (wrongId ?? "(null，按候选比对)"));

        // 同长度时后缀说了算 —— 这里只是把规则写清楚，避免以后有人误以为 BLAKE3 会走另一套。
        string sameLength = Path.Combine(dir, "samelength.blake3");
        string blake2bValue = ComputeDigest(Invoke(algorithms, "ById", "blake2b-256"), data);
        File.WriteAllText(sameLength, blake2bValue + " *payload.bin\r\n", new UTF8Encoding(false));
        object sameLengthDoc = Invoke(checksumFile, "Parse", sameLength);
        Check("同长度时不与后缀冲突（仍按 .blake3 认）",
            IdOf(Get(sameLengthDoc, "Algorithm")) == "blake3",
            "判定=" + IdOf(Get(sameLengthDoc, "Algorithm")));

        // 行内算法名优先
        string inline = Path.Combine(dir, "inline.txt");
        File.WriteAllText(inline, "BLAKE3 (" + Path.GetFileName(target) + ") = " + digest, new UTF8Encoding(false));
        object inlineDoc = Invoke(checksumFile, "Parse", inline);
        object inlineEntry = FirstEntry(inlineDoc);
        string inlineId = inlineEntry == null ? null : IdOf(Get(inlineEntry, "Algorithm"));
        Check("行内写 BLAKE3 认得出来", inlineId == "blake3", "识别=" + (inlineId ?? "(null)"));

        // 记录 → 文件反查
        object resolution = Invoke(resolver, "ResolveRecord", recordPath);
        object resolved = Get(resolution, "Resolved");
        int count = 0;
        string firstTarget = null;
        foreach (object item in (System.Collections.IEnumerable)resolved)
        {
            if (count == 0) firstTarget = (string)Get(item, "TargetPath");
            count++;
        }
        Check("从 .blake3 记录反查到目标文件", count == 1, "反查到 " + count + " 个");
        Check("反查到的正是 payload.bin",
            firstTarget != null && string.Equals(Path.GetFileName(firstTarget), "payload.bin", StringComparison.OrdinalIgnoreCase),
            firstTarget ?? "(null)");

        // 文件 → 记录反查
        object match = Invoke(resolver, "FindRecordForFile", target);
        Check("从文件反查到 .blake3 记录",
            match != null && IdOf(Get(match, "Algorithm")) == "blake3",
            match == null ? "(没有匹配)" : IdOf(Get(match, "Algorithm")));

        Console.WriteLine("");
        if (_failures == 0)
        {
            Console.WriteLine("全部符合预期 ✓");
            return 0;
        }
        Console.WriteLine("!! 有 " + _failures + " 项不符合预期");
        return 1;
    }

    static object FirstEntry(object document)
    {
        object entries = Get(document, "Entries");
        Array array = entries as Array;
        if (array == null || array.Length == 0) return null;
        return array.GetValue(0);
    }

    static string IdOf(object algorithmInfo)
    {
        return algorithmInfo == null ? null : (string)Get(algorithmInfo, "Id");
    }

    static string ComputeDigest(object algorithmInfo, byte[] data)
    {
        Delegate factory = (Delegate)algorithmInfo.GetType().GetProperty("CreateCore").GetValue(algorithmInfo);
        object core = factory.DynamicInvoke(new object[0]);
        Type coreType = core.GetType();
        coreType.GetMethod("Initialize").Invoke(core, null);
        coreType.GetMethod("Append").Invoke(core, new object[] { data, 0, data.Length });
        byte[] digest = (byte[])coreType.GetMethod("Finish").Invoke(core, null);

        var sb = new StringBuilder(digest.Length * 2);
        foreach (byte b in digest) sb.Append(b.ToString("x2"));
        return sb.ToString();
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
        if (info == null) { Console.WriteLine("!! 找不到 " + type.Name + "." + method); _failures++; return null; }

        try { return info.Invoke(null, new object[] { arg }); }
        catch (TargetInvocationException e)
        {
            Console.WriteLine("!! " + type.Name + "." + method + " 抛异常: " + e.InnerException.Message);
            _failures++;
            return null;
        }
    }

    /// <summary>
    /// 把配置目录改道到临时目录。必须在触碰产品任何类型之前调用 ——
    /// 探针用 LoadFrom 载入产品程序集后，产品会拿「入口程序集名」当配置目录名，
    /// 那个名字就是本探针，配置会落到 %LOCALAPPDATA%\<探针名>\ 里，
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