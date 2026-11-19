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

        // 内核拆分之后，每个算法都有自己独立的类，探针按 id 直接对应到类名。
        // 这也顺带守住「一个算法一个服务」：类名对不上就会在这里报找不到。
        Register(asm, "md5", "Md5Core");
        Register(asm, "sha1", "Sha1Core");
        Register(asm, "sha224", "Sha224Core");
        Register(asm, "sha256", "Sha256Core");
        Register(asm, "sha384", "Sha384Core");
        Register(asm, "sha512", "Sha512Core");
        Register(asm, "sha512-224", "Sha512T224Core");
        Register(asm, "sha512-256", "Sha512T256Core");
        Register(asm, "sha3-224", "Sha3_224Core");
        Register(asm, "sha3-256", "Sha3_256Core");
        Register(asm, "sha3-384", "Sha3_384Core");
        Register(asm, "sha3-512", "Sha3_512Core");
        Register(asm, "blake2s", "Blake2sCore");
        Register(asm, "blake2b-256", "Blake2b256Core");
        Register(asm, "blake2b-512", "Blake2b512Core");
        Register(asm, "blake3", "Blake3Core");
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
        CheckBlake3Tree();
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
        Set(Empty, "md5", "d41d8cd98f00b204e9800998ecf8427e");
        Set(Empty, "sha1", "da39a3ee5e6b4b0d3255bfef95601890afd80709");
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
        Set(Empty, "blake2b-512", "786a02f742015903c6c6fd852552d272912f4740e15847618a86e217f71f5419d25e1031afee585313896444934eb04b903a685b1448b755d56f701afe9be2ce");
        Set(Empty, "blake3", "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262");
        Set(Empty, "crc32", "00000000");
        Set(Empty, "crc64", "0000000000000000");

        Set(Abc, "md5", "900150983cd24fb0d6963f7d28e17f72");
        Set(Abc, "sha1", "a9993e364706816aba3e25717850c26c9cd0d89d");
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
        Set(Abc, "blake2b-512", "ba80a53f981c4d0d6a2797b69f12f6e94c212f14685ac4b74b12bb6fdbffa2d17d87c5392aab792dc252d5de4533cc9518d38aa8dbf1925ab92386edd4009923");
        Set(Abc, "blake3", "6437b3ac38465133ffb63b75273a8db548c558465d79db03fd359c6cd5bd9d85");
        Set(Abc, "crc32", "352441c2");
        Set(Abc, "crc64", "2cd8094a1a277627");

        Set(Numbers, "md5", "25f9e794323b453885f5181f1b624d0b");
        Set(Numbers, "sha1", "f7c3bc1d808e04732adf679965ccc34ca7ae3441");
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
        Set(Numbers, "blake2b-512", "f5ab8bafa6f2f72b431188ac38ae2de7bb618fb3d38b6cbf639defcdd5e10a86b22fccff571da37e42b23b80b657ee4d936478f582280a87d6dbb1da73f5c47d");
        Set(Numbers, "blake3", "b7d65b48420d1033cb2595293263b6f72eabee20d55e699d0df1973b3c9deed1");
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

    /// <summary>
    /// BLAKE3 的树形边界。它是唯一一个「输出不只取决于字节流、还取决于数据被切成几个 chunk」
    /// 的算法：1024 字节的 chunk、chunk 内的 block、树的两两合并，任何一处差一个字节都算不出正确的值。
    /// 所以这里按 1024 的整数倍与前后一个字节逐个对参考值，专门咬住 chunk 边界。
    ///
    /// 期望值由官方 blake3 包（Rust 实现的 Python 绑定）算出，不是照着自己的输出抄的。
    /// </summary>
    static void CheckBlake3Tree()
    {
        Console.WriteLine("");
        Console.WriteLine("【BLAKE3 树形边界】");

        if (!_cores.ContainsKey("blake3"))
        {
            Check("Blake3Core 存在", false, "程序集里没有 Blake3Core");
            return;
        }

        // 官方 test_vectors.json 的输入：第 i 个字节是 i % 251，这样任何偏移错位都会改变结果。
        var expected = new Dictionary<int, string>
        {
            { 0, "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262" },
            { 1, "2d3adedff11b61f14c886e35afa036736dcd87a74d27b5c1510225d0f592e213" },
            { 2, "7b7015bb92cf0b318037702a6cdd81dee41224f734684c2c122cd6359cb1ee63" },
            { 3, "e1be4d7a8ab5560aa4199eea339849ba8e293d55ca0a81006726d184519e647f" },
            { 63, "e9bc37a594daad83be9470df7f7b3798297c3d834ce80ba85d6e207627b7db7b" },
            { 64, "4eed7141ea4a5cd4b788606bd23f46e212af9cacebacdc7d1f4c6dc7f2511b98" },
            { 65, "de1e5fa0be70df6d2be8fffd0e99ceaa8eb6e8c93a63f2d8d1c30ecb6b263dee" },
            { 127, "d81293fda863f008c09e92fc382a81f5a0b4a1251cba1634016a0f86a6bd640d" },
            { 128, "f17e570564b26578c33bb7f44643f539624b05df1a76c81f30acd548c44b45ef" },
            { 129, "683aaae9f3c5ba37eaaf072aed0f9e30bac0865137bae68b1fde4ca2aebdcb12" },
            { 1023, "10108970eeda3eb932baac1428c7a2163b0e924c9a9e25b35bba72b28f70bd11" },
            { 1024, "42214739f095a406f3fc83deb889744ac00df831c10daa55189b5d121c855af7" },
            { 1025, "d00278ae47eb27b34faecf67b4fe263f82d5412916c1ffd97c8cb7fb814b8444" },
            { 2048, "e776b6028c7cd22a4d0ba182a8bf62205d2ef576467e838ed6f2529b85fba24a" },
            { 2049, "5f4d72f40d7a5f82b15ca2b2e44b1de3c2ef86c426c95c1af0b6879522563030" },
            { 3072, "b98cb0ff3623be03326b373de6b9095218513e64f1ee2edd2525c7ad1e5cffd2" },
            { 3073, "7124b49501012f81cc7f11ca069ec9226cecb8a2c850cfe644e327d22d3e1cd3" },
            { 4096, "015094013f57a5277b59d8475c0501042c0b642e531b0a1c8f58d2163229e969" },
            { 4097, "9b4052b38f1c5fc8b1f9ff7ac7b27cd242487b3d890d15c96a1c25b8aa0fb995" },
            { 5120, "9cadc15fed8b5d854562b26a9536d9707cadeda9b143978f319ab34230535833" },
            { 6144, "3e2e5b74e048f3add6d21faab3f83aa44d3b2278afb83b80b3c35164ebeca205" },
            { 7168, "61da957ec2499a95d6b8023e2b0e604ec7f6b50e80a9678b89d2628e99ada77a" },
            { 8192, "aae792484c8efe4f19e2ca7d371d8c467ffb10748d8a5a1ae579948f718a2a63" }
        };

        int failuresBefore = _failures;

        foreach (KeyValuePair<int, string> pair in expected)
        {
            var data = new byte[pair.Key];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 251);

            string actual = Hex("blake3", data);
            Check("BLAKE3 " + pair.Key + " 字节", actual == pair.Value,
                actual == pair.Value ? "" : "实际=" + actual + " 期望=" + pair.Value);
        }

        // 分块喂入必须与一次性喂入一致：chunk 缓冲区跨 Append 边界是最容易写错的地方，
        // 所以块大小特意取 1 / 63 / 64 / 1024 / 1025 / 4097 —— 既有比一块小、也有不是 1024 整数倍的。
        int chunkFailures = 0;
        foreach (int size in new[] { 0, 1, 1023, 1024, 1025, 3072, 4096, 4097, 10000 })
        {
            var data = new byte[size];
            for (int i = 0; i < size; i++) data[i] = (byte)(i % 251);

            string once = Hex("blake3", data);
            foreach (int chunk in new[] { 1, 63, 64, 65, 1000, 1024, 1025, 4097 })
            {
                if (HexChunked("blake3", data, chunk) != once) chunkFailures++;
            }
        }
        Check("BLAKE3 分块喂入与一次性喂入一致", chunkFailures == 0, "不一致 " + chunkFailures + " 组");

        Check("BLAKE3 边界断言全部通过", _failures == failuresBefore, "失败 " + (_failures - failuresBefore) + " 项");
    }

    static void CheckLargeInput()
    {
        Console.WriteLine("");
        Console.WriteLine("【大输入】");

        byte[] data = Payload(10 * 1024 * 1024);
        short reported = 0;

        var checks = new List<string> { "sha256", "sha512", "sha3-256", "blake2s", "blake3", "crc32" };
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

    /// <summary>
    /// 框架适配层：确认每个 FrameworkCore 子类都真的把自己那个算法的实例造出来了。
    ///
    /// 这里刻意**不**用 HashAlgorithm.Create 直接算 —— 那是验 .NET 自己，与我们的包装无关。
    /// 要验的是「Md5Core 算的是 MD5、Sha256Core 算的是 SHA-256」这类对应关系有没有串线，
    /// 而这正是共用 BuiltInCore 时最容易出错、拆分后也仍然可能复制粘贴错的地方。
    /// </summary>
    static void CheckBuiltInCoverage()
    {
        Console.WriteLine("");
        Console.WriteLine("【框架算法适配】");

        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "md5", "" },
            { "sha1", "" },
            { "sha256", "" },
            { "sha384", "" },
            { "sha512", "" }
        };

        if (!_cores.ContainsKey("md5") || !_cores.ContainsKey("sha1"))
        {
            Check("框架算法已注册", false, "缺少 md5 / sha1 内核");
            return;
        }

        int mismatched = 0;
        foreach (string id in new List<string>(expected.Keys))
        {
            if (!_cores.ContainsKey(id))
            {
                Check(id + " 内核存在", false, "程序集里没有对应类型");
                mismatched++;
                continue;
            }

            // 与我们通过包装类算出的值比对的，是框架 API 直接算出来的值：
            // 两者必须相等，说明包装类没有指向别的算法。
            byte[] data = Encoding.UTF8.GetBytes("abc");
            string viaCore = Hex(id, data);
            string viaFramework = HexViaFramework(id, data);

            Check(id + " 包装类算的是自己那个算法",
                string.Equals(viaCore, viaFramework, StringComparison.OrdinalIgnoreCase),
                viaCore == viaFramework ? "" : "内核=" + viaCore + " 框架=" + viaFramework);

            if (!string.Equals(viaCore, viaFramework, StringComparison.OrdinalIgnoreCase)) mismatched++;
        }

        // 五个子类必须各自独立：任意两个类的类型名不能相同（拆分的直接目的）
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in expected.Keys)
        {
            if (_cores.ContainsKey(id)) names.Add(_cores[id].Name);
        }
        Check("框架算法各自独立成类", names.Count == expected.Count,
            "不同类的数量=" + names.Count + " / " + expected.Count);
    }

    /// <summary>直接用框架 API 算一遍，作为包装类的对照。</summary>
    static string HexViaFramework(string id, byte[] data)
    {
        System.Security.Cryptography.HashAlgorithm algorithm;
        switch (id.ToLowerInvariant())
        {
            case "md5": algorithm = System.Security.Cryptography.MD5.Create(); break;
            case "sha1": algorithm = System.Security.Cryptography.SHA1.Create(); break;
            case "sha256": algorithm = System.Security.Cryptography.SHA256.Create(); break;
            case "sha384": algorithm = System.Security.Cryptography.SHA384.Create(); break;
            case "sha512": algorithm = System.Security.Cryptography.SHA512.Create(); break;
            default: return "";
        }

        byte[] digest;
        using (algorithm)
        {
            digest = algorithm.ComputeHash(data);
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
            case "md5": return 32;
            case "sha1": return 40;
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
            case "blake2b-512": return 128;
            case "blake3": return 64;
            case "crc32": return 8;
            case "crc64": return 16;
            default: return 0;
        }
    }

    /// <summary>
    /// 造一个内核实例。
    ///
    /// 拆分完成后，**每一个具体算法都是无参构造** —— 摘要长度、速率这些参数
    /// 写在各自的子类里，不再由调用方传。这里如果还需要按类型名特判，
    /// 就说明又出现了「一个类被多个算法共用、靠参数区分」的耦合。
    /// </summary>
    static object Create(string id, Type type)
    {
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
