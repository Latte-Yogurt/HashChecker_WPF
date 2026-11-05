using System;
using System.Collections.Generic;
using System.IO;

namespace HashChecker.Services
{
    /// <summary>校验记录里一条记录解析出的目标文件。</summary>
    public class ResolvedEntry
    {
        /// <summary>来源记录条目。</summary>
        public ChecksumEntry Entry { get; set; }

        /// <summary>解析到磁盘上的真实文件。</summary>
        public string TargetPath { get; set; }
    }

    /// <summary>一份校验记录的解析结果。</summary>
    public class RecordResolution
    {
        /// <summary>记录文件本身。</summary>
        public string RecordPath { get; set; }

        /// <summary>记录文件的实际编码名。</summary>
        public string EncodingName { get; set; }

        /// <summary>能对应到磁盘文件的记录。</summary>
        public List<ResolvedEntry> Resolved { get; set; }

        /// <summary>记录里有、磁盘上找不到的条数。</summary>
        public int UnresolvedCount { get; set; }

        /// <summary>记录里没有任何可用条目时的原因；正常时为 null。</summary>
        public string Error { get; set; }

        /// <summary>这份记录的算法（由后缀判定或按值长度推断）。可能为 null。</summary>
        public HashAlgorithmInfo Algorithm { get; set; }

        /// <summary>这份记录的候选算法集合。</summary>
        public List<HashAlgorithmInfo> Candidates { get; set; }
    }

    /// <summary>为目标文件找到的校验记录。</summary>
    public class RecordMatch
    {
        /// <summary>记录文件路径。</summary>
        public string RecordPath { get; set; }

        /// <summary>记录里与该文件对应的小写摘要值。</summary>
        public string Hash { get; set; }

        /// <summary>记录文件的实际编码名。</summary>
        public string EncodingName { get; set; }

        /// <summary>这条记录对应的算法；不确定时为 null（按候选比对）。</summary>
        public HashAlgorithmInfo Algorithm { get; set; }
    }

    /// <summary>
    /// 校验记录与目标文件之间的双向查找。两个方向都要支持：
    /// 1. 拖进一份 .sha256（或任何内容像校验记录的文件）→ 反推出它记录的文件；
    /// 2. 拖进普通文件 → 在同目录里找引用它的记录（不要求记录与文件同名）。
    /// 文件名比较统一走 ChecksumFile.FileNameMatches，因此记录里少写扩展名也能对上。
    /// </summary>
    public static class ChecksumResolver
    {
        /// <summary>内容嗅探的字节上限：校验记录都很小，超过这个尺寸不可能是记录。</summary>
        private const long SniffMaxBytes = 4L * 1024 * 1024;

        /// <summary>目录扫描时二次嗅探的字节上限，避免为了找记录把整个目录的文件都读一遍。</summary>
        private const long ScanSniffMaxBytes = 256L * 1024;

        /// <summary>不像校验记录的二进制扩展名，扫描时直接跳过。</summary>
        private static readonly string[] _binaryExtensions =
        {
            ".exe", ".dll", ".sys", ".bin", ".dat", ".db", ".zip", ".7z", ".rar",
            ".gz", ".bz2", ".xz", ".iso", ".img", ".msi", ".cab", ".pak", ".jar",
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tif", ".tiff",
            ".mp3", ".mp4", ".mkv", ".avi", ".mov", ".wav", ".flac", ".webm",
            ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
            ".ttf", ".otf", ".woff", ".woff2", ".so", ".a", ".lib", ".obj", ".pdb",
            ".lnk", ".url", ".cur"
        };

        /// <summary>
        /// 一次批量添加里往往有多个同目录文件，逐个重解析同一份记录会把读盘放大到平方级。
        /// 这里按「路径 + 大小 + 修改时间」缓存解析结果，批量开始与结束时各清一次。
        /// </summary>
        private static readonly Dictionary<string, CachedDocument> _documentCache =
            new Dictionary<string, CachedDocument>(StringComparer.OrdinalIgnoreCase);

        private static readonly object _cacheLock = new object();

        /// <summary>清空解析缓存。</summary>
        public static void ResetCache()
        {
            lock (_cacheLock)
            {
                _documentCache.Clear();
            }
        }

        /// <summary>解析结果缓存项。磁盘文件改了（大小或修改时间变了）就自然失效。</summary>
        private class CachedDocument
        {
            public long Length { get; set; }
            public DateTime Stamp { get; set; }
            public ChecksumDocument Document { get; set; }
        }

