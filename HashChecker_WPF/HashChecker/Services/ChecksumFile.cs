using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using HashChecker.Models;

namespace HashChecker.Services
{
    /// <summary>校验记录里的一行。</summary>
    public class ChecksumEntry
    {
        /// <summary>规范化后的小写摘要值。</summary>
        public string Hash { get; set; }

        /// <summary>记录里写的文件路径，可能是绝对路径、相对路径或只有文件名。</summary>
        public string Path { get; set; }

        /// <summary>该行原文，解析失败时便于回显。</summary>
        public string RawLine { get; set; }

        /// <summary>
        /// 这一行的算法。记录后缀写明或行内写了算法名时确定；
        /// 只按值长度推且同长度有多个候选时为 null，此时按候选集合比对。
        /// </summary>
        public HashAlgorithmInfo Algorithm { get; set; }

        /// <summary>值的十六进制长度，用于按长度推断候选算法。</summary>
        public int HexLength { get { return Hash == null ? 0 : Hash.Length; } }

        /// <summary>这一行的候选算法：明确的算法优先，否则按长度给一组。</summary>
        public List<HashAlgorithmInfo> Candidates
        {
            get
            {
                if (Algorithm != null) return new List<HashAlgorithmInfo> { Algorithm };
                return HashAlgorithms.ByHexLength(HexLength);
            }
        }

        public bool HasPath { get { return !string.IsNullOrEmpty(Path); } }
    }

    /// <summary>一次解析的结果：条目、实际用的编码名与有效内容行数（供内容嗅探用）。</summary>
    public class ChecksumDocument
    {
        /// <summary>解析出的条目，顺序与文件里的一致。</summary>
        public ChecksumEntry[] Entries { get; set; }

        /// <summary>实际使用的编码名，例如 UTF-8 BOM、GB18030。</summary>
        public string EncodingName { get; set; }

        /// <summary>来源文件路径。</summary>
        public string SourcePath { get; set; }

        /// <summary>非空、非注释的行数，用于判断这份文件像不像校验记录。</summary>
        public int ContentLineCount { get; set; }

        /// <summary>
        /// 整份记录的算法，由文件后缀决定（.md5 → MD5、.sha3-256 → SHA3-256）。
        /// 后缀认不出来但所有条目的值长度一致时退而按长度推断；
        /// 同长度有多个候选时为 null，表示不确定，按候选比对。
        /// </summary>
        public HashAlgorithmInfo Algorithm { get; set; }

        /// <summary>这份记录的候选算法集合。</summary>
        public List<HashAlgorithmInfo> Candidates { get; set; }
    }

    /// <summary>
    /// 校验记录读写。解析口径尽量宽容，因为这类文件来自四面八方：
    /// 编码交给 TextEncoding 探测；行格式支持 sha256sum 的「值 + 引导符 + 文件名」、
    /// 「文件名 = 值」、「SHA256 (文件名) = 值」以及只有值没有文件名；
    /// 算法先看后缀，认不出来按值的十六进制长度推断，同长度多候选时全部保留；
    /// 文件名允许带引号、带子目录、少写扩展名。
    /// </summary>
    public static class ChecksumFile
    {
        /// <summary>默认写出的校验文件扩展名，实际保存时会按算法替换。</summary>
        public const string Extension = ".sha256";

        /// <summary>
        /// 能明确认定为校验记录的纯文本容器扩展名。
        /// 注意 .txt 不在这里：它太宽泛，任何说明文字都可能叫 readme.txt，
        /// 认作记录会把用户拖进来的普通文本误当成校验记录去反推目标文件；
        /// .txt 只作为「剥掉容器后缀再认算法」的候选。
        /// </summary>
        private static readonly string[] _recordContainerExtensions =
        {
            ".sum", ".checksum", ".checksums", ".ck"
        };

        /// <summary>可以剥掉一层再认算法后缀的容器扩展名。</summary>
        private static readonly string[] _containerExtensions =
        {
            ".txt", ".text", ".sum", ".checksum", ".checksums", ".ck", ".lst", ".log"
        };

        /// <summary>行内出现的一串十六进制；长度限制由已知算法集合决定，见 BuildHashPattern。</summary>
        private static readonly Regex _hashPattern = BuildHashPattern();

