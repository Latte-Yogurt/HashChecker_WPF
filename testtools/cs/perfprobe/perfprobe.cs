using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

/// <summary>
/// 吞吐性能基准（对应需求 1「极致的性能」）。
///
/// 测的是产品真实的热路径：HashingEngine.HashFile 读文件 + 喂增量内核。
/// 用反射调产品的 HashingEngine，而不是在探针里重写一遍——
/// 重写的版本测不到产品里的缓冲复用与进度节流改动。
///
/// 用法: perfprobe.exe &lt;HashChecker.exe&gt; [工作目录] [--quick]
/// 退出码: 0 = 跑完（不判失败），2 = 用法错误
/// </summary>
class PerfProbe
{
    static void Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("用法: perfprobe.exe <HashChecker.exe> [工作目录] [--quick]");
            Environment.Exit(2);
        }

        string asmPath = Path.GetFullPath(args[0]);
        bool quick = Array.IndexOf(args, "--quick") >= 0;

        string work = args.Length > 1 && !args[1].StartsWith("--")
            ? Path.GetFullPath(args[1])
            : Path.Combine(Path.GetTempPath(), "HashCheckerPerfProbe");

        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);

        var asm = Assembly.LoadFrom(asmPath);
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string simple = new AssemblyName(e.Name).Name;
            string candidate = Path.Combine(Path.GetDirectoryName(asmPath), simple + ".exe");
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        };

        Type engineType = asm.GetType("HashChecker.Services.HashingEngine");
        Type algorithmType = asm.GetType("HashChecker.Services.HashAlgorithms");
        if (engineType == null || algorithmType == null)
        {
            Console.WriteLine("!! 找不到 HashingEngine / HashAlgorithms");
            Environment.Exit(1);
        }

        Console.WriteLine("=== 吞吐基准 ===");
        Console.WriteLine("被测程序: " + asmPath);

        // 单文件吞吐：256MB（quick 模式 64MB）
        int megabytes = quick ? 64 : 256;
        string big = Path.Combine(work, "big.bin");
        MakeFile(big, megabytes * 1024 * 1024);

        Console.WriteLine("");
        Console.WriteLine("【单文件吞吐】");
        MeasureSingle(engineType, algorithmType, big, "SHA-256", megabytes);
        MeasureSingle(engineType, algorithmType, big, "MD5", megabytes);
        MeasureSingle(engineType, algorithmType, big, "SHA3-256", megabytes);
        MeasureSingle(engineType, algorithmType, big, "SHA-512", megabytes);
        MeasureSingle(engineType, algorithmType, big, "CRC-32", megabytes);

        // 多小文件：批量任务里最常见的形态，验缓冲复用是否生效
        Console.WriteLine("");
        Console.WriteLine("【多小文件（缓冲复用）】");
        int count = quick ? 500 : 2000;
        string dir = Path.Combine(work, "many");
        Directory.CreateDirectory(dir);
        for (int i = 0; i < count; i++)
        {
            MakeFile(Path.Combine(dir, "f" + i + ".bin"), 64 * 1024);
        }
        MeasureBatch(engineType, algorithmType, dir, count);

        Console.WriteLine("");
        Console.WriteLine("（基准只报数，不判失败）");
        Environment.Exit(0);
    }

    static void MeasureSingle(Type engineType, Type algorithmType, string path, string algorithmName, int megabytes)
    {
        object engine = Activator.CreateInstance(engineType);
        object algorithm = FindAlgorithm(algorithmType, algorithmName);
        if (algorithm == null)
        {
            Console.WriteLine("  !! 找不到算法 " + algorithmName);
            return;
        }

        // 预热：JIT 与文件缓存都会影响第一次的数字
        RunOnce(engine, algorithm, path);

        var sw = new Stopwatch();
        sw.Start();
        RunOnce(engine, algorithm, path);
        sw.Stop();

        double seconds = sw.Elapsed.TotalSeconds;
        double mbPerSecond = seconds <= 0 ? 0 : megabytes / seconds;

        Console.WriteLine(string.Format("  {0,-9} {1,7:N1} MB/s   （{2} MB，{3:N0} ms）",
            algorithmName, mbPerSecond, megabytes, sw.Elapsed.TotalMilliseconds));
    }

    static void MeasureBatch(Type engineType, Type algorithmType, string dir, int count)
    {
        string[] files = Directory.GetFiles(dir);
        Array.Sort(files);

        object engine = Activator.CreateInstance(engineType);
        object algorithm = FindAlgorithm(algorithmType, "SHA-256");
        if (algorithm == null) return;

        var sw = new Stopwatch();
        long totalBytes = 0;

        // 预热
        RunBatch(engine, algorithm, files, out totalBytes);

        sw.Start();
        RunBatch(engine, algorithm, files, out totalBytes);
        sw.Stop();

        double seconds = sw.Elapsed.TotalSeconds;
        double mb = totalBytes / (1024.0 * 1024.0);
        double mbPerSecond = seconds <= 0 ? 0 : mb / seconds;

        Console.WriteLine(string.Format("  {0} 个 64KB 文件  {1,7:N1} MB/s   （{2:N1} MB，{3:N0} ms，{4:N0} 个/秒）",
            count, mbPerSecond, mb, sw.Elapsed.TotalMilliseconds, count / seconds));
    }

    static void RunOnce(object engine, object algorithm, string path)
    {
        InvokeHash(engine, algorithm, path);
    }

    static void RunBatch(object engine, object algorithm, string[] files, out long totalBytes)
    {
        totalBytes = 0;
        foreach (string file in files)
        {
            totalBytes += new FileInfo(file).Length;
            InvokeHash(engine, algorithm, file);
        }
    }

    /// <summary>缓存的 HashFile 方法：每文件调一次，逐次反射查找会把固定开销放大成主项。</summary>
    static MethodInfo _hashFile;
    static int _hashFileArgc;

    /// <summary>缓存的「算法列表」构造器与实例：同一算法复用同一个列表。</summary>
    static object _cachedList;
    static object _cachedAlgorithm;
    static Type _cachedListType;

    /// <summary>
    /// 通过反射调 HashFile。它是 private 的，但它就是产品的真实热路径；
    /// 直接测私有方法是为了不把基准写成「重写一遍算法」那种假测试。
    ///
    /// 注意：反射本身有固定开销，批量测上千个小文件时必须把它缓存掉，
    /// 否则测出来的数字主要反映反射快慢，而不是产品吞吐。
    /// </summary>
    static void InvokeHash(object engine, object algorithm, string path)
    {
        if (_hashFile == null)
        {
            _hashFile = engine.GetType().GetMethod("HashFile",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (_hashFile == null)
            {
                Console.WriteLine("  !! 找不到 HashFile");
                return;
            }

            // HashFile 后来加了 knownSize 参数（用于校正实际读到的字节数）。
            // 按参数个数判断该传几个，探针才不会因为签名变化而崩。
            _hashFileArgc = _hashFile.GetParameters().Length;
        }

        if (_cachedList == null || !_cachedAlgorithm.Equals(algorithm))
        {
            _cachedAlgorithm = algorithm;
            _cachedListType = typeof(List<>).MakeGenericType(algorithm.GetType());
            _cachedList = Activator.CreateInstance(_cachedListType);

            ((System.Collections.IList)_cachedList).Add(algorithm);
        }

        try
        {
            object[] argv = _hashFileArgc >= 4
                ? new object[] { path, _cachedList, (long)0, CancellationToken.None }
                : new object[] { path, _cachedList, CancellationToken.None };
            _hashFile.Invoke(engine, argv);
        }
        catch (TargetInvocationException e)
        {
            Console.WriteLine("  !! HashFile 抛异常: " + e.InnerException.Message);
        }
    }

    static object FindAlgorithm(Type algorithmType, string displayName)
    {
        object all = algorithmType.GetProperty("All", BindingFlags.Public | BindingFlags.Static).GetValue(null);
        foreach (object item in (System.Collections.IEnumerable)all)
        {
            string name = (string)item.GetType().GetProperty("DisplayName").GetValue(item);
            if (string.Equals(name, displayName, StringComparison.OrdinalIgnoreCase)) return item;
        }
        return null;
    }

    static void MakeFile(string path, int size)
    {
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            var buffer = new byte[1024 * 1024];
            int written = 0;
            int seed = 17;
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
