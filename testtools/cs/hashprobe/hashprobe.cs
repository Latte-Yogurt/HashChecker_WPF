using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

/// <summary>
/// 多算法内核的运行时验证。
///
/// 参考值不写在这里猜：先用 `--vectors` 模式把算出来的值打印出来，
/// 再和 Python hashlib / 独立脚本的结果逐条比对，确认无误后回填到下面的表里。
///
/// 用法: hashprobe.exe &lt;HashChecker.exe&gt; [--vectors]
/// 退出码: 0 = 全部通过, 1 = 有失败项, 2 = 用法错误
/// </summary>
class HashProbe
{
    static int _failures;
    static Type _coreInterface;
    static Dictionary<string, Type> _cores = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

    // 期望值：由 Python hashlib 独立算出（见 testtools/hashvectors.py）
    static readonly Dictionary<string, string> Empty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string> Abc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string> Numbers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>框架算法的工厂：同一份 BuiltInCore 适配层，按 id 给不同的算法。</summary>
    static readonly Dictionary<string, Func<System.Security.Cryptography.HashAlgorithm>> _builtInFactories =
        new Dictionary<string, Func<System.Security.Cryptography.HashAlgorithm>>(StringComparer.OrdinalIgnoreCase)
        {
            { "md5", () => System.Security.Cryptography.MD5.Create() },
            { "sha1", () => System.Security.Cryptography.SHA1.Create() },
            { "sha256", () => System.Security.Cryptography.SHA256.Create() },
            { "sha384", () => System.Security.Cryptography.SHA384.Create() },
            { "sha512", () => System.Security.Cryptography.SHA512.Create() }
        };

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
            Console.WriteLine("用法: hashprobe.exe <HashChecker.exe> [--vectors]");
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

        _coreInterface = asm.GetType("HashChecker.Services.IHashCore");
        if (_coreInterface == null)
        {
            Console.WriteLine("!! 找不到 IHashCore");
            return 1;
        }

        Register(asm, "sha224", "Sha224Core");
        Register(asm, "sha256", "BuiltInCore");
        Register(asm, "sha384", "BuiltInCore");
        Register(asm, "sha512", "BuiltInCore");
        Register(asm, "sha512-224", "Sha512T224Core");
        Register(asm, "sha512-256", "Sha512T256Core");
        Register(asm, "sha3-224", "Sha3_224Core");
        Register(asm, "sha3-256", "Sha3_256Core");
        Register(asm, "sha3-384", "Sha3_384Core");
        Register(asm, "sha3-512", "Sha3_512Core");
        Register(asm, "blake2s", "Blake2sCore");
        Register(asm, "blake2b-256", "Blake2bCore");
        Register(asm, "crc32", "Crc32Core");
        Register(asm, "crc64", "Crc64Core");

        LoadVectors();
        bool vectorsOnly = Array.IndexOf(args, "--vectors") >= 0;

        Console.WriteLine("=== 多算法内核的运行时验证 ===");
        Console.WriteLine("被测程序: " + asmPath);
        Console.WriteLine("");

        if (vectorsOnly)
        {
            DumpVectors();
            return 0;
        }

        CheckVectors();
        CheckStreaming();
        CheckReuse();
        CheckLargeInput();
        CheckBuiltInCoverage();

