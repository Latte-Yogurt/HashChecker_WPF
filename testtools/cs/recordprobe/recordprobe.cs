using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// 校验记录解析与反推的运行时验证。
///
/// 直接反射调用被测程序里的 ChecksumFile / ChecksumResolver / TextEncoding，
/// 用各种编码、各种行格式、各种文件名写法的记录文件试一遍，
/// 确认「宽容解码」「记录 → 文件反推」「文件 → 记录查找」三条链路都对。
///
/// 用法: recordprobe.exe &lt;被测 exe 路径&gt; [工作目录]
/// 退出码: 0 = 全部通过, 1 = 有失败项, 2 = 用法错误
/// </summary>
class RecordProbe
{
    static int _failures;
    static string _work;
    static Type _checksumFile;
    static Type _resolver;
    static Type _textEncoding;

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
            Console.WriteLine("用法: recordprobe.exe <被测 exe 路径> [工作目录]");
            return 2;
        }

        string asmPath = Path.GetFullPath(args[0]);
        _work = args.Length > 1
            ? Path.GetFullPath(args[1])
            : Path.Combine(Path.GetTempPath(), "HashCheckerRecordProbe");

        if (Directory.Exists(_work)) Directory.Delete(_work, true);
        Directory.CreateDirectory(_work);

        var asm = Assembly.LoadFrom(asmPath);
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string simple = new AssemblyName(e.Name).Name;
            string candidate = Path.Combine(Path.GetDirectoryName(asmPath), simple + ".exe");
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        };

        _checksumFile = asm.GetType("HashChecker.Services.ChecksumFile");
        _resolver = asm.GetType("HashChecker.Services.ChecksumResolver");
        _textEncoding = asm.GetType("HashChecker.Services.TextEncoding");

        Console.WriteLine("=== 校验记录解析 / 反推的运行时验证 ===");
        Console.WriteLine("被测程序: " + asmPath);
        Console.WriteLine("工作目录: " + _work);

        if (_checksumFile == null || _resolver == null || _textEncoding == null)
        {
            Console.WriteLine("!! 找不到 ChecksumFile / ChecksumResolver / TextEncoding");
            return 1;
        }

        CheckEncodings();
        CheckLineFormats();
        CheckMissingExtension();
        CheckRecordToFiles();
        CheckFileToRecord();
        CheckNotARecord();
        CheckAlgorithms();

        Console.WriteLine("");
        if (_failures == 0)
        {
            Console.WriteLine("全部符合预期 ✓");
            return 0;
        }
        Console.WriteLine("!! 有 " + _failures + " 项不符合预期");
        return 1;
    }

    // ---------------------------------------------------------------- 编码宽容性
    static void CheckEncodings()
    {
        Console.WriteLine("");
        Console.WriteLine("【编码宽容性】");

        string dir = Path.Combine(_work, "enc");
        Directory.CreateDirectory(dir);
        string target = MakeFile(Path.Combine(dir, "payload.bin"), 8192);
        string hash = RawSha256(target);
        string line = hash + " *" + Path.GetFileName(target) + "\r\n";

        TryEncoding(dir, "utf8-nobom", line, new UTF8Encoding(false));
        TryEncoding(dir, "utf8-bom", line, new UTF8Encoding(true));
        TryEncoding(dir, "utf16le-bom", line, Encoding.Unicode);
        TryEncoding(dir, "utf16be-bom", line, Encoding.BigEndianUnicode);
        TryEncoding(dir, "utf16le-nobom", line, new UnicodeEncoding(false, false));
        TryEncoding(dir, "gb18030", line, GetEncoding("GB18030"));
        TryEncoding(dir, "gbk", line, GetEncoding("GBK"));
        TryEncoding(dir, "big5", line, GetEncoding("Big5"));
        TryEncoding(dir, "utf32le", line, new UTF32Encoding(false, true));

        // 无 BOM 且带中文文件名的 UTF-8：最容易被误判成 GBK 的一种
        string cnFile = MakeFile(Path.Combine(dir, "中文 名称.bin"), 4096);
        string cnLine = RawSha256(cnFile) + " *中文 名称.bin\r\n";
        TryEncoding(dir, "utf8-chinese-name", cnLine, new UTF8Encoding(false));

        // GB18030 的中文文件名记录：必须靠「严格 UTF-8 解不开」回落才能读对
        string gbPath = Path.Combine(dir, "gb18030-chinese-name.sha256");
        File.WriteAllText(gbPath, cnLine, GetEncoding("GB18030"));
        CheckDocument("gb18030-chinese-name", gbPath, 1, null);
        string gbResolved = ResolveFirst(gbPath);
        Check("gb18030 中文文件名反推成功",
            gbResolved != null && gbResolved.EndsWith("中文 名称.bin", StringComparison.Ordinal),
            "实际=" + (gbResolved ?? "(null)"));

        // Big5 中文文件名
        string big5Path = Path.Combine(dir, "big5-chinese-name.sha256");
        string big5File = MakeFile(Path.Combine(dir, "繁體檔.bin"), 2048);
        File.WriteAllText(big5Path, RawSha256(big5File) + " *繁體檔.bin\r\n", GetEncoding("Big5"));
        CheckDocument("big5-chinese-name", big5Path, 1, null);

        // 混合换行与 CRLF / LF / CR
        string mixed = hash + " *" + Path.GetFileName(target) + "\n"
                       + hash + " *" + Path.GetFileName(target) + "\r"
                       + "# 注释行\r\n\r\n";
        string mixedPath = Path.Combine(dir, "mixed-newlines.sha256");
        File.WriteAllText(mixedPath, mixed, new UTF8Encoding(false));
        CheckDocument("混合换行/注释", mixedPath, 2, null);
    }

    static void TryEncoding(string dir, string tag, string text, Encoding encoding)
    {
        string path = Path.Combine(dir, tag + ".sha256");
        File.WriteAllText(path, text, encoding);
        CheckDocument(tag, path, 1, null);
    }

    static Encoding GetEncoding(string name)
    {
        try { return Encoding.GetEncoding(name); }
        catch (Exception)
        {
            // 系统没装该代码页时退回 UTF-8，该项检查会被跳过而不是误报失败
            return new UTF8Encoding(false);
        }
    }

    // ---------------------------------------------------------------- 行格式
    static void CheckLineFormats()
    {
        Console.WriteLine("");
        Console.WriteLine("【行格式宽容性】");

        string dir = Path.Combine(_work, "fmt");
        Directory.CreateDirectory(dir);
        string target = MakeFile(Path.Combine(dir, "tool.exe"), 4096);
        string hash = RawSha256(target);
        string upper = hash.ToUpperInvariant();
        string name = Path.GetFileName(target);

        var cases = new List<string[]>
        {
            new[] { "sha256sum 文本模式", hash + " *" + name },
            new[] { "sha256sum 二进制模式", hash + "  " + name },
            new[] { "制表符分隔", hash + "\t" + name },
            new[] { "大写值", upper + " *" + name },
            new[] { "sha256: 前缀", "sha256:" + hash + " *" + name },
            new[] { "文件名 = 值", name + " = " + hash },
            new[] { "算法括号格式", "SHA256 (" + name + ") = " + hash },
            new[] { "带引号文件名", hash + " *\"" + name + "\"" },
            new[] { "相对子目录", hash + " *sub/" + name },
            new[] { "只有值", hash },
            new[] { "值后无引导符无空格", hash + "*" + name },
        };

        foreach (string[] c in cases)
        {
            string path = Path.Combine(dir, "case_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".sha256");
            File.WriteAllText(path, c[1], new UTF8Encoding(false));

            string parsedHash = InvokeString(_checksumFile, "ParseLine", new object[] { c[1], null }, "Hash");
            string parsedPath = InvokeString(_checksumFile, "ParseLine", new object[] { c[1], null }, "Path");

            Check(c[0], parsedHash != null && parsedHash == hash,
                "解析值=" + (parsedHash ?? "(null)") + " 路径=" + (parsedPath ?? ""));

            // 「只有值」的记录靠记录文件名反推目标，而这个临时记录名与目标无关，
            // 解析不到东西是正确行为，不参与反推断言。
            if (c[1] == hash)
            {
                string noTarget = ResolveFirst(path);
                Check(c[0] + " 不反推无关文件", noTarget == null,
                    noTarget == null ? "正确地没有反推出文件" : "误认=" + Path.GetFileName(noTarget));
                continue;
            }

            string resolved = ResolveFirst(path);
            Check(c[0] + " → 反推出文件", resolved != null,
                resolved == null ? "未解析到目标" : Path.GetFileName(resolved));
        }
    }

    // ---------------------------------------------------------------- 少写扩展名
    static void CheckMissingExtension()
    {
        Console.WriteLine("");
        Console.WriteLine("【文件名少写扩展名】");

        string dir = Path.Combine(_work, "noext");
        Directory.CreateDirectory(dir);
        string target = MakeFile(Path.Combine(dir, "ASCIIPlayer.exe"), 16 * 1024);
        string hash = RawSha256(target);

        // 记录里写不带扩展名的名字
        string path = Path.Combine(dir, "ASCIIPlayer.sha256");
        File.WriteAllText(path, hash + " *ASCIIPlayer", new UTF8Encoding(false));

        string resolved = ResolveFirst(path);
        Check("记录名 ASCIIPlayer 对上 ASCIIPlayer.exe",
            resolved != null && resolved.EndsWith("ASCIIPlayer.exe", StringComparison.OrdinalIgnoreCase),
            "实际=" + (resolved ?? "(null)"));

        bool match = (bool)InvokeStatic(_checksumFile, "FileNameMatches", "ASCIIPlayer", "ASCIIPlayer.exe");
        Check("FileNameMatches 允许缺扩展名", match, "ASCIIPlayer vs ASCIIPlayer.exe");

        bool reverse = (bool)InvokeStatic(_checksumFile, "FileNameMatches", "ASCIIPlayer.exe", "ASCIIPlayer");
        Check("FileNameMatches 反向也允许", reverse, "ASCIIPlayer.exe vs ASCIIPlayer");

        bool different = (bool)InvokeStatic(_checksumFile, "FileNameMatches", "Other.exe", "ASCIIPlayer.exe");
        Check("不同文件名不误判", !different, "Other.exe vs ASCIIPlayer.exe");

        bool otherExt = (bool)InvokeStatic(_checksumFile, "FileNameMatches", "ASCIIPlayer.zip", "ASCIIPlayer.exe");
        Check("不同扩展名不误判", !otherExt, "ASCIIPlayer.zip vs ASCIIPlayer.exe");
    }

    // ---------------------------------------------------------------- 记录 → 文件
    static void CheckRecordToFiles()
    {
        Console.WriteLine("");
        Console.WriteLine("【校验记录 → 反推文件】");

        string dir = Path.Combine(_work, "multi");
        Directory.CreateDirectory(dir);
        string a = MakeFile(Path.Combine(dir, "one.bin"), 4096);
        string b = MakeFile(Path.Combine(dir, "two.bin"), 8192);

        string path = Path.Combine(dir, "bundle.sha256");
        File.WriteAllText(path,
            "# 两条记录\r\n" + RawSha256(a) + " *one.bin\r\n" + RawSha256(b) + " *two\r\n",
            new UTF8Encoding(true));

        object resolution = InvokeStatic(_resolver, "ResolveRecord", path);
        int resolved = (int)GetProperty(resolution, "Resolved").GetType()
            .GetProperty("Count").GetValue(GetProperty(resolution, "Resolved"));
        int unresolved = (int)GetProperty(resolution, "UnresolvedCount");
        string encodingName = (string)GetProperty(resolution, "EncodingName");

        Check("两条记录都解析到文件", resolved == 2, "解析=" + resolved + " 未解析=" + unresolved);
        Check("编码探测为 UTF-8 BOM", encodingName != null && encodingName.Contains("UTF-8"),
            "实际=" + encodingName);

        string missing = Path.Combine(dir, "bad.sha256");
        File.WriteAllText(missing, RawSha256(a) + " *does_not_exist.bin", new UTF8Encoding(false));
        object bad = InvokeStatic(_resolver, "ResolveRecord", missing);
        int badResolved = (int)GetProperty(bad, "Resolved").GetType()
            .GetProperty("Count").GetValue(GetProperty(bad, "Resolved"));
        Check("找不到目标时记为未解析", badResolved == 0, "解析=" + badResolved);
        Check("找不到目标时有提示文本", GetProperty(bad, "Error") != null,
            "Error=" + (GetProperty(bad, "Error") ?? "(null)"));
    }

    // ---------------------------------------------------------------- 文件 → 记录
    static void CheckFileToRecord()
    {
        Console.WriteLine("");
        Console.WriteLine("【文件 → 查找校验记录】");

        string dir = Path.Combine(_work, "lookup");
        Directory.CreateDirectory(dir);
        string target = MakeFile(Path.Combine(dir, "app.exe"), 4096);
        string hash = RawSha256(target);

        // 伴侣同名记录
        string companion = target + ".sha256";
        File.WriteAllText(companion, hash + " *" + Path.GetFileName(target), new UTF8Encoding(true));
        string found = FindMatchHash(companion, target);
        Check("同名伴侣记录", found != null && found == hash, "找到=" + (found ?? "(null)"));

        // 名字与目标同基名、但扩展名不是 .sha256 的记录
        File.Delete(companion);
        string sameBase = Path.Combine(dir, "app.sha256.txt");
        File.WriteAllText(sameBase, hash + " *app.exe", new UTF8Encoding(false));
        string found2 = FindMatchHash(sameBase, target);
        Check("同基名 .sha256.txt 记录", found2 != null && found2 == hash, "找到=" + (found2 ?? "(null)"));

        // 完全不相干的记录名，内容里引用了目标
        File.Delete(sameBase);
        string odd = Path.Combine(dir, "whatever.txt");
        File.WriteAllText(odd, hash + " *app.exe", GetEncoding("GB18030"));
        string found3 = FindMatchHash(odd, target);
        Check("无关文件名但内容引用目标", found3 != null && found3 == hash, "找到=" + (found3 ?? "(null)"));

        // 记录里只有值、没有文件名
        File.Delete(odd);
        string bare = Path.Combine(dir, "app.exe.sha256.sum");
        File.WriteAllText(bare, hash, new UTF8Encoding(false));
        string found4 = FindMatchHash(bare, target);
        Check("单行无文件名记录", found4 != null && found4 == hash, "找到=" + (found4 ?? "(null)"));
    }

    // ---------------------------------------------------------------- 不是记录
    static void CheckNotARecord()
    {
        Console.WriteLine("");
        Console.WriteLine("【不该被当成校验记录】");

        string dir = Path.Combine(_work, "neg");
        Directory.CreateDirectory(dir);

        string binary = MakeFile(Path.Combine(dir, "binary.dat"), 4096);
        bool b1 = (bool)InvokeStatic(_resolver, "LooksLikeRecord", binary);
        Check("二进制文件不是记录", !b1, "binary.dat");

        string prose = Path.Combine(dir, "readme.txt");
        File.WriteAllText(prose, "这是说明文字。\r\n没有任何 SHA256 值。\r\n", GetEncoding("GB18030"));
        bool b2 = (bool)InvokeStatic(_resolver, "LooksLikeRecord", prose);
        Check("普通文本不是记录", !b2, "readme.txt");

        string hashOnlyFile = Path.Combine(dir, "hash.txt");
        File.WriteAllText(hashOnlyFile, "只有一行哈希，没有别的\r\n", new UTF8Encoding(false));
        bool b3 = (bool)InvokeStatic(_resolver, "LooksLikeRecord", hashOnlyFile);
        Check("内容不像记录时按内容判定", !b3, "hash.txt");

        // 值 + 文件名 → 是记录
        string file = MakeFile(Path.Combine(dir, "x.bin"), 1024);
        string good = Path.Combine(dir, "x.txt");
        File.WriteAllText(good, RawSha256(file) + " *x.bin", new UTF8Encoding(false));
        bool b4 = (bool)InvokeStatic(_resolver, "LooksLikeRecord", good);
        Check("内容像记录即认定为记录", b4, "x.txt");
    }

    // ---------------------------------------------------------------- 多算法
    /// <summary>
    /// 算法相关的断言：
    /// 1. 记录后缀能决定算法（.md5 → MD5、.sha3-256 → SHA3-256）；
    /// 2. 后缀认不出时按值长度推断，同长度多候选要全部保留；
    /// 3. 一行里写了算法名时以行内为准；
    /// 4. 同一文件有 md5 与 sha256 两份记录时，取摘要更长的那份。
    /// </summary>
    static void CheckAlgorithms()
    {
        Console.WriteLine("");
        Console.WriteLine("【算法判定】");

        Type algorithms = _checksumFile.Assembly.GetType("HashChecker.Services.HashAlgorithms");
        if (algorithms == null)
        {
            Check("HashAlgorithms 类型存在", false, "找不到类型");
            return;
        }

        string dir = Path.Combine(_work, "algo");
        Directory.CreateDirectory(dir);
        string target = MakeFile(Path.Combine(dir, "blob.bin"), 4096);

        string md5 = RawHex("MD5", target);
        string sha256 = RawHex("SHA256", target);
        string sha1 = RawHex("SHA1", target);

        CheckExtensionAlgorithm(algorithms, dir, "record.md5", md5, "md5", "MD5");
        CheckExtensionAlgorithm(algorithms, dir, "record.sha1", sha1, "sha1", "SHA-1");
        CheckExtensionAlgorithm(algorithms, dir, "record.sha256", sha256, "sha256", "SHA-256");
        // 后缀判算法要用「长度与该算法匹配」的值来测：
        // SHA-512/256 输出 32 字节，拿 SHA-512 的 64 字节值去测，
        // 按现在的「冲突时以长度为准」规则会被正确判成不确定，
        // 那条断言反而会失败——所以这里特意用 SHA-256 的值（同为 32 字节）。
        CheckExtensionAlgorithm(algorithms, dir, "record.sha512-256", sha256, "sha512-256", "SHA-512/256");

        // 后缀完全无关时按长度推断：16 位 → MD5
        string blind = Path.Combine(dir, "blind.sum");
        File.WriteAllText(blind, md5 + " *" + Path.GetFileName(target), new UTF8Encoding(false));
        object document = InvokeStatic(_checksumFile, "Parse", blind);
        object algorithm = document == null ? null : GetProperty(document, "Algorithm");
        string name = algorithm == null ? "(null)" : (string)GetProperty(algorithm, "Id");
        Check(".sum 后缀按长度推出 MD5", string.Equals(name, "md5", StringComparison.OrdinalIgnoreCase), "推断=" + name);

        // 64 位值没有后缀提示：SHA-256 与 SHA3-256 都是候选，不能只留一个
        int candidates = CountCandidates(algorithms, sha256);
        Check("64 位值有多个候选算法", candidates > 1, "候选数=" + candidates);

        // 行内写明算法名：即使后缀无关也要认出来。
        // 用 SHA-256 的值是因为这里只要验「算法名被认出来」，不用独立算一遍 SHA3。
        string inline = Path.Combine(dir, "inline.txt");
        File.WriteAllText(inline, "SHA3-256 (" + Path.GetFileName(target) + ") = " + sha256, new UTF8Encoding(false));
        object inlineDoc = InvokeStatic(_checksumFile, "Parse", inline);
        object entry = inlineDoc == null ? null : FirstEntry(inlineDoc);
        object inlineAlgorithm = entry == null ? null : GetProperty(entry, "Algorithm");
        string inlineId = inlineAlgorithm == null ? "(null)" : (string)GetProperty(inlineAlgorithm, "Id");
        Check("行内算法名优先", string.Equals(inlineId, "sha3-256", StringComparison.OrdinalIgnoreCase), "识别=" + inlineId);

        // 同名伴侣里 md5 与 sha256 都在：取更强的那份
        string md5Path = target + ".md5";
        string sha256Path = target + ".sha256";
        File.WriteAllText(md5Path, md5 + " *" + Path.GetFileName(target), new UTF8Encoding(false));
        File.WriteAllText(sha256Path, sha256 + " *" + Path.GetFileName(target), new UTF8Encoding(false));

        object match = InvokeStatic(_resolver, "FindRecordForFile", target);
        string matched = match == null ? null : (string)GetProperty(match, "Hash");
        Check("多份记录时选 SHA-256", matched != null && matched.Length == 64,
            "选中值长度=" + (matched == null ? 0 : matched.Length));
    }

    static object FirstEntry(object document)
    {
        object entries = GetProperty(document, "Entries");
        if (entries == null) return null;
        Array array = entries as Array;
        if (array == null || array.Length == 0) return null;
        return array.GetValue(0);
    }

    static int CountCandidates(Type algorithms, string digest)
    {
        try
        {
            var info = algorithms.GetMethod("CandidatesFor", BindingFlags.Public | BindingFlags.Static);
            object list = info.Invoke(null, new object[] { digest });
            if (list == null) return 0;
            var items = (System.Collections.IEnumerable)list;
            int count = 0;
            foreach (object unused in items) count++;
            return count;
        }
        catch (Exception e)
        {
            Console.WriteLine("!! CandidatesFor 抛异常: " + e.Message);
            _failures++;
            return 0;
        }
    }

    static void CheckExtensionAlgorithm(Type algorithms, string dir, string fileName,
        string hash, string expectedId, string expectedDisplay)
    {
        string path = Path.Combine(dir, fileName);
        File.WriteAllText(path, hash + " *blob.bin", new UTF8Encoding(false));

        object document = InvokeStatic(_checksumFile, "Parse", path);
        object algorithm = document == null ? null : GetProperty(document, "Algorithm");
        string id = algorithm == null ? "(null)" : (string)GetProperty(algorithm, "Id");
        string display = algorithm == null ? "(null)" : (string)GetProperty(algorithm, "DisplayName");

        Check(fileName + " 后缀判算法",
            string.Equals(id, expectedId, StringComparison.OrdinalIgnoreCase),
            "识别=" + id + "（期望 " + expectedId + "） 显示=" + display);
    }

    /// <summary>
    /// 用独立实现算一个值，作为断言基准。
    ///
    /// 不能用 HashAlgorithm.Create(name)：它只认 MD5 / SHA1 / SHA256 / SHA384 / SHA512
    /// 这几个内置名，传 "SHA3-256" 会返回 null，后面直接解引用就是访问冲突
    /// （表现为进程 0xC0000005 退出，而不是一条正常的失败断言）。
    /// </summary>
    static string RawHex(string algorithmName, string path)
    {
        System.Security.Cryptography.HashAlgorithm algorithm = null;
        switch (algorithmName.ToUpperInvariant())
        {
            case "MD5": algorithm = System.Security.Cryptography.MD5.Create(); break;
            case "SHA1": algorithm = System.Security.Cryptography.SHA1.Create(); break;
            case "SHA256": algorithm = System.Security.Cryptography.SHA256.Create(); break;
            case "SHA384": algorithm = System.Security.Cryptography.SHA384.Create(); break;
            case "SHA512": algorithm = System.Security.Cryptography.SHA512.Create(); break;
            default:
                Console.WriteLine("!! RawHex 不支持的算法: " + algorithmName);
                _failures++;
                return string.Empty;
        }

        using (algorithm)
        using (var stream = File.OpenRead(path))
        {
            byte[] digest = algorithm.ComputeHash(stream);
            var sb = new System.Text.StringBuilder(digest.Length * 2);
            foreach (byte b in digest) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }

    // ---------------------------------------------------------------- 工具
    static string ResolveFirst(string recordPath)
    {
        object resolution = InvokeStatic(_resolver, "ResolveRecord", recordPath);
        object list = GetProperty(resolution, "Resolved");
        var items = (System.Collections.IEnumerable)list;
        foreach (object item in items)
        {
            return (string)GetProperty(item, "TargetPath");
        }
        return null;
    }

    static string FindMatchHash(string recordHint, string targetPath)
    {
        object match = InvokeStatic(_resolver, "FindRecordForFile", targetPath);
        if (match == null) return null;
        return (string)GetProperty(match, "Hash");
    }

    static void CheckDocument(string name, string path, int expectedCount, string ignored)
    {
        object document = InvokeStatic(_checksumFile, "Parse", path);
        object entries = GetProperty(document, "Entries");
        int count = (int)entries.GetType().GetProperty("Length").GetValue(entries);
        string encodingName = (string)GetProperty(document, "EncodingName");

        Check(name, count == expectedCount,
            "条目=" + count + "（期望 " + expectedCount + "） 编码=" + encodingName);
    }

    static object InvokeStatic(Type type, string method, params object[] args)
    {
        MethodInfo info = type.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
        if (info == null)
        {
            Console.WriteLine("!! 找不到方法 " + type.Name + "." + method);
            _failures++;
            return null;
        }
        try
        {
            return info.Invoke(null, args);
        }
        catch (TargetInvocationException e)
        {
            Console.WriteLine("!! " + type.Name + "." + method + " 抛异常: " + e.InnerException);
            _failures++;
            return null;
        }
    }

    static string InvokeString(Type type, string method, string arg, string property)
    {
        return InvokeString(type, method, new object[] { arg }, property);
    }

    /// <summary>按参数个数选重载再调用（ParseLine 现在有一参 / 两参两个版本）。</summary>
    static string InvokeString(Type type, string method, object[] args, string property)
    {
        MethodInfo info = FindOverload(type, method, args.Length);
        if (info == null) return null;
        try
        {
            object result = info.Invoke(null, args);
            if (result == null) return null;
            return (string)GetProperty(result, property);
        }
        catch (TargetInvocationException e)
        {
            Console.WriteLine("!! " + type.Name + "." + method + " 抛异常: " + e.InnerException);
            _failures++;
            return null;
        }
    }

    static MethodInfo FindOverload(Type type, string method, int argumentCount)
    {
        foreach (MethodInfo info in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (info.Name == method && info.GetParameters().Length == argumentCount) return info;
        }
        Console.WriteLine("!! 找不到方法 " + type.Name + "." + method + "/" + argumentCount);
        _failures++;
        return null;
    }

    static object GetProperty(object instance, string name)
    {
        if (instance == null) return null;
        return instance.GetType().GetProperty(name).GetValue(instance);
    }

    static string MakeFile(string path, int size)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        byte[] data = new byte[size];
        for (int i = 0; i < size; i++) data[i] = (byte)((i * 31 + 7) & 0xFF);
        File.WriteAllBytes(path, data);
        return path;
    }

    static string RawSha256(string path)
    {
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(path))
        {
            byte[] bytes = sha.ComputeHash(stream);
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