        /// <summary>带缓存地解析一份记录。取不到文件属性时直接解析，不进缓存，避免把陈旧内容留在表里。</summary>
        private static ChecksumDocument LoadDocument(string recordPath)
        {
            long length = 0;
            DateTime stamp = DateTime.MinValue;

            try
            {
                FileInfo info = new FileInfo(recordPath);
                length = info.Length;
                stamp = info.LastWriteTimeUtc;
            }
            catch
            {
                return ChecksumFile.Parse(recordPath);
            }

            lock (_cacheLock)
            {
                CachedDocument cached;
                if (_documentCache.TryGetValue(recordPath, out cached)
                    && cached.Length == length
                    && cached.Stamp == stamp)
                {
                    return cached.Document;
                }
            }

            ChecksumDocument parsed = ChecksumFile.Parse(recordPath);

            lock (_cacheLock)
            {
                _documentCache[recordPath] = new CachedDocument
                {
                    Length = length,
                    Stamp = stamp,
                    Document = parsed
                };
            }

            return parsed;
        }

        /// <summary>
        /// 判断一个文件是不是校验记录。先看扩展名，扩展名不认识时再按内容判断：
        /// 每一行有效内容都能解析成「十六进制值 + 可选文件名」才算记录。
        /// </summary>
        public static bool LooksLikeRecord(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            if (ChecksumFile.HasRecordExtension(path)) return true;
            return SniffRecord(path);
        }

        /// <summary>
        /// 按内容判断是不是校验记录。读不出来或不像时返回 false，绝不抛异常。
        /// 有一行解析不出来就认为不是记录，这样「源码里恰好写了哈希常量」之类的文件
        /// 不会被误判；读不到（占用、无权限、编码异常）也当作不是记录。
        /// </summary>
        public static bool SniffRecord(string path)
        {
            try
            {
                FileInfo info = new FileInfo(path);
                if (info.Length <= 0 || info.Length > SniffMaxBytes) return false;

                DecodedText decoded = TextEncoding.Read(path);
                ChecksumDocument document = ChecksumFile.ParseText(decoded.Text, decoded.EncodingName, path);

                return document.Entries.Length > 0
                    && document.Entries.Length == document.ContentLineCount;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 由校验记录反推目标文件。记录里的路径按以下顺序尝试：
        /// 绝对路径 → 记录所在目录 + 记录里的相对路径 → 只取基名 → 目录内文件名匹配
        /// → 用记录文件名推断（ASCIIPlayer.sha256 对应 ASCIIPlayer*）。
        /// </summary>
        public static RecordResolution ResolveRecord(string recordPath)
        {
            var resolution = new RecordResolution
            {
                RecordPath = recordPath,
                Resolved = new List<ResolvedEntry>(),
                UnresolvedCount = 0
            };

            ChecksumDocument document;
            try
            {
                document = ChecksumFile.Parse(recordPath);
            }
            catch (Exception ex)
            {
                resolution.Error = ex.Message;
                return resolution;
            }

            resolution.EncodingName = document.EncodingName;
            resolution.Algorithm = document.Algorithm;
            resolution.Candidates = document.Candidates ?? new List<HashAlgorithmInfo>();

            if (document.Entries.Length == 0)
            {
                resolution.Error = ChecksumFile.HasRecordExtension(recordPath)
                    ? Localization.Get("MsgRecordEmpty")
                    : Localization.Get("MsgNotAChecksumFile");
                return resolution;
            }

            string folder;
            try
            {
                folder = Path.GetDirectoryName(Path.GetFullPath(recordPath));
            }
            catch
            {
                folder = Path.GetDirectoryName(recordPath);
            }

            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) folder = string.Empty;

            string recordFull;
            try { recordFull = Path.GetFullPath(recordPath); }
            catch { recordFull = recordPath; }

            string recordBase = ChecksumFile.GetBaseName(ChecksumFile.SafeFileName(recordFull));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ChecksumEntry entry in document.Entries)
            {
                string target = LocateTarget(entry, folder, recordBase, recordFull);
                if (target == null)
                {
                    resolution.UnresolvedCount++;
                    continue;
                }

                if (!seen.Add(target)) continue;
                resolution.Resolved.Add(new ResolvedEntry { Entry = entry, TargetPath = target });
            }

            if (resolution.Resolved.Count == 0 && resolution.Error == null)
            {
                resolution.Error = Localization.Format("MsgRecordNoTarget", resolution.UnresolvedCount);
            }

            return resolution;
        }

