using System.Globalization;
using System.Text;
using HashChecker.Services;

namespace HashChecker.Models
{
    /// <summary>
    /// 摘要文本值的格式化与规范化。算法不止一种，长度就不是固定的 64：
    /// 位数由算法决定，比对前统一规范化，比对时按算法给定位数。
    /// </summary>
    public static class HashValues
    {
        private static readonly char[] _hexLower = "0123456789abcdef".ToCharArray();

        /// <summary>字节数组转小写十六进制串。</summary>
        public static string ToHex(byte[] bytes)
        {
            if (bytes == null) return string.Empty;

            var chars = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                chars[i * 2] = _hexLower[b >> 4];
                chars[i * 2 + 1] = _hexLower[b & 0x0F];
            }
            return new string(chars);
        }

        /// <summary>按设置决定输出大小写。</summary>
        public static string Format(string hex, bool upperCase)
        {
            if (string.IsNullOrEmpty(hex)) return string.Empty;
            return upperCase ? hex.ToUpperInvariant() : hex.ToLowerInvariant();
        }

        /// <summary>
        /// 规范化摘要值：去空白、去引号、去算法前缀与引导符，统一转小写。
        /// 不是纯十六进制时返回 null；长度不在这里限制，由调用方按算法判断。
        /// </summary>
        public static string Normalize(string raw)
        {
            return HashAlgorithms.NormalizeDigest(raw);
        }

        /// <summary>规范化后再校验长度：长度与指定算法不符时返回 null。</summary>
        public static string Normalize(string raw, HashAlgorithmInfo algorithm)
        {
            string value = Normalize(raw);
            if (value == null) return null;
            if (algorithm == null) return value;
            return value.Length == algorithm.HexLength ? value : null;
        }

        /// <summary>算一段文本（UTF-8）的摘要，供计算器使用。</summary>
        public static string HashText(string text, HashAlgorithmInfo algorithm)
        {
            HashAlgorithmInfo target = algorithm ?? HashAlgorithms.Default;
            byte[] bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);

            IHashCore core = target.CreateCore();
            core.Initialize();
            core.Append(bytes, 0, bytes.Length);
            return ToHex(core.Finish());
        }

        /// <summary>按字节数格式化成 KB / MB 等，用于状态栏与属性框。</summary>
        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes.ToString("N0", CultureInfo.CurrentCulture) + " B";

            double value = bytes;
            string[] units = { "KB", "MB", "GB", "TB", "PB" };
            int unit = -1;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return string.Format(CultureInfo.CurrentCulture, "{0:N2} {1}", value, units[unit]);
        }
    }
}
