using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

/// <summary>
/// 记录内路径边界（V01 维度）：`..` 穿越、UNC、末分隔符、超长路径、盘符根。
///
/// 与 recordprobe 分开，是因为它守的是**安全边界**而不是解析正确性：
/// 记录文件的内容不可信，它写什么路径都不该让程序跑到记录所在目录之外去认文件。
///
/// 每个用例都要求 ResolveRecord 不抛异常，并明确断言「该接受的接受、该拒绝的拒绝」。
/// 用法: pathprobe.exe &lt;被测 exe 路径&gt; [工作目录]
/// 退出码: 0 = 全部符合预期, 1 = 有失败项, 2 = 用法错误
/// </summary>
class PathProbe
{
    static int _failures;
    static string _work;
    static Type _resolver;

    static void Check(string name, bool ok, string detail)
    {
        if (!ok) _failures++;
        Console.WriteLine("  {0} {1}{2}", ok ? "OK  " : "!!  ", name,
            string.IsNullOrEmpty(detail) ? "" : "  —— " + detail);
    }

    static object Call(Type t, string name, params object[] args)
    {
        MethodInfo m = null;
        foreach (MethodInfo candidate in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (candidate.Name != name) continue;
            if (candidate.GetParameters().Length != args.Length) continue;
            m = candidate;
            break;
        }
        if (m == null) throw new MissingMethodException(t.Name + "." + name);
        return m.Invoke(null, args);
    }

    /// <summary>解析一份记录，返回 (目标路径列表, 未解析条数, 是否抛异常, 异常说明)。</summary>
    static List<string> Resolve(string recordPath, out int unresolved, out string error)
    {
        unresolved = -1;
        error = null;
        var targets = new List<string>();
        try
        {
            object resolution = Call(_resolver, "ResolveRecord", recordPath);
            object list = resolution.GetType().GetProperty("Resolved").GetValue(resolution);
            foreach (object entry in (System.Collections.IEnumerable)list)
            {
                targets.Add((string)entry.GetType().GetProperty("TargetPath").GetValue(entry));
            }
            unresolved = (int)resolution.GetType().GetProperty("UnresolvedCount").GetValue(resolution);
        }
        catch (Exception ex)
        {
            Exception inner = ex is TargetInvocationException ? ex.InnerException : ex;
            error = inner.GetType().Name + ": " + inner.Message;
        }
        return targets;
    }

    static string Hex64()
    {
        return new string('a', 64);
    }