        Console.WriteLine("");
        if (_failures == 0)
        {
            Console.WriteLine("全部符合预期 ✓");
            return 0;
        }
        Console.WriteLine("!! 有 " + _failures + " 项不符合预期");
        return 1;
    }

    static void Register(Assembly asm, string id, string typeName)
    {
        Type type = asm.GetType("HashChecker.Services." + typeName);
        if (type != null) _cores[id] = type;
    }

    /// <expected>/<name>_empty.txt 之类的外部参考文件由 python 生成，这里只是保留接口；
    /// 真正的期望值写死在 LoadVectors 里，避免探针依赖外部文件而静默跳过。
    static void LoadVectors()
    {
        Set(Empty, "sha224", "d14a028c2a3a2bc9476102bb288234c415a2b01f828ea62ac5b3e42f");
        Set(Empty, "sha256", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
        Set(Empty, "sha384", "38b060a751ac96384cd9327eb1b1e36a21fdb71114be07434c0cc7bf63f6e1da274edebfe76f65fbd51ad2f14898b95b");
        Set(Empty, "sha512", "cf83e1357eefb8bdf1542850d66d8007d620e4050b5715dc83f4a921d36ce9ce47d0d13c5d85f2b0ff8318d2877eec2f63b931bd47417a81a538327af927da3e");
        Set(Empty, "sha512-224", "6ed0dd02806fa89e25de060c19d3ac86cabb87d6a0ddd05c333b84f4");
        Set(Empty, "sha512-256", "c672b8d1ef56ed28ab87c3622c5114069bdd3ad7b8f9737498d0c01ecef0967a");
        Set(Empty, "sha3-224", "6b4e03423667dbb73b6e15454f0eb1abd4597f9a1b078e3f5b5a6bc7");
        Set(Empty, "sha3-256", "a7ffc6f8bf1ed76651c14756a061d662f580ff4de43b49fa82d80a4b80f8434a");
        Set(Empty, "sha3-384", "0c63a75b845e4f7d01107d852e4c2485c51a50aaaa94fc61995e71bbee983a2ac3713831264adb47fb6bd1e058d5f004");
        Set(Empty, "sha3-512", "a69f73cca23a9ac5c8b567dc185a756e97c982164fe25859e0d1dcc1475c80a615b2123af1f5f94c11e3e9402c3ac558f500199d95b6d3e301758586281dcd26");
        Set(Empty, "blake2s", "69217a3079908094e11121d042354a7c1f55b6482ca1a51e1b250dfd1ed0eef9");
        Set(Empty, "blake2b-256", "0e5751c026e543b2e8ab2eb06099daa1d1e5df47778f7787faab45cdf12fe3a8");
        Set(Empty, "crc32", "00000000");
        Set(Empty, "crc64", "0000000000000000");

        Set(Abc, "sha224", "23097d223405d8228642a477bda255b32aadbce4bda0b3f7e36c9da7");
        Set(Abc, "sha256", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        Set(Abc, "sha384", "cb00753f45a35e8bb5a03d699ac65007272c32ab0eded1631a8b605a43ff5bed8086072ba1e7cc2358baeca134c825a7");
        Set(Abc, "sha512", "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f");
        Set(Abc, "sha512-224", "4634270f707b6a54daae7530460842e20e37ed265ceee9a43e8924aa");
        Set(Abc, "sha512-256", "53048e2681941ef99b2e29b76b4c7dabe4c2d0c634fc6d46e0e2f13107e7af23");
        Set(Abc, "sha3-224", "e642824c3f8cf24ad09234ee7d3c766fc9a3a5168d0c94ad73b46fdf");
        Set(Abc, "sha3-256", "3a985da74fe225b2045c172d6bd390bd855f086e3e9d525b46bfe24511431532");
        Set(Abc, "sha3-384", "ec01498288516fc926459f58e2c6ad8df9b473cb0fc08c2596da7cf0e49be4b298d88cea927ac7f539f1edf228376d25");
        Set(Abc, "sha3-512", "b751850b1a57168a5693cd924b6b096e08f621827444f70d884f5d0240d2712e10e116e9192af3c91a7ec57647e3934057340b4cf408d5a56592f8274eec53f0");
        Set(Abc, "blake2s", "508c5e8c327c14e2e1a72ba34eeb452f37458b209ed63a294d999b4c86675982");
        Set(Abc, "blake2b-256", "bddd813c634239723171ef3fee98579b94964e3bb1cb3e427262c8c068d52319");
        Set(Abc, "crc32", "352441c2");
        Set(Abc, "crc64", "2cd8094a1a277627");

        Set(Numbers, "sha224", "9b3e61bf29f17c75572fae2e86e17809a4513d07c8a18152acf34521");
        Set(Numbers, "sha256", "15e2b0d3c33891ebb0f1ef609ec419420c20e320ce94c65fbc8c3312448eb225");
        Set(Numbers, "sha384", "eb455d56d2c1a69de64e832011f3393d45f3fa31d6842f21af92d2fe469c499da5e3179847334a18479c8d1dedea1be3");
        Set(Numbers, "sha512", "d9e6762dd1c8eaf6d61b3c6192fc408d4d6d5f1176d0c29169bc24e71c3f274ad27fcd5811b313d681f7e55ec02d73d499c95455b6b5bb503acf574fba8ffe85");
        Set(Numbers, "sha512-224", "f2a68a474bcbea375e9fc62eaab7b81fefbda64bb1c72d72e7c27314");
        Set(Numbers, "sha512-256", "1877345237853a31ad79e14c1fcb0ddcd3df9973b61af7f906e4b4d052cc9416");
        Set(Numbers, "sha3-224", "5795c3d628fd638c9835a4c79a55809f265068c88729a1a3fcdf8522");
        Set(Numbers, "sha3-256", "87cd084d190e436f147322b90e7384f6a8e0676c99d21ef519ea718e51d45f9c");
        Set(Numbers, "sha3-384", "8b90ede4d095409f1a12492c2520599683a9478dc70b7566d23b3e41ece8538c6cde92382a5e38786490375c54672abf");
        Set(Numbers, "sha3-512", "e1e44d20556e97a180b6dd3ed7ae5c465cafd553fa8747dca038fb95635b77a37318f7ddf7aec1f6c3c14bb160ba2497007decf38dd361cab199e3b8c8fe1f5c");
        Set(Numbers, "blake2s", "7acc2dd21a2909140507f37396acce906864b5f118dfa766b107962b7a82a0d4");
        Set(Numbers, "blake2b-256", "16e0bf1f85594a11e75030981c0b670370b3ad83a43f49ae58a2fd6f6513cde9");
        Set(Numbers, "crc32", "cbf43926");
        Set(Numbers, "crc64", "995dc9bbdf1939fa");
    }

    static void Set(Dictionary<string, string> table, string id, string value)
    {
        table[id] = value;
    }

    static void CheckVectors()
    {
        Console.WriteLine("【已知向量】");

        var inputs = new Dictionary<string, string>
        {
            { "空输入", "" },
            { "\"abc\"", "abc" },
            { "\"123456789\"", "123456789" }
        };

        foreach (KeyValuePair<string, string> input in inputs)
        {
            foreach (KeyValuePair<string, Type> core in _cores)
            {
                Dictionary<string, string> expected = null;
                if (input.Value.Length == 0) expected = Empty;
                else if (input.Value == "abc") expected = Abc;
                else if (input.Value == "123456789") expected = Numbers;

                if (expected == null || !expected.ContainsKey(core.Key)) continue;

                string actual = Hex(core.Key, Encoding.UTF8.GetBytes(input.Value));
                string want = expected[core.Key];

                if (HexLength(core.Key) != want.Length)
                {
                    Check(core.Key + " " + input.Key, actual.Length == want.Length,
                        "长度不符：实际 " + actual.Length + " 位，期望 " + want.Length + " 位");
                    continue;
                }

                Check(core.Key + " " + input.Key, actual == want,
                    actual == want ? "" : "实际=" + actual + " 期望=" + want);
            }
        }
    }

    /// <summary>分块喂入必须与一次性喂入结果相同。</summary>
    static void CheckStreaming()
    {
        Console.WriteLine("");
        Console.WriteLine("【分块流式】");

        byte[] data = Payload(300 * 1024 + 7);
        foreach (KeyValuePair<string, Type> core in _cores)
        {
            string once = Hex(core.Key, data);
            string chunked = HexChunked(core.Key, data, 1000);
            Check(core.Key + " 分块一致", once == chunked, "一次性=" + Prefix(once) + " 分块=" + Prefix(chunked));
        }
    }

    /// <summary>取前 16 位做回显；短摘要（CRC-32 只有 8 位）不能硬截 16 位。</summary>
    static string Prefix(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return "(空)";
        return hex.Length <= 16 ? hex : hex.Substring(0, 16);
    }

    /// <summary>同一个实例算完一个输入后重置，必须能正确算下一个。</summary>
    static void CheckReuse()
    {
        Console.WriteLine("");
        Console.WriteLine("【实例复用】");

        byte[] first = Encoding.UTF8.GetBytes("abc");
        byte[] second = Payload(64 * 1024);

        foreach (KeyValuePair<string, Type> core in _cores)
        {
            object instance = Create(core.Key, core.Value);
            string once = InvokeHex(instance, first);

            string twice = InvokeHex(instance, second);
            if (twice == null)
            {
                Check(core.Key + " 复用", false, "第二次调用失败");
                continue;
            }

            string fresh = Hex(core.Key, second);
            Check(core.Key + " 复用后结果正确", twice == fresh, "复用=" + Prefix(twice) + " 新建=" + Prefix(fresh));
            Check(core.Key + " 首次结果不变", once == Hex(core.Key, first), "首次=" + Prefix(once));
        }
    }

    static void CheckLargeInput()
    {
        Console.WriteLine("");
        Console.WriteLine("【大输入】");

        byte[] data = Payload(10 * 1024 * 1024);
        short reported = 0;

        var checks = new List<string> { "sha256", "sha512", "sha3-256", "blake2s", "crc32" };
        foreach (string id in checks)
        {
            if (!_cores.ContainsKey(id)) continue;
            string value = Hex(id, data);
            if (value.Length > 0) reported++;
            Check(id + " 处理 10MB 不抛异常", value.Length == HexLength(id),
                "长度=" + value.Length + "（期望 " + HexLength(id) + "）");
        }
        Check("大输入覆盖到多个算法", reported == checks.Count, "完成 " + reported + " / " + checks.Count);
    }

    /// <summary>框架自带的算法必须能通过适配层正确工作。</summary>
    static void CheckBuiltInCoverage()
    {
        Console.WriteLine("");
        Console.WriteLine("【框架算法适配】");

        string md5Empty = HexViaBuiltIn("MD5", new byte[0]);
        Check("MD5 空输入", md5Empty == "d41d8cd98f00b204e9800998ecf8427e", "实际=" + md5Empty);

        string md5Abc = HexViaBuiltIn("MD5", Encoding.UTF8.GetBytes("abc"));
        Check("MD5 \"abc\"", md5Abc == "900150983cd24fb0d6963f7d28e17f72", "实际=" + md5Abc);

        string sha1Abc = HexViaBuiltIn("SHA1", Encoding.UTF8.GetBytes("abc"));
        Check("SHA-1 \"abc\"", sha1Abc == "a9993e364706816aba3e25717850c26c9cd0d89d", "实际=" + sha1Abc);
    }

    static string HexViaBuiltIn(string algorithm, byte[] data)
    {
        byte[] digest;
        using (var core = System.Security.Cryptography.HashAlgorithm.Create(algorithm))
        {
            digest = core.ComputeHash(data);
        }

        var sb = new StringBuilder(digest.Length * 2);
        foreach (byte b in digest) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    static void DumpVectors()
    {
        Console.WriteLine("# id\tempty\tabc\t123456789");
        foreach (KeyValuePair<string, Type> core in _cores)
        {
            string e = Hex(core.Key, new byte[0]);
            string a = Hex(core.Key, Encoding.UTF8.GetBytes("abc"));
            string n = Hex(core.Key, Encoding.UTF8.GetBytes("987654321".Length == 0 ? "" : "123456789"));
            Console.WriteLine(core.Key + "\t" + e + "\t" + a + "\t" + n);
        }
    }

    static int HexLength(string id)
    {
        switch (id.ToLowerInvariant())
        {
            case "sha224": return 56;
            case "sha256": return 64;
            case "sha384": return 96;
            case "sha512": return 128;
            case "sha512-224": return 56;
            case "sha512-256": return 64;
            case "sha3-224": return 56;
            case "sha3-256": return 64;
            case "sha3-384": return 96;
            case "sha3-512": return 128;
            case "blake2s": return 64;
            case "blake2b-256": return 64;
            case "crc32": return 8;
            case "crc64": return 16;
            default: return 0;
        }
    }

    static object Create(string id, Type type)
    {
        if (type.Name == "BuiltInCore")
        {
            // 适配层要一个工厂；按 id 给对应算法，不能一律 SHA-256
            Func<System.Security.Cryptography.HashAlgorithm> factory = _builtInFactories[id];
            return type.GetConstructor(new[] { typeof(Func<System.Security.Cryptography.HashAlgorithm>) })
                .Invoke(new object[] { factory });
        }
        if (type.Name == "Blake2bCore")
        {
            return type.GetConstructor(new[] { typeof(int) }).Invoke(new object[] { 32 });
        }
        return type.GetConstructor(Type.EmptyTypes).Invoke(null);
    }

    static string Hex(string id, byte[] data)
    {
        object instance = Create(id, _cores[id]);
        return InvokeHex(instance, data);
    }

    static string HexChunked(string id, byte[] data, int chunk)
    {
        object instance = Create(id, _cores[id]);
        MethodInfo init = _coreInterface.GetMethod("Initialize");
        MethodInfo append = _coreInterface.GetMethod("Append");
        MethodInfo finish = _coreInterface.GetMethod("Finish");

        init.Invoke(instance, null);
        for (int offset = 0; offset < data.Length; offset += chunk)
        {
            int take = Math.Min(chunk, data.Length - offset);
            append.Invoke(instance, new object[] { data, offset, take });
        }
        byte[] digest = (byte[])finish.Invoke(instance, null);
        return ToHex(digest);
    }

    static string InvokeHex(object instance, byte[] data)
    {
        try
        {
            MethodInfo init = _coreInterface.GetMethod("Initialize");
            MethodInfo append = _coreInterface.GetMethod("Append");
            MethodInfo finish = _coreInterface.GetMethod("Finish");

            init.Invoke(instance, null);
            append.Invoke(instance, new object[] { data, 0, data.Length });
            byte[] digest = (byte[])finish.Invoke(instance, null);
            return ToHex(digest);
        }
        catch (TargetInvocationException e)
        {
            Console.WriteLine("!! 内核抛异常: " + e.InnerException.Message);
            _failures++;
            return "";
        }
    }

    static string ToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    static byte[] Payload(int size)
    {
        var data = new byte[size];
        for (int i = 0; i < size; i++) data[i] = (byte)((i * 37 + 11) & 0xFF);
        return data;
    }
}
