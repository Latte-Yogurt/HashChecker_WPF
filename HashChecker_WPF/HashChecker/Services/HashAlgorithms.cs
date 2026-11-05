using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace HashChecker.Services
{
    /// <summary>
    /// 一个算法目录项：标识、显示名、摘要位数、内核工厂，以及能把它认出来的后缀与别名。
    /// </summary>
    public class HashAlgorithmInfo
    {
        /// <summary>规范标识，全部小写，例如 sha256、sha3-256、crc32。</summary>
        public string Id { get; set; }

        /// <summary>界面显示名，例如 SHA-256。</summary>
        public string DisplayName { get; set; }

        /// <summary>摘要字节数。</summary>
        public int DigestBytes { get; set; }

        /// <summary>摘要的十六进制字符数（DigestBytes * 2），也是校验记录里值的长度。</summary>
        public int HexLength { get { return DigestBytes * 2; } }

        /// <summary>新建一个可复用的增量内核。</summary>
        public Func<IHashCore> CreateCore { get; set; }

        /// <summary>能把这个算法认出来的文件后缀，全部小写且带点，例如 .sha256。</summary>
        public List<string> Extensions { get; set; }

        /// <summary>
        /// 写校验记录时用的首选后缀，取 Extensions 的第一项。
        /// 必须按「算法」而不是「算法族」取：SHA3-256 要写 .sha3-256 而不是 .sha256，
        /// 写错了文件传回来时会被按 SHA-256 认，校验必然对不上。目录里每个算法的首选后缀唯一。
        /// </summary>
        public string Extension
        {
            get
            {
                if (Extensions == null || Extensions.Count == 0) return ".checksum";
                return Extensions[0];
            }
        }

        /// <summary>名字别名，全部小写且已去掉分隔符，sha256 与 sha-256 都能命中。</summary>
        public HashSet<string> Aliases { get; set; }

        public override string ToString()
        {
            return DisplayName;
        }
    }

    /// <summary>
    /// 算法目录。设计目标是最宽容的兼容性：
    /// 1. 覆盖 .NET Framework 自带算法与自实现算法；
    /// 2. 同一算法接受多种后缀与写法（.sha256 / .sha256sum / .sha2，名字侧接受 SHA-256 / sha256 / SHA2-256）；
    /// 3. 后缀认不出时按值的长度推断候选，比对时任一个命中即通过，
    ///    避免因为分不清 SHA-256 与 SHA3-256 就误报失败。
    /// </summary>
    public static class HashAlgorithms
    {
        private static readonly List<HashAlgorithmInfo> _all = BuildCatalog();

        private static readonly Dictionary<string, HashAlgorithmInfo> _byId =
            new Dictionary<string, HashAlgorithmInfo>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, HashAlgorithmInfo> _byAlias =
            new Dictionary<string, HashAlgorithmInfo>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, HashAlgorithmInfo> _byExtension =
            new Dictionary<string, HashAlgorithmInfo>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<int, List<HashAlgorithmInfo>> _byHexLength =
            new Dictionary<int, List<HashAlgorithmInfo>>();

        private static readonly List<string> _extensionKeys = new List<string>();

        private static readonly string[] _containerExtensions =
        {
            ".txt", ".text", ".checksum", ".checksums", ".ck", ".lst", ".out", ".log"
        };

        static HashAlgorithms()
        {
            foreach (HashAlgorithmInfo info in _all)
            {
                _byId[info.Id] = info;

                foreach (string alias in info.Aliases)
                {
                    _byAlias[NormalizeName(alias)] = info;
                }

                foreach (string extension in info.Extensions)
                {
                    _byExtension[extension] = info;
                    if (!_extensionKeys.Contains(extension)) _extensionKeys.Add(extension);
                }

                List<HashAlgorithmInfo> sameLength;
                if (!_byHexLength.TryGetValue(info.HexLength, out sameLength))
                {
                    sameLength = new List<HashAlgorithmInfo>();
                    _byHexLength[info.HexLength] = sameLength;
                }
                sameLength.Add(info);
            }

            _extensionKeys.Sort((a, b) => b.Length.CompareTo(a.Length));
        }

        /// <summary>目录里的全部算法，顺序即界面下拉框的顺序。</summary>
        public static IList<HashAlgorithmInfo> All
        {
            get { return _all; }
        }

        /// <summary>默认算法（SHA-256），没有记录可依据时用它。</summary>
        public static HashAlgorithmInfo Default
        {
            get { return _byId["sha256"]; }
        }

        /// <summary>按标识取算法；找不到返回 null。</summary>
        public static HashAlgorithmInfo ById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            HashAlgorithmInfo info;
            return _byId.TryGetValue(id.Trim(), out info) ? info : null;
        }

        /// <summary>按名字或别名取算法；找不到返回 null。</summary>
        public static HashAlgorithmInfo ByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            string key = NormalizeName(name);
            if (key.Length == 0) return null;

            HashAlgorithmInfo info;
            if (_byAlias.TryGetValue(key, out info)) return info;
            return _byId.TryGetValue(name.Trim(), out info) ? info : null;
        }

        /// <summary>
        /// 按文件后缀判断算法，认不出来时返回 null，由调用方按值长度推断。
        /// 后缀匹配最长优先（.sha256sum 与 .sha256 同时命中时取更长的那个），
        /// 并且会先剥掉 .txt / .checksum 这类容器后缀，所以 hash.md5.txt 也能认成 MD5。
        /// </summary>
        public static HashAlgorithmInfo ByExtension(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            string name = Path.GetFileName(path).ToLowerInvariant();
            if (name.Length == 0) return null;

            HashAlgorithmInfo info = MatchSuffix(name);
            if (info != null) return info;

            foreach (string container in _containerExtensions)
            {
                if (!name.EndsWith(container, StringComparison.Ordinal)) continue;
                string stripped = name.Substring(0, name.Length - container.Length);
                info = MatchSuffix(stripped);
                if (info != null) return info;
            }

            return null;
        }

        /// <summary>值长度对应的候选算法集合；没有匹配时返回空列表。</summary>
        public static List<HashAlgorithmInfo> ByHexLength(int hexLength)
        {
            List<HashAlgorithmInfo> list;
            return _byHexLength.TryGetValue(hexLength, out list) ? list : new List<HashAlgorithmInfo>();
        }

        /// <summary>能出现在校验记录里的值长度集合。</summary>
        public static List<int> KnownHexLengths
        {
            get
            {
                var list = new List<int>(_byHexLength.Keys);
                list.Sort();
                return list;
            }
        }

        /// <summary>
        /// 规范化摘要文本：去空白、去引号、去 sha256: 这类前缀、去引导符，统一转小写。
        /// 不是纯十六进制时返回 null；长度不在这里限制，由调用方判断。
        /// </summary>
        public static string NormalizeDigest(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;

            string value = raw.Trim().Trim('"').Trim('\'').Trim();

            int colon = value.IndexOf(':');
            if (colon > 0 && colon <= 12)
            {
                string prefix = value.Substring(0, colon);
                if (ByName(prefix) != null) value = value.Substring(colon + 1).Trim();
            }

            int equals = value.IndexOf('=');
            if (equals > 0 && equals <= 12)
            {
                string prefix = value.Substring(0, equals);
                if (ByName(prefix) != null) value = value.Substring(equals + 1).Trim();
            }

            while (value.Length > 0 && (value[0] == '*' || value[0] == '?' || value[0] == ' '))
            {
                value = value.Substring(1).TrimStart();
            }

            value = value.ToLowerInvariant();

            for (int i = 0; i < value.Length; i++)
            {
                if (!Uri.IsHexDigit(value[i])) return null;
            }

            return value.Length == 0 ? null : value;
        }

        /// <summary>由已知值推断候选算法：长度对上的一组，可能有多个（SHA-256 与 SHA3-256 等）。</summary>
        public static List<HashAlgorithmInfo> CandidatesFor(string digest)
        {
            string normalized = NormalizeDigest(digest);
            if (normalized == null) return new List<HashAlgorithmInfo>();
            return ByHexLength(normalized.Length);
        }

        /// <summary>把名字归一化：去掉分隔符与大小写，便于别名匹配。</summary>
        public static string NormalizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;

            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name.Trim())
            {
                if (c == '-' || c == '_' || c == ' ' || c == '.' || c == '/') continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>在文件名里找最长命中的算法后缀。</summary>
        private static HashAlgorithmInfo MatchSuffix(string name)
        {
            foreach (string extension in _extensionKeys)
            {
                if (!name.EndsWith(extension, StringComparison.Ordinal)) continue;
                return _byExtension[extension];
            }
            return null;
        }

        /// <summary>构造算法目录。顺序即界面下拉框的顺序，改动会直接改变用户看到的列表。</summary>
        private static List<HashAlgorithmInfo> BuildCatalog()
        {
            var list = new List<HashAlgorithmInfo>();

            list.Add(Simple("md5", "MD5", 16,
                () => new BuiltInCore(() => MD5.Create()),
                new[] { ".md5", ".md5sum" },
                new[] { "md5", "md-5", "md5sum" }));

            list.Add(Simple("sha1", "SHA-1", 20,
                () => new BuiltInCore(() => SHA1.Create()),
                new[] { ".sha1", ".sha1sum", ".sha-1" },
                new[] { "sha1", "sha-1", "sha1sum" }));

            list.Add(Simple("sha224", "SHA-224", 28,
                () => new Sha224Core(),
                new[] { ".sha224", ".sha224sum", ".sha2-224" },
                new[] { "sha224", "sha-224", "sha2-224", "sha2_224" }));

            list.Add(Simple("sha256", "SHA-256", 32,
                () => new BuiltInCore(() => SHA256.Create()),
                new[] { ".sha256", ".sha256sum", ".sha2", ".sha2-256" },
                new[] { "sha256", "sha-256", "sha2-256", "sha256sum", "sha2" }));

            list.Add(Simple("sha384", "SHA-384", 48,
                () => new BuiltInCore(() => SHA384.Create()),
                new[] { ".sha384", ".sha384sum", ".sha2-384" },
                new[] { "sha384", "sha-384", "sha2-384" }));

            list.Add(Simple("sha512", "SHA-512", 64,
                () => new BuiltInCore(() => SHA512.Create()),
                new[] { ".sha512", ".sha512sum", ".sha2-512" },
                new[] { "sha512", "sha-512", "sha2-512", "sha512sum" }));

            list.Add(Simple("sha512-224", "SHA-512/224", 28,
                () => new Sha512T224Core(),
                new[] { ".sha512-224", ".sha512_224", ".sha512t224" },
                new[] { "sha512-224", "sha512/224", "sha512t224", "sha512_224" }));

            list.Add(Simple("sha512-256", "SHA-512/256", 32,
                () => new Sha512T256Core(),
                new[] { ".sha512-256", ".sha512_256", ".sha512t256" },
                new[] { "sha512-256", "sha512/256", "sha512t256", "sha512_256" }));

            list.Add(Simple("sha3-224", "SHA3-224", 28,
                () => new Sha3_224Core(),
                new[] { ".sha3-224", ".sha3_224", ".sha3224" },
                new[] { "sha3-224", "sha3_224", "sha3224", "sha3-224sum" }));

            list.Add(Simple("sha3-256", "SHA3-256", 32,
                () => new Sha3_256Core(),
                new[] { ".sha3-256", ".sha3_256", ".sha3256", ".sha3" },
                new[] { "sha3-256", "sha3_256", "sha3256", "sha3", "sha3-256sum" }));

            list.Add(Simple("sha3-384", "SHA3-384", 48,
                () => new Sha3_384Core(),
                new[] { ".sha3-384", ".sha3_384", ".sha3384" },
                new[] { "sha3-384", "sha3_384", "sha3384", "sha3-384sum" }));

            list.Add(Simple("sha3-512", "SHA3-512", 64,
                () => new Sha3_512Core(),
                new[] { ".sha3-512", ".sha3_512", ".sha3512" },
                new[] { "sha3-512", "sha3_512", "sha3512", "sha3-512sum" }));

            list.Add(Simple("blake2s-256", "BLAKE2s-256", 32,
                () => new Blake2sCore(),
                new[] { ".blake2s", ".blake2s-256", ".b2s" },
                new[] { "blake2s", "blake2s-256", "b2s" }));

            list.Add(Simple("blake2b-256", "BLAKE2b-256", 32,
                () => new Blake2bCore(32),
                new[] { ".blake2b-256", ".blake2b256", ".b2b-256" },
                new[] { "blake2b-256", "blake2b256", "b2b-256" }));

            list.Add(Simple("blake2b-512", "BLAKE2b-512", 64,
                () => new Blake2bCore(64),
                new[] { ".blake2b", ".blake2b-512", ".b2b", ".blake2" },
                new[] { "blake2b", "blake2b-512", "b2b", "blake2" }));

            list.Add(Simple("crc32", "CRC-32", 4,
                () => new Crc32Core(),
                new[] { ".crc32", ".crc", ".sfv" },
                new[] { "crc32", "crc-32", "crc", "sfv" }));

            list.Add(Simple("crc64", "CRC-64", 8,
                () => new Crc64Core(),
                new[] { ".crc64", ".crc-64" },
                new[] { "crc64", "crc-64" }));

            return list;
        }

        /// <summary>构造一个内置算法的目录项。</summary>
        private static HashAlgorithmInfo Simple(string id, string displayName, int digestBytes,
            Func<IHashCore> core, string[] extensions, string[] aliases)
        {
            var info = new HashAlgorithmInfo
            {
                Id = id,
                DisplayName = displayName,
                DigestBytes = digestBytes,
                CreateCore = core,
                Extensions = new List<string>(extensions),
                Aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            };

            foreach (string alias in aliases)
            {
                info.Aliases.Add(alias);
            }

            return info;
        }
    }
}
