using System;
using System.IO;
using System.Text;

namespace HashChecker.Services
{
    /// <summary>一次文本解码的结果：文本内容 + 实际使用的编码名，便于提示与排错。</summary>
    public class DecodedText
    {
        /// <summary>解码后的文本（BOM 已剥离）。</summary>
        public string Text { get; set; }

        /// <summary>实际使用的编码名，例如 UTF-8、UTF-8 BOM、GB18030。</summary>
        public string EncodingName { get; set; }
    }

    /// <summary>
    /// 校验记录的宽容解码。.sha256 是别人给的文件，编码完全不可控：
    /// UTF-8、带 BOM 的 UTF-8、GB2312 / GBK / GB18030、Big5、Shift_JIS、UTF-16（有无 BOM）、
    /// UTF-32，甚至从网页复制出来带了零宽字符的，都要能读出来。
    ///
    /// 探测顺序：BOM 最可靠先按它定；没有 BOM 时能通过严格 UTF-8 校验的一律当 UTF-8；
    /// 字节里大量 0x00 且集中在奇数位或偶数位时按 UTF-16 处理；
    /// 否则按中文环境（GB18030 → GBK → Big5 → Shift_JIS → EUC-KR）依次尝试；
    /// 全失败时按宽松 UTF-8 解一遍，保证永远能读出字节而不是抛异常。
    /// </summary>
    public static class TextEncoding
    {
        private const int BomLengthUtf8 = 3;

        /// <summary>读取文件并按宽容策略解码。</summary>
        public static DecodedText Read(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            return Decode(bytes);
        }

        /// <summary>解码一段字节。</summary>
        public static DecodedText Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return new DecodedText { Text = string.Empty, EncodingName = "UTF-8" };
            }

            DecodedText byBom = TryBom(bytes);
            if (byBom != null) return byBom;

            DecodedText byUtf16 = TryUtf16WithoutBom(bytes);
            if (byUtf16 != null) return byUtf16;

            DecodedText byUtf8 = TryStrictUtf8(bytes);
            if (byUtf8 != null) return byUtf8;

            string[] legacy = { "GB18030", "GBK", "Big5", "Shift_JIS", "EUC-KR" };
            foreach (string name in legacy)
            {
                DecodedText decoded = TrySingleByteEncoding(bytes, name);
                if (decoded != null) return decoded;
            }

            return new DecodedText
            {
                Text = Clean(new UTF8Encoding(false, false).GetString(bytes)),
                EncodingName = "UTF-8 (fallback)"
            };
        }

        /// <summary>
        /// 统一清理解码后的文本：剥掉残留 BOM、零宽字符与行首的奇怪空白。
        /// 这些字符不是空白，Trim() 去不掉，会顶着十六进制值让整行解析失败。
        /// </summary>
        public static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (c == '\uFEFF' || c == '\u200B' || c == '\u200C' || c == '\u200D'
                    || c == '\u2060' || c == '\u200E' || c == '\u200F'
                    || (c >= '\u202A' && c <= '\u202E'))
                {
                    continue;
                }

                char normalized = c;
                if (normalized == '\u3000' || normalized == '\u00A0') normalized = ' ';
                sb.Append(normalized);
            }

            return sb.ToString();
        }

        /// <summary>按 BOM 定编码。没有 BOM 时返回 null。</summary>
        private static DecodedText TryBom(byte[] bytes)
        {
            Encoding encoding = null;
            string name = null;

            if (bytes.Length >= 4)
            {
                if (bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
                {
                    encoding = Encoding.UTF32;
                    name = "UTF-32 BE";
                }
                else if (bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
                {
                    encoding = new UTF32Encoding(false, true);
                    name = "UTF-32 LE";
                }
            }

            if (encoding == null && bytes.Length >= BomLengthUtf8
                && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                encoding = new UTF8Encoding(true);
                name = "UTF-8 BOM";
            }

            if (encoding == null && bytes.Length >= 2)
            {
                if (bytes[0] == 0xFF && bytes[1] == 0xFE)
                {
                    encoding = new UnicodeEncoding(false, true);
                    name = "UTF-16 LE BOM";
                }
                else if (bytes[0] == 0xFE && bytes[1] == 0xFF)
                {
                    encoding = new UnicodeEncoding(true, true);
                    name = "UTF-16 BE BOM";
                }
            }

            if (encoding == null) return null;

            try
            {
                return new DecodedText
                {
                    Text = Clean(encoding.GetString(bytes)),
                    EncodingName = name
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>严格 UTF-8：解得出就是 UTF-8，解不出返回 null。</summary>
        private static DecodedText TryStrictUtf8(byte[] bytes)
        {
            try
            {
                var strict = new UTF8Encoding(false, true);
                string text = strict.GetString(bytes);
                return new DecodedText { Text = Clean(text), EncodingName = "UTF-8" };
            }
            catch (DecoderFallbackException)
            {
                return null;
            }
        }

        /// <summary>
        /// 无 BOM 的 UTF-16：靠「0x00 扎堆在奇数位还是偶数位」判断。纯 ASCII 的 UTF-16 文本
        /// 每个字符都带一个 0x00 字节，特征非常明显。
        ///
        /// 这条必须排在严格 UTF-8 之前：UTF-16 文本恰好也能被 UTF-8「成功」解出来
        /// （每个字节都是合法起点），留给我们的是一串带 NUL 的怪文本，
        /// 于是值解析失败、整份记录变成空文件。
        /// </summary>
        private static DecodedText TryUtf16WithoutBom(byte[] bytes)
        {
            int sample = Math.Min(bytes.Length, 4096);
            if (sample < 4) return null;

            int evenZero = 0;
            int oddZero = 0;
            int pairs = sample / 2;
            for (int i = 0; i + 1 < sample; i += 2)
            {
                if (bytes[i] == 0x00) evenZero++;
                if (bytes[i + 1] == 0x00) oddZero++;
            }

            Encoding encoding;
            string name;
            if (oddZero > pairs * 0.5 && evenZero < pairs * 0.05)
            {
                encoding = new UnicodeEncoding(false, false);
                name = "UTF-16 LE";
            }
            else if (evenZero > pairs * 0.5 && oddZero < pairs * 0.05)
            {
                encoding = new UnicodeEncoding(true, false);
                name = "UTF-16 BE";
            }
            else
            {
                return null;
            }

            try
            {
                return new DecodedText { Text = Clean(encoding.GetString(bytes)), EncodingName = name };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 用某个代码页解一次。GB 系几乎不会失败，Big5 与 Shift_JIS 会失败，
        /// 失败就说明不是它，继续下一个。
        /// </summary>
        private static DecodedText TrySingleByteEncoding(byte[] bytes, string encodingName)
        {
            Encoding encoding;
            try
            {
                encoding = Encoding.GetEncoding(encodingName);
            }
            catch (ArgumentException)
            {
                return null;
            }

            try
            {
                string text = encoding.GetString(bytes);
                return new DecodedText
                {
                    Text = Clean(text),
                    EncodingName = encodingName.ToUpperInvariant() == "GBK" ? "GBK" : encodingName
                };
            }
            catch (DecoderFallbackException)
            {
                return null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }
}