        /// <summary>行内写明的算法名，例如「SHA256 (file) = ...」里的 SHA256。</summary>
        private static readonly Regex _algorithmPattern = new Regex(
            @"^(?<name>[A-Za-z][A-Za-z0-9\-_\/\.]*?)\s*(?=\(|:|=|\s)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>解析一个校验记录。文件不存在或读不出时抛出 IO 异常，由调用方决定如何提示。</summary>
        public static ChecksumDocument Parse(string path)
        {
            DecodedText decoded = TextEncoding.Read(path);
            return ParseText(decoded.Text, decoded.EncodingName, path);
        }

        /// <summary>解析一个校验记录，只要条目。</summary>
        public static ChecksumEntry[] Read(string path)
        {
            return Parse(path).Entries;
        }

        /// <summary>按已解码的文本解析。单独抽出来便于测试与内容嗅探。</summary>
        public static ChecksumDocument ParseText(string text, string encodingName, string sourcePath)
        {
            HashAlgorithmInfo byExtension = HashAlgorithms.ByExtension(sourcePath);

            var document = new ChecksumDocument
            {
                Entries = new ChecksumEntry[0],
                EncodingName = encodingName,
                SourcePath = sourcePath,
                ContentLineCount = 0,
                Algorithm = byExtension,
                Candidates = byExtension != null
                    ? new List<HashAlgorithmInfo> { byExtension }
                    : new List<HashAlgorithmInfo>()
            };

            if (string.IsNullOrEmpty(text)) return document;
            // 统一换行：\r 单独出现（老 Mac 文本）也要当换行。

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            var results = new List<ChecksumEntry>(lines.Length);
            int contentLines = 0;

            foreach (string rawLine in lines)
            {
                if (rawLine == null) continue;

                string line = rawLine.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#", StringComparison.Ordinal)) continue;

                contentLines++;

                ChecksumEntry entry = ParseLine(line, byExtension);
                if (entry != null) results.Add(entry);
            }

            document.ContentLineCount = contentLines;
            document.Entries = results.ToArray();

            if (document.Entries.Length > 0)
            {
                List<HashAlgorithmInfo> inferred = InferFromEntries(document.Entries);

                bool suffixMatchesValues = byExtension != null
                    && document.Entries[0].HexLength == byExtension.HexLength;

                if (suffixMatchesValues && byExtension != null)
                {
                    document.Algorithm = byExtension;
                    document.Candidates = new List<HashAlgorithmInfo> { byExtension };
                }
                else if (inferred.Count == 1)
                {
                    document.Algorithm = inferred[0];
                    document.Candidates = inferred;
                }
                else
                {
                    document.Algorithm = null;
                    document.Candidates = inferred;
                }
            }

            return document;
        }

        /// <summary>按条目值长度推断候选算法：长度必须一致，否则说明这份记录是混排的。</summary>
        private static List<HashAlgorithmInfo> InferFromEntries(ChecksumEntry[] entries)
        {
            var lengths = new HashSet<int>();
            foreach (ChecksumEntry entry in entries)
            {
                if (entry.HexLength > 0) lengths.Add(entry.HexLength);
            }

            if (lengths.Count != 1) return new List<HashAlgorithmInfo>();

            var list = new List<HashAlgorithmInfo>();
            foreach (ChecksumEntry entry in entries)
            {
                foreach (HashAlgorithmInfo info in entry.Candidates)
                {
                    if (!list.Contains(info)) list.Add(info);
                }
            }
            return list;
        }

        /// <summary>解析单行。识别不出合法摘要值时返回 null，由调用方忽略该行。</summary>
        public static ChecksumEntry ParseLine(string line)
        {
            return ParseLine(line, null);
        }

        /// <summary>解析单行，并带上记录文件后缀给出的算法提示。</summary>
        public static ChecksumEntry ParseLine(string line, HashAlgorithmInfo extensionHint)
        {
            if (string.IsNullOrEmpty(line)) return null;

            string text = line.Trim();
            if (text.Length == 0) return null;

            // 常见格式里文件名在值后面（sha256sum）；少数工具写在值前面，
            // 后面没有东西时再回退到前面去找。
            Match match = _hashPattern.Match(text);
            if (!match.Success) return null;

            string hash = match.Value.ToLowerInvariant();
            string before = text.Substring(0, match.Index);
            string after = text.Substring(match.Index + match.Length);

            string path = ExtractPathAfter(after);
            if (path.Length == 0) path = ExtractPathBefore(before);

            HashAlgorithmInfo algorithm = extensionHint;

            if (algorithm == null)
            {
                algorithm = DetectInlineAlgorithm(before, after);
            }

            if (algorithm != null && algorithm.HexLength != hash.Length)
            {
                List<HashAlgorithmInfo> byLength = HashAlgorithms.ByHexLength(hash.Length);

                if (byLength.Count == 1) algorithm = byLength[0];
                else algorithm = null;
            }

            if (algorithm == null)
            {
                List<HashAlgorithmInfo> candidates = HashAlgorithms.ByHexLength(hash.Length);
                if (candidates.Count == 1) algorithm = candidates[0];
            }

            return new ChecksumEntry
            {
                Hash = hash,
                Path = path,
                RawLine = line,
                Algorithm = algorithm
            };
        }

        /// <summary>从值的两侧找行内写明的算法名；行内写的优先于后缀。</summary>
        private static HashAlgorithmInfo DetectInlineAlgorithm(string before, string after)
        {
            foreach (string fragment in new[] { before, after })
            {
                if (string.IsNullOrEmpty(fragment)) continue;

                Match match = _algorithmPattern.Match(fragment.Trim());
                if (!match.Success) continue;

                HashAlgorithmInfo info = HashAlgorithms.ByName(match.Groups["name"].Value);
                if (info != null) return info;
            }
            return null;
        }

        /// <summary>从值的后面取文件名：剥掉引导符、分隔符、引号与括号。</summary>
        private static string ExtractPathAfter(string after)
        {
            string value = (after ?? string.Empty).Trim();
            if (value.Length == 0) return string.Empty;

            while (value.Length > 0)
            {
                char first = value[0];
                if (first == '*' || first == '?' || first == '=' || first == ':'
                    || first == '-' || first == '>' || char.IsWhiteSpace(first))
                {
                    value = value.Substring(1);
                    continue;
                }
                break;
            }

            value = TrimWrapped(value, '(', ')');
            value = TrimWrapped(value, '[', ']');
            return TrimQuotes(value).Trim();
        }

        /// <summary>从值的前面取文件名：应对「文件名 = 值」「SHA256 (文件名) = 值」两种写法。</summary>
        private static string ExtractPathBefore(string before)
        {
            string value = (before ?? string.Empty).Trim();
            if (value.Length == 0) return string.Empty;

            value = value.TrimEnd('=', ':', ' ', '\t').Trim();
            if (value.EndsWith("->", StringComparison.Ordinal))
            {
                value = value.Substring(0, value.Length - 2).Trim();
            }
            if (value.Length == 0) return string.Empty;

            Match algorithm = _algorithmPattern.Match(value);
            if (algorithm.Success && HashAlgorithms.ByName(algorithm.Groups["name"].Value) != null)
            {
                value = value.Substring(algorithm.Length).TrimStart(' ', '\t', ':', '=');
            }

            value = value.TrimEnd('=', ':', ' ', '\t').Trim();
            value = TrimWrapped(value, '(', ')');
            value = TrimWrapped(value, '[', ']');
            return TrimQuotes(value).Trim();
        }

        /// <summary>成对的外层括号一起剥掉；只有一半时保持原样。</summary>
        private static string TrimWrapped(string value, char open, char close)
        {
            if (value.Length < 2) return value;
            if (value[0] != open || value[value.Length - 1] != close) return value;
            return value.Substring(1, value.Length - 2).Trim();
        }

        /// <summary>去掉成对的引号（单引号、双引号、反引号）。</summary>
        private static string TrimQuotes(string value)
        {
            if (value.Length < 2) return value;

            char first = value[0];
            char last = value[value.Length - 1];
            if ((first == '"' || first == '\'' || first == '`') && first == last)
            {
                return value.Substring(1, value.Length - 2).Trim();
            }
            return value;
        }

        /// <summary>规范化记录里的路径：去空白、去引号、去引导符、统一斜杠。只做文本层面的事，不碰磁盘。</summary>
        public static string NormalizeRecordedPath(string raw)
        {
            string value = (raw ?? string.Empty).Trim();
            value = TrimQuotes(value).Trim();

            while (value.Length > 0 && (value[0] == '*' || value[0] == '?' || char.IsWhiteSpace(value[0])))
            {
                value = value.Substring(1).TrimStart();
            }

            value = value.Replace('/', '\\');
            return value.Trim();
        }

        /// <summary>取不带扩展名的基名：没有点、点在开头、点在结尾时原样返回。</summary>
        public static string GetBaseName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;

            int dot = name.LastIndexOf('.');
            if (dot <= 0 || dot == name.Length - 1) return name;

            return name.Substring(0, dot);
        }