    static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("用法: pathprobe.exe <被测 exe 路径> [工作目录]");
            return 2;
        }

        string asmPath = Path.GetFullPath(args[0]);
        _work = args.Length > 1
            ? Path.GetFullPath(args[1])
            : Path.Combine(Path.GetTempPath(), "HashCheckerPathProbe");

        if (Directory.Exists(_work)) Directory.Delete(_work, true);
        Directory.CreateDirectory(_work);

        var asm = Assembly.LoadFrom(asmPath);
        _resolver = asm.GetType("HashChecker.Services.ChecksumResolver");
        if (_resolver == null)
        {
            Console.WriteLine("!! 找不到 ChecksumResolver");
            return 1;
        }

        Console.WriteLine("=== 记录内路径边界验证 ===");
        Console.WriteLine("工作目录: " + _work);
        Console.WriteLine("");

        // 目录布局：
        //   <work>/inner/        —— 记录放这里
        //   <work>/secret.txt    —— 上一级，绝不该被记录里写的相对路径摸到
        string inner = Path.Combine(_work, "inner");
        Directory.CreateDirectory(inner);
        string secret = Path.Combine(_work, "secret.txt");
        File.WriteAllText(secret, "SECRET-OUTSIDE-RECORD-DIR");

        int unresolved;

        // ---------------------------------------------------------------- 1 .. 穿越
        string traversal = Path.Combine(inner, "trav.sha256");
        File.WriteAllText(traversal, Hex64() + " *../secret.txt\n");
        string err1;
        List<string> targets1 = Resolve(traversal, out unresolved, out err1);
        bool escaped = false;
        foreach (string t in targets1)
        {
            if (string.Equals(Path.GetFullPath(t), Path.GetFullPath(secret),
                StringComparison.OrdinalIgnoreCase)) escaped = true;
        }
        Check("`..` 穿越记录目录", err1 == null && !escaped,
            err1 != null
                ? "抛了 " + err1
                : escaped
                    ? "记录里的 ../secret.txt 被认成目标文件，越出了记录所在目录"
                    : string.Format("解析出 {0} 个目标，均未越出 inner\\", targets1.Count));

        // ---------------------------------------------------------------- 2 末分隔符
        string trailing = Path.Combine(inner, "trail.md5");
        File.WriteAllText(trailing, new string('b', 32) + " *payload.txt\\\n");
        File.WriteAllText(Path.Combine(inner, "payload.txt"), "payload");
        string err2;
        List<string> targets2 = Resolve(trailing, out unresolved, out err2);
        Check("记录末尾带分隔符不抛异常", err2 == null,
            err2 != null
                ? "抛了 " + err2
                : string.Format("解析 {0} 个、未解析 {1} 个", targets2.Count, unresolved));

        // ---------------------------------------------------------------- 3 盘符根
        string rootRec = Path.Combine(inner, "root.sha256");
        File.WriteAllText(rootRec, Hex64() + " *C:\\\n");
        string err3;
        List<string> targets3 = Resolve(rootRec, out unresolved, out err3);
        Check("记录指向盘符根不抛异常", err3 == null,
            err3 != null ? "抛了 " + err3 : string.Format("解析 {0} 个", targets3.Count));

        // ---------------------------------------------------------------- 4 超长路径
        string longRec = Path.Combine(inner, "long.sha256");
        File.WriteAllText(longRec, Hex64() + " *" + new string('c', 300) + ".txt\n");
        string err4;
        List<string> targets4 = Resolve(longRec, out unresolved, out err4);
        Check("记录里写超长文件名（300 字符）不抛异常", err4 == null,
            err4 != null ? "抛了 " + err4 : string.Format("解析 {0} 个", targets4.Count));

        // ---------------------------------------------------------------- 5 UNC 路径
        string uncRec = Path.Combine(inner, "unc.sha256");
        File.WriteAllText(uncRec, Hex64() + " *\\\\server\\share\\x.txt\n");
        string err5;
        List<string> targets5 = Resolve(uncRec, out unresolved, out err5);
        Check("记录指向 UNC 路径不抛异常", err5 == null,
            err5 != null ? "抛了 " + err5 : string.Format("解析 {0} 个", targets5.Count));

        // ---------------------------------------------------------------- 6 非法字符
        string badRec = Path.Combine(inner, "bad.sha256");
        File.WriteAllText(badRec, Hex64() + " *bad\0name.txt\n");
        string err6;
        List<string> targets6 = Resolve(badRec, out unresolved, out err6);
        Check("记录里的文件名含 NUL 不抛异常", err6 == null,
            err6 != null ? "抛了 " + err6 : string.Format("解析 {0} 个", targets6.Count));

        // ---------------------------------------------------------------- 7 绝对路径（既有设计：接受）
        string absRec = Path.Combine(inner, "abs.sha256");
        File.WriteAllText(absRec, Hex64() + " *" + secret + "\n");
        string err7;
        List<string> targets7 = Resolve(absRec, out unresolved, out err7);
        Check("记录写绝对路径", err7 == null,
            err7 != null
                ? "抛了 " + err7
                : targets7.Count > 0
                    ? "绝对路径被接受（既有设计：记录本来就允许写绝对路径）"
                    : "绝对路径未被接受");

        Console.WriteLine("");
        if (_failures == 0)
        {
            Console.WriteLine("全部符合预期 ✓");
            return 0;
        }
        Console.WriteLine("失败 " + _failures + " 项");
        return 1;
    }
}
