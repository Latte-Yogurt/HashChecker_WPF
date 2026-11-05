using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

/// <summary>
/// 「自验证 / 计算器读文件」不再卡死界面的验证。
///
/// 这两个入口原来都在 UI 线程上同步读文件并算摘要：用 SHA3 算 24MB 要三秒多，
/// 这段时间窗口不重绘、点关闭没反应——用户看到的就是卡死。
///
/// 判断方式：反汇编方法体的 IL，看里面有没有 Task.Run / AsyncTaskMethodBuilder。
/// 有就说明计算被派发到后台线程；若方法体里同时还有 FileStream + Append 的同步循环，
/// 那就是仍留在 UI 线程上算。
///
/// 为什么不用「调用入口再测耗时」：自验证算完会弹 MessageBox，在无消息循环的
/// 探针进程里那是一次模态调用，会直接把探针挡住——测出来的不是「卡不卡 UI」，
/// 而是「探针自己会不会死等」。IL 判据不运行代码，稳且准确。
///
/// 用法: hangprobe.exe &lt;HashChecker.exe&gt; [工作目录]
/// 退出码: 0 = 通过, 1 = 有不符合项, 2 = 用法错误
/// </summary>
class HangProbe
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
            Console.WriteLine("用法: hangprobe.exe <HashChecker.exe> [工作目录]");
            return 2;
        }

        string asmPath = Path.GetFullPath(args[0]);

        _work = args.Length > 1
            ? Path.GetFullPath(args[1])
            : Path.Combine(Path.GetTempPath(), "HashCheckerHangProbe");

        if (Directory.Exists(_work)) Directory.Delete(_work, true);
        Directory.CreateDirectory(_work);

        Assembly asm = Assembly.LoadFrom(asmPath);
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string simple = new AssemblyName(e.Name).Name;
            string candidate = Path.Combine(Path.GetDirectoryName(asmPath), simple + ".exe");
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        };

        Console.WriteLine("=== 界面卡死验证 ===");
        Console.WriteLine("被测程序: " + asmPath);
        Console.WriteLine("");

        Type windowType = asm.GetType("HashChecker.MainWindow");
        Type calcType = asm.GetType("HashChecker.CalculatorWindow");
        Type algorithms = asm.GetType("HashChecker.Services.HashAlgorithms");

        if (windowType == null)
        {
            Check("找到 MainWindow", false, "");
            return Finish();
        }

        Console.WriteLine("【自验证】");
        CheckDispatch(windowType, "SelfTest_Click", "自验证");
        CheckNoInlineFileLoop(windowType, "SelfTest_Click", "自验证");

        Console.WriteLine("");
        Console.WriteLine("【计算器读文件】");
        if (calcType == null)
        {
            Check("找到 CalculatorWindow", false, "");
        }
        else
        {
            CheckDispatch(calcType, "ComputeFromFile", "计算器读文件");
            CheckNoInlineFileLoop(calcType, "ComputeFromFile", "计算器读文件");
        }

        Console.WriteLine("");
        Console.WriteLine("【同步实现的真实代价】");
        MeasureSyncCost(windowType, algorithms);

        return Finish();
    }

    static int Finish()
    {
        Console.WriteLine("");
        if (_failures != 0)
        {
            Console.WriteLine("!! 有 " + _failures + " 项不符合预期");
            return 1;
        }
        Console.WriteLine("全部符合预期 ✓");
        return 0;
    }

    /// <summary>量「如果同步执行」要多久。用最慢的 SHA3-512 算 24MB。</summary>
    static void MeasureSyncCost(Type windowType, Type algorithms)
    {
        if (algorithms == null)
        {
            Check("找到 HashAlgorithms", false, "");
            return;
        }

        MethodInfo byId = algorithms.GetMethod("ById", BindingFlags.Public | BindingFlags.Static,
            null, new[] { typeof(string) }, null);
        object sha3 = byId == null ? null : byId.Invoke(null, new object[] { "sha3-512" });

        if (sha3 == null)
        {
            Check("找到 SHA3-512", false, "");
            return;
        }

        string big = Path.Combine(_work, "big.bin");
        MakeFile(big, 24 * 1024 * 1024);

        MethodInfo compute = windowType.GetMethod("ComputeFileHash",
            BindingFlags.NonPublic | BindingFlags.Static);

        if (compute == null)
        {
            Check("读到文件并算出摘要的实现存在", false, "找不到 ComputeFileHash");
            return;
        }

        var sw = Stopwatch.StartNew();
        compute.Invoke(null, new object[] { big, sha3 });
        sw.Stop();

        Console.WriteLine("  SHA3-512 算 24MB 需 {0:N0} ms——若同步跑在 UI 线程，界面就卡这么久",
            sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>方法体 IL 里是否出现异步派发的痕迹。</summary>
    static void CheckDispatch(Type owner, string methodName, string label)
    {
        MethodInfo method = owner.GetMethod(methodName,
            BindingFlags.NonPublic | BindingFlags.Instance);

        if (method == null)
        {
            Check(label + " 找到入口 " + methodName, false, "方法不存在");
            return;
        }

        string il = ReadIlText(method);
        if (il == null)
        {
            Check(label + " 能读到方法体", false, "拿不到 IL");
            return;
        }

        // async 方法被编译成「外壳 + 状态机类」：
        // 外壳里只有 AsyncVoidMethodBuilder.Start 之类，
        // 真正的 await Task.Run 在状态机类的 MoveNext 里。
        // 所以先看外壳，再顺着状态机找 MoveNext。
        bool dispatch = ContainsDispatch(il);

        if (!dispatch)
        {
            string stateIl = ReadStateMachineIl(owner, method.Name);
            if (stateIl != null) dispatch = ContainsDispatch(stateIl);
        }

        Check(label + " 把计算派发到后台线程", dispatch,
            dispatch ? "" : "外壳与状态机里都没有异步派发痕迹");
    }

    /// <summary>IL 文本里是否有异步派发的标记。</summary>
    static bool ContainsDispatch(string il)
    {
        return il.Contains("Task.Run")
               || il.Contains("AsyncVoidMethodBuilder")
               || il.Contains("AsyncTaskMethodBuilder")
               || il.Contains("TaskFactory.StartNew")
               || il.Contains("ThreadPool");
    }

    /// <summary>
    /// 找到编译器为该 async 方法生成的状态机类，并读它的 MoveNext。
    /// 命名是 C# 编译器约定的 &lt;方法名&gt;d__N，嵌套在声明类里。
    /// </summary>
    static string ReadStateMachineIl(Type owner, string methodName)
    {
        foreach (Type nested in owner.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
        {
            string name = nested.Name;
            if (!name.StartsWith("<" + methodName + ">", StringComparison.Ordinal)) continue;

            MethodInfo moveNext = nested.GetMethod("MoveNext",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

            if (moveNext == null) continue;

            return ReadIlText(moveNext);
        }

        return null;
    }

    /// <summary>入口里不该还留着逐块读文件的同步循环。</summary>
    static void CheckNoInlineFileLoop(Type owner, string methodName, string label)
    {
        MethodInfo method = owner.GetMethod(methodName,
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (method == null) return;

        string il = ReadIlText(method);
        if (il == null) return;

        // 同步实现必然同时出现 FileStream 与 Append 的调用
        bool inline = il.Contains("FileStream") && il.Contains("IHashCore.Append");

        Check(label + " 入口内不再同步读文件", !inline,
            inline ? "入口里仍有 FileStream + Append 的同步循环" : "");
    }

    /// <summary>把方法体的 IL 粗粒度反汇编成文本，用于查名字。</summary>
    static string ReadIlText(MethodInfo method)
    {
        try
        {
            MethodBody body = method.GetMethodBody();
            if (body == null) return null;

            byte[] bytes = body.GetILAsByteArray();
            if (bytes == null) return null;

            Module module = method.Module;
            var sb = new StringBuilder();

            int i = 0;
            while (i < bytes.Length)
            {
                byte op = bytes[i];

                // 含元数据 token 的操作码：0x28=call 0x6F=callvirt 0x73=newobj
                // 0x72=ldstr 0x7E=ldsfld 0x7B=ldfld 0x74=castclass
                bool hasToken = op == 0x28 || op == 0x6F || op == 0x73
                                || op == 0x72 || op == 0x7E || op == 0x7B || op == 0x74;

                if (hasToken && i + 4 < bytes.Length)
                {
                    int token = BitConverter.ToInt32(bytes, i + 1);
                    sb.Append(' ');

                    try
                    {
                        if (op == 0x72)
                        {
                            sb.Append(module.ResolveString(token));
                        }
                        else
                        {
                            MemberInfo member = module.ResolveMember(token);
                            if (member != null && member.DeclaringType != null)
                            {
                                sb.Append(member.DeclaringType.Name).Append('.').Append(member.Name);
                            }
                            else if (member != null)
                            {
                                sb.Append(member.Name);
                            }
                        }
                    }
                    catch
                    {
                        // 解析不了的 token 跳过，不影响其他名字的收集
                    }

                    i += 5;
                    continue;
                }

                i++;
            }

            return sb.ToString();
        }
        catch
        {
            return null;
        }
    }

    static void MakeFile(string path, int size)
    {
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            var buffer = new byte[1024 * 1024];
            int seed = 11;
            int written = 0;

            while (written < size)
            {
                int take = Math.Min(buffer.Length, size - written);
                for (int i = 0; i < take; i++)
                {
                    seed = (seed * 1103515245 + 12345) & 0x7FFFFFFF;
                    buffer[i] = (byte)(seed >> 16);
                }

                stream.Write(buffer, 0, take);
                written += take;
            }
        }
    }
}