        /// <summary>
        /// 为一个普通文件寻找引用它的校验记录。顺序很重要：先看算法后缀伴侣
        /// （file.exe.md5 / file.exe.sha256 …），这是需求里的主路径；找不到时再退回目录扫描，
        /// 那时记录文件名可以完全不相干。同一文件可能有多份不同算法的记录，
        /// 全部收集后优先返回与目标取值长度对得上的那一份。
        /// </summary>
        public static RecordMatch FindRecordForFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;

            string full;
            try { full = Path.GetFullPath(filePath); }
            catch { full = filePath; }

            string fileName = Path.GetFileName(full);
            string folder = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;

            var matches = new List<RecordMatch>();
            foreach (string candidate in AlgorithmCompanions(full))
            {
                if (!File.Exists(candidate)) continue;
                RecordMatch match = MatchInRecord(candidate, fileName);
                if (match != null) matches.Add(match);
            }

            if (matches.Count > 0) return ChooseStrongest(matches);

            foreach (string candidate in CandidateRecords(full, fileName, folder))
            {
                if (string.Equals(candidate, full, StringComparison.OrdinalIgnoreCase)) continue;
                RecordMatch match = MatchInRecord(candidate, fileName);
                if (match != null) return match;
            }

            return null;
        }

        /// <summary>
        /// 同一文件有多份记录时选一份：算法明确的优先于只按长度推断的，
        /// 明确算法里选摘要更长的那份（SHA-256 比 MD5 更能说明文件没被改过）。
        /// </summary>
        private static RecordMatch ChooseStrongest(List<RecordMatch> matches)
        {
            RecordMatch best = null;
            for (int i = 0; i < matches.Count; i++)
            {
                RecordMatch current = matches[i];
                if (best == null)
                {
                    best = current;
                    continue;
                }

                bool currentExplicit = current.Algorithm != null;
                bool bestExplicit = best.Algorithm != null;
                if (currentExplicit != bestExplicit)
                {
                    if (currentExplicit) best = current;
                    continue;
                }

                int currentLength = current.Hash == null ? 0 : current.Hash.Length;
                int bestLength = best.Hash == null ? 0 : best.Hash.Length;
                if (currentLength > bestLength) best = current;
            }
            return best;
        }

        /// <summary>同名伴侣候选里只保留「扩展名能对上某个算法」的那些，file.exe.bak 之类不算。</summary>
        private static List<string> AlgorithmCompanions(string fullPath)
        {
            var list = new List<string>();
            foreach (string candidate in ChecksumFile.CompanionCandidates(fullPath))
            {
                if (HashAlgorithms.ByExtension(candidate) == null) continue;
                list.Add(candidate);
            }
            return list;
        }

        /// <summary>在一份记录里找与文件名对应的值；没有则返回 null。只有一条记录且没写文件名时同样认可。</summary>
        private static RecordMatch MatchInRecord(string recordPath, string fileName)
        {
            try
            {
                ChecksumDocument document = LoadDocument(recordPath);
                if (document.Entries.Length == 0) return null;

                foreach (ChecksumEntry entry in document.Entries)
                {
                    if (!ChecksumFile.FileNameMatches(entry.Path, fileName)) continue;

                    return new RecordMatch
                    {
                        RecordPath = recordPath,
                        Hash = entry.Hash,
                        EncodingName = document.EncodingName,
                        Algorithm = entry.Algorithm ?? document.Algorithm
                    };
                }

                if (document.Entries.Length == 1 && !document.Entries[0].HasPath)
                {
                    ChecksumEntry only = document.Entries[0];
                    return new RecordMatch
                    {
                        RecordPath = recordPath,
                        Hash = only.Hash,
                        EncodingName = document.EncodingName,
                        Algorithm = only.Algorithm ?? document.Algorithm
                    };
                }
            }
            catch
            {
                /* 记录读不出来就当作没有匹配，交给其他记录继续找 */
            }

            return null;
        }

        /// <summary>
        /// 目录里可能是校验记录的文件。排序规则：与目标同基名的最优先，
        /// 其次是记录类扩展名，最后才是别的文本文件；目录不可枚举时放弃扫描。
        /// </summary>
        private static List<string> CandidateRecords(string fullPath, string fileName, string folder)
        {
            var preferred = new List<string>();
            var secondary = new List<string>();

            string targetBase = ChecksumFile.GetBaseName(fileName);

            string[] files;
            try
            {
                files = Directory.GetFiles(folder);
            }
            catch
            {
                return preferred;
            }

            foreach (string candidate in files)
            {
                if (string.Equals(candidate, fullPath, StringComparison.OrdinalIgnoreCase)) continue;

                string candidateName = Path.GetFileName(candidate);
                string candidateBase = ChecksumFile.GetBaseName(candidateName);

                bool sameBase = targetBase.Length > 0
                    && string.Equals(candidateBase, targetBase, StringComparison.OrdinalIgnoreCase);

                bool recordExtension = ChecksumFile.HasRecordExtension(candidate);
                if (!sameBase && !recordExtension && !MayBeText(candidate)) continue;

                if (sameBase || recordExtension) preferred.Add(candidate);
                else secondary.Add(candidate);
            }

            preferred.AddRange(secondary);
            return preferred;
        }

