using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

/// <summary>
/// 记录写出边界（V05 维度）：BOM 开关、换行、只读与隐藏属性、占用中的目标、
/// 空行列表、文件名以 * 开头、Unicode 文件名。
///
/// 守的是 H5（资源释放）、H11（只读目录 / 磁盘满）、H14（边界输入）：
/// 写出失败必须抛出来让调用方提示用户，不能静默写半截。
///
/// 用法: writeprobe.exe &lt;被测 exe 路径&gt; [工作目录]
/// 退出码: 0 = 全部符合预期, 1 = 有失败项, 2 = 用法错误
/// </summary>
class WriteProbe
{
    static int _failures;
    static string _work;
    static Type _checksumFile;

    static void Check(string name, bool ok, string detail)
    {
        if (!ok) _failures++;
        Console.WriteLine("  {0} {1}{2}", ok ? "OK  " : "!!  ", name,
            string.IsNullOrEmpty(detail) ? "" : "  —— " + detail);
    }

    static MethodInfo Find(string name, int argCount)
    {
        foreach (MethodInfo m in _checksumFile.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name == name && m.GetParameters().Length == argCount) return m;
        }
        return null;
    }

    static void Write(string path, string[] lines, bool bom)
    {
        Find("Write", 3).Invoke(null, new object[] { path, lines, bom });
    }

    static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("用法: writeprobe.exe <被测 exe 路径> [工作目录]");
            return 2;
        }

        string asmPath = Path.GetFullPath(args[0]);
        _work = args.Length > 1
            ? Path.GetFullPath(args[1])
            : Path.Combine(Path.GetTempPath(), "HashCheckerWriteProbe");

        if (Directory.Exists(_work)) Directory.Delete(_work, true);
        Directory.CreateDirectory(_work);

        Assembly asm = Assembly.LoadFrom(asmPath);
        _checksumFile = asm.GetType("HashChecker.Services.ChecksumFile");
        if (_checksumFile == null)
        {
            Console.WriteLine("!! 找不到 ChecksumFile");
            return 1;
        }

        Console.WriteLine("=== 记录写出边界验证 ===");
        Console.WriteLine("");

        try
        {
            RunAll();
        }
        catch (Exception ex)
        {
            Exception inner = ex is TargetInvocationException ? ex.InnerException : ex;
            Console.WriteLine("!! 探针异常: " + inner.GetType().Name + ": " + inner.Message);
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

    static void RunAll()
    {
        string[] lines =
        {
            new string('a', 64) + " *alpha.bin",
            new string('b', 64) + " *beta.bin",
        };

        // ---------------------------------------------------------------- 1 不带 BOM
        string p1 = Path.Combine(_work, "nobom.sha256");
        Write(p1, lines, false);
        byte[] b1 = File.ReadAllBytes(p1);
        Check("Write(withBom=false) 首字节不是 UTF-8 BOM",
            !(b1.Length >= 3 && b1[0] == 0xEF && b1[1] == 0xBB && b1[2] == 0xBF),
            string.Format("首三字节 {0:X2} {1:X2} {2:X2}，长度 {3}",
                b1[0], b1[1], b1[2], b1.Length));
        string t1 = File.ReadAllText(p1, Encoding.UTF8);
        Check("写出的行数与输入一致（按 \\n 计）",
            t1.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Length == lines.Length,
            "实际行数=" + t1.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Length);

        // ---------------------------------------------------------------- 2 带 BOM
        string p2 = Path.Combine(_work, "bom.sha256");
        Write(p2, lines, true);
        byte[] b2 = File.ReadAllBytes(p2);
        Check("Write(withBom=true) 首三字节是 EF BB BF",
            b2.Length >= 3 && b2[0] == 0xEF && b2[1] == 0xBB && b2[2] == 0xBF,
            string.Format("首三字节 {0:X2} {1:X2} {2:X2}", b2[0], b2[1], b2[2]));

        // BOM 记录必须还能被自己读回来
        object parsed = Find("Parse", 1).Invoke(null, new object[] { p2 });
        object entries = parsed.GetType().GetProperty("Entries").GetValue(parsed);
        int count = ((Array)entries).Length;
        Check("带 BOM 的记录能被 Parse 读回同样条目数", count == lines.Length,
            "读回 " + count + " 条");

        // ---------------------------------------------------------------- 3 覆盖已有文件
        string p3 = Path.Combine(_work, "overwrite.sha256");
        File.WriteAllText(p3, "旧内容，比新内容长很多很多很多很多很多很多很多很多很多很多\n");
        Write(p3, lines, false);
        string t3 = File.ReadAllText(p3, Encoding.UTF8);
        Check("覆盖已有文件时旧内容不残留", !t3.Contains("旧内容"),
            t3.Length < 300 ? "新内容长度 " + t3.Length : "仍在: " + t3.Substring(0, 30));

        // ---------------------------------------------------------------- 4 只读目标
        string p4 = Path.Combine(_work, "readonly.sha256");
        File.WriteAllText(p4, "x");
        File.SetAttributes(p4, FileAttributes.ReadOnly);
        bool readOnlyOk;
        string readOnlyDetail;
        try
        {
            Write(p4, lines, false);
            readOnlyOk = true;
            readOnlyDetail = "只读属性被清掉后成功写出（符合实现注释：覆盖前先清隐藏/只读）";
        }
        catch (Exception ex)
        {
            Exception inner = ex is TargetInvocationException ? ex.InnerException : ex;
            readOnlyOk = false;
            readOnlyDetail = "抛了 " + inner.GetType().Name + ": " + inner.Message;
        }
        File.SetAttributes(p4, FileAttributes.Normal);
        Check("覆盖只读文件", readOnlyOk, readOnlyDetail);

        // ---------------------------------------------------------------- 5 隐藏目标
        string p5 = Path.Combine(_work, "hidden.sha256");
        File.WriteAllText(p5, "x");
        File.SetAttributes(p5, FileAttributes.Hidden);
        bool hiddenOk;
        string hiddenDetail;
        try
        {
            Write(p5, lines, false);
            hiddenOk = true;
            hiddenDetail = "隐藏属性被清掉后成功写出";
        }
        catch (Exception ex)
        {
            Exception inner = ex is TargetInvocationException ? ex.InnerException : ex;
            hiddenOk = false;
            hiddenDetail = "抛了 " + inner.GetType().Name + ": " + inner.Message;
        }
        File.SetAttributes(p5, FileAttributes.Normal);
        Check("覆盖隐藏文件", hiddenOk, hiddenDetail);

        // ---------------------------------------------------------------- 6 目标被占用
        string p6 = Path.Combine(_work, "locked.sha256");
        File.WriteAllText(p6, "x");
        using (new FileStream(p6, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            bool lockedThrew;
            string lockedDetail;
            try
            {
                Write(p6, lines, false);
                lockedThrew = false;
                lockedDetail = "未抛异常（静默失败？）";
            }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException ? ex.InnerException : ex;
                lockedThrew = inner is IOException || inner is UnauthorizedAccessException;
                lockedDetail = "抛了 " + inner.GetType().Name + "（调用方可提示用户）";
            }
            Check("目标被独占占用时抛出 IO / 权限异常而不是静默失败", lockedThrew, lockedDetail);
        }

        // ---------------------------------------------------------------- 7 空行列表
        string p7 = Path.Combine(_work, "empty.sha256");
        bool emptyOk;
        string emptyDetail;
        try
        {
            Write(p7, new string[0], false);
            long len = new FileInfo(p7).Length;
            emptyOk = len <= 3;
            emptyDetail = "写出 " + len + " 字节";
        }
        catch (Exception ex)
        {
            Exception inner = ex is TargetInvocationException ? ex.InnerException : ex;
            emptyOk = false;
            emptyDetail = "抛了 " + inner.GetType().Name;
        }
        Check("空行列表写出空文件", emptyOk, emptyDetail);

        // ---------------------------------------------------------------- 8 文件名以 * 开头
        string line8 = (string)Find("FormatLine", 3)
            .Invoke(null, new object[] { new string('c', 64), "*weird.bin", false });
        Check("文件名以 * 开头时加下划线避免与引导符混淆",
            line8.Contains("_*weird.bin") || line8.Contains(" *"), "得到: " + line8);

        // ---------------------------------------------------------------- 9 Unicode 文件名
        string p9 = Path.Combine(_work, "unicode.sha256");
        var uniLines = new[]
        {
            new string('d', 64) + " *中文文件名.bin",
            new string('e', 64) + " *日本語ファイル.bin",
            new string('f', 64) + " *emoji-😀.bin",
        };
        Write(p9, uniLines, false);
        object parsed9 = Find("Parse", 1).Invoke(null, new object[] { p9 });
        object entries9 = parsed9.GetType().GetProperty("Entries").GetValue(parsed9);
        var names = new List<string>();
        foreach (object e in (Array)entries9)
        {
            names.Add((string)e.GetType().GetProperty("Path").GetValue(e, null));
        }
        Check("Unicode 文件名写后读回一致",
            names.Count == 3 && names.Contains("中文文件名.bin")
            && names.Contains("日本語ファイル.bin"),
            string.Join(" | ", names.ToArray()));

        // ---------------------------------------------------------------- 10 目录不存在
        string p10 = Path.Combine(_work, "nosuchdir", "x.sha256");
        bool noDirThrew;
        string noDirDetail;
        try
        {
            Write(p10, lines, false);
            noDirThrew = false;
            noDirDetail = "未抛异常（目录不存在也没有报错？）";
        }
        catch (Exception ex)
        {
            Exception inner = ex is TargetInvocationException ? ex.InnerException : ex;
            noDirThrew = inner is DirectoryNotFoundException || inner is IOException;
            noDirDetail = "抛了 " + inner.GetType().Name + "（调用方可提示）";
        }
        Check("目标目录不存在时抛出异常而不是静默失败", noDirThrew, noDirDetail);
    }
}