        /// <summary>
        /// 路径文本里是否有控制字符（含 NUL）。记录来自外部，这类字符会让
        /// `Path.IsPathRooted` / `Path.Combine` / `File.Exists` 直接抛 ArgumentException，
        /// 所以要在调用任何 Path API 之前先挡掉。
        /// </summary>
        public static bool HasControlChars(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;

            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] < ' ') return true;
            }
            return false;
        }

        /// <summary>
        /// 取文件名部分，绝不对不可信输入抛异常。
        /// 记录文件的路径来自外部，可能含 NUL 等非法字符（`Path.GetFileName` 会抛
        /// ArgumentException），那时退回「不做目录拆分、整串当名字」的结果。
        /// </summary>
        public static string SafeFileName(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            try
            {
                return Path.GetFileName(path);
            }
            catch
            {
                return path;
            }
        }

        /// <summary>
        /// 记录里的文件名与磁盘上的文件名是否对得上。除忽略大小写的全名比较外，
        /// 还允许一边少写扩展名：记录写 ASCIIPlayer、磁盘上是 ASCIIPlayer.exe 也算命中。
        /// 但只有「少写或多写扩展名」这一种情况才算：ASCIIPlayer.zip 不能被当成
        /// ASCIIPlayer.exe，那是另一个文件，误认会直接算错哈希。
        /// </summary>
        public static bool FileNameMatches(string recordedPath, string actualFileName)
        {
            string recorded = NormalizeRecordedPath(recordedPath);
            if (recorded.Length == 0) return false;

            string actual = NormalizeRecordedPath(actualFileName);
            if (actual.Length == 0) return false;

            string recordedName = SafeFileName(recorded);
            if (recordedName.Length == 0) recordedName = recorded;

            string actualName = SafeFileName(actual);
            if (actualName.Length == 0) actualName = actual;

            if (string.Equals(recordedName, actualName, StringComparison.OrdinalIgnoreCase)) return true;

            string recordedBase = GetBaseName(recordedName);
            string actualBase = GetBaseName(actualName);

            if (recordedBase.Length > 0
                && string.Equals(recordedBase, actualName, StringComparison.OrdinalIgnoreCase)) return true;

            if (actualBase.Length > 0
                && string.Equals(recordedName, actualBase, StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        /// <summary>扩展名是否为明确的校验记录类型：算法后缀，或 .sum / .checksum / .ck 这类通用容器。</summary>
        public static bool HasRecordExtension(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            if (HashAlgorithms.ByExtension(path) != null) return true;

            string lower = path.ToLowerInvariant();
            foreach (string container in _recordContainerExtensions)
            {
                if (lower.EndsWith(container, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>与文件同目录、同名伴侣的候选路径，按算法目录逐个后缀排列。</summary>
        public static string[] CompanionCandidates(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return new string[0];

            var results = new List<string>();

            foreach (HashAlgorithmInfo info in HashAlgorithms.All)
            {
                foreach (string extension in info.Extensions)
                {
                    string candidate = filePath + extension;
                    if (!results.Contains(candidate)) results.Add(candidate);
                }
            }

            foreach (string container in _containerExtensions)
            {
                string candidate = filePath + container;
                if (!results.Contains(candidate)) results.Add(candidate);
            }

            return results.ToArray();
        }

        /// <summary>
        /// 把一批「值 + 文件名」写成校验记录。默认 UTF-8，可选用 BOM 兼容老工具；
        /// 覆盖前先清掉隐藏与只读属性，否则 FileMode.Create 会失败。
        /// </summary>
        public static void Write(string path, IEnumerable<string> lines, bool withBom)
        {
            if (string.IsNullOrEmpty(path) || lines == null) return;

            if (File.Exists(path))
            {
                FileInfo info = new FileInfo(path);
                if ((info.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden ||
                    (info.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                {
                    info.Attributes = FileAttributes.Normal;
                }
            }

            using (var writer = new StreamWriter(path, false, new System.Text.UTF8Encoding(withBom)))
            {
                foreach (string line in lines)
                {
                    writer.WriteLine(line);
                }
            }
        }

        /// <summary>
        /// 生成一行标准格式的校验文本：hash + 空格 + * + 文件名，与 sha256sum 的文本模式互通。
        /// 文件名本身以 * 开头时前面加下划线，避免和引导符混淆。
        /// </summary>
        public static string FormatLine(string hash, string fileName, bool upperCase)
        {
            string value = HashValues.Format(hash, upperCase);
            string name = fileName ?? string.Empty;

            if (name.StartsWith("*", StringComparison.Ordinal)) name = "_" + name;

            return string.Format(CultureInfo.InvariantCulture, "{0} *{1}", value, name);
        }

        /// <summary>
        /// 按已知算法的长度构造行内摘要匹配：只认长度在已知集合内的十六进制串，
        /// 避免把 CRC32 的 8 位值或随手写的一段十六进制当成摘要。
        /// </summary>
        private static Regex BuildHashPattern()
        {
            var lengths = new List<int>(HashAlgorithms.KnownHexLengths);
            lengths.Sort((a, b) => b.CompareTo(a));

            var alternatives = new List<string>(lengths.Count);
            foreach (int length in lengths)
            {
                alternatives.Add(string.Format(CultureInfo.InvariantCulture, "[0-9A-Fa-f]{{{0}}}", length));
            }

            string body = string.Join("|", alternatives);
            return new Regex(
                string.Format(CultureInfo.InvariantCulture, "(?<![0-9A-Fa-f])({0})(?![0-9A-Fa-f])", body),
                RegexOptions.Compiled | RegexOptions.CultureInvariant);
        }
    }
}