        /// <summary>体积足够小、扩展名不像二进制，才值得读一遍做内容嗅探。</summary>
        private static bool MayBeText(string path)
        {
            try
            {
                FileInfo info = new FileInfo(path);
                if (info.Length <= 0 || info.Length > ScanSniffMaxBytes) return false;

                string lower = path.ToLowerInvariant();
                foreach (string extension in _binaryExtensions)
                {
                    if (lower.EndsWith(extension, StringComparison.Ordinal)) return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 按顺序尝试把一条记录落到磁盘上的真实文件；都失败时返回 null。
        /// 记录里没写文件名、或写的名字在这条目录里找不到时，用记录文件自己的名字反推。
        ///
        /// 记录文件的内容不可信，所以对它的文件名有两道关：
        /// 含控制字符（NUL 等）时直接当作「没写文件名」——任何 Path API 都会在它上面抛异常；
        /// 相对路径只在记录所在目录内解析，带 `..` 的越界路径会被归一后判掉。
        /// 绝对路径仍按原样接受——这是记录格式本来的约定（sha256sum 也允许绝对路径）。
        /// </summary>
        private static string LocateTarget(ChecksumEntry entry, string folder, string recordBase, string recordFull)
        {
            string recorded = ChecksumFile.NormalizeRecordedPath(entry.Path);
            if (ChecksumFile.HasControlChars(recorded)) recorded = string.Empty;

            if (recorded.Length > 0)
            {
                if (Path.IsPathRooted(recorded) && File.Exists(recorded)) return recorded;

                if (folder.Length > 0)
                {
                    string combined = TryCombineInside(folder, recorded);
                    if (combined != null && File.Exists(combined)) return combined;

                    string baseOnly = ChecksumFile.SafeFileName(recorded);
                    if (baseOnly.Length > 0 && !ChecksumFile.HasControlChars(baseOnly))
                    {
                        string plain = Path.Combine(folder, baseOnly);
                        if (File.Exists(plain)) return plain;
                    }

                    string byName = FindByFileName(folder, recorded, recordFull);
                    if (byName != null) return byName;
                }
            }

            if (folder.Length > 0 && recordBase.Length > 0)
            {
                string byRecord = FindByFileName(folder, recordBase, recordFull);
                if (byRecord != null) return byRecord;
            }

            return null;
        }

        /// <summary>
        /// 把记录里的相对路径拼到记录目录下，但只在结果仍落在该目录内时才返回路径；
        /// 越界（`..` 穿越）、无法解析（含 NUL 等非法字符）时返回 null。
        /// 这条边界校验是路径安全的第一道关：记录来自外部，不能让它摸到目录之外的文件。
        /// </summary>
        private static string TryCombineInside(string folder, string recorded)
        {
            try
            {
                string root = Path.GetFullPath(folder);
                string combined = Path.GetFullPath(Path.Combine(root, recorded));

                string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar.ToString(),
                    StringComparison.Ordinal)
                    ? root
                    : root + Path.DirectorySeparatorChar;

                if (combined.Length <= root.Length) return null;
                if (!combined.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)) return null;

                return combined;
            }
            catch
            {
                // 含非法字符（如 NUL）或路径过长时无法解析：当作这条记录没写文件名。
                return null;
            }
        }

        /// <summary>在目录里找文件名能对上（含少写扩展名）的文件；a.exe 与 a.exe.exe 并存时取最短的那个。</summary>
        private static string FindByFileName(string folder, string recordedName, string excludePath)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(folder);
            }
            catch
            {
                return null;
            }

            string best = null;
            foreach (string candidate in files)
            {
                if (excludePath.Length > 0
                    && string.Equals(candidate, excludePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string candidateName = Path.GetFileName(candidate);
                if (!ChecksumFile.FileNameMatches(recordedName, candidateName)) continue;

                if (best == null || candidate.Length < best.Length) best = candidate;
            }

            return best;
        }
    }
}
