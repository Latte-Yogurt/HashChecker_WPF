using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace HashChecker.Services
{
    /// <summary>
    /// 配置文件的最小 JSON 读写实现，只覆盖本项目需要的形态：
    /// 顶层一个对象，值为字符串、整数、布尔，此外不接受任何复杂结构。
    ///
    /// 与 <see cref="JsonParser"/> 的分工：后者是完整的 JSON 解析器（对象 / 数组 / 嵌套），
    /// 供需要逐层取值的场合使用；本类只做「配置键值表」这一件事，
    /// 把整份文件摊平成 <c>Dictionary&lt;string,string&gt;</c>，写回时由键表决定顺序与类型。
    /// 两者不互相替代：探针与 <see cref="AppSettings"/> 依赖 <see cref="JsonParser"/> 的
    /// 树形接口，配置的键表驱动逻辑则需要这里的扁平视图。
    ///
    /// 输出是标准 JSON（RFC 8259）：UTF-8 无 BOM、双引号、原生 bool 与整数，
    /// 任何现代 JSON 解析器（jq / Python json / JS JSON.parse）都能直接读。
    /// </summary>
    public static class JsonConfig
    {
        #region 解析

        /// <summary>
        /// 把 JSON 文本解析成键值字典，值统一表示为 string（布尔写成 true/false、整数写成十进制）。
        /// 非法文本返回 null —— 调用方据此走「重建默认配置」的路径。
        /// </summary>
        public static Dictionary<string, string> Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            JsonValue root;
            try
            {
                root = JsonParser.Parse(text);
            }
            catch (JsonException)
            {
                return null;
            }
            catch (Exception)
            {
                return null;
            }

            if (root == null || root.Kind != JsonKind.Object) return null;

            var result = new Dictionary<string, string>(32, StringComparer.OrdinalIgnoreCase);

            foreach (string key in root.Keys)
            {
                JsonValue item = root.Get(key);
                if (item == null) continue;

                switch (item.Kind)
                {
                    case JsonKind.String:
                        result[key] = item.Text ?? string.Empty;
                        break;
                    case JsonKind.Bool:
                        result[key] = item.Bool ? bool.TrueString : bool.FalseString;
                        break;
                    case JsonKind.Number:
                        result[key] = item.Number.ToString("R", CultureInfo.InvariantCulture);
                        break;
                    case JsonKind.Null:
                        result[key] = string.Empty;
                        break;
                    default:
                        // 嵌套结构不是本项目的配置形态，跳过而不是判整份损坏。
                        break;
                }
            }

            return result;
        }

        #endregion

        #region 写入

        /// <summary>
        /// 按固定键序把键值写成标准 JSON。缩进两格、行尾逗号、键序稳定，
        /// 便于用户直接编辑，也让文件在版本间 diff 干净。
        /// </summary>
        public static string Build(IReadOnlyList<KeyValuePair<string, string>> entries,
            IReadOnlyList<bool> isBoolean)
        {
            var builder = new StringBuilder(512);
            builder.Append("{\n");

            for (int i = 0; i < entries.Count; i++)
            {
                string key = entries[i].Key;
                string value = entries[i].Value ?? string.Empty;

                builder.Append("  \"");
                AppendEscaped(builder, key);
                builder.Append("\": ");

                // 布尔与整数按原生 JSON 标量写出，字符串加引号 ——
                // 这样任何 JSON 解析器都能正确还原类型。
                bool raw = isBoolean != null && i < isBoolean.Count && isBoolean[i];
                if (raw)
                {
                    // JSON 只认小写的 true/false；C# 的 bool.ToString() 给出 "True"/"False"，
                    // 直接写出去就是非法 JSON。
                    builder.Append(bool.TryParse(value, out bool flag) && flag ? "true" : "false");
                }
                else if (IsJsonNumber(value))
                {
                    builder.Append(value);
                }
                else
                {
                    builder.Append('"');
                    AppendEscaped(builder, value);
                    builder.Append('"');
                }

                if (i < entries.Count - 1) builder.Append(',');
                builder.Append('\n');
            }

            builder.Append("}\n");
            return builder.ToString();
        }

        /// <summary>
        /// 判断一个值是否应当**不加引号**写进 JSON（即它是一个合法的 JSON 数字）。
        ///
        /// 为什么不能只认整数：窗口坐标是 double（例如 293.33333333333331），
        /// 只认整数会把它们写成带引号的字符串，读回来再解析类型就变了 ——
        /// 实测因此出现过 WindowLeft 是字符串、WindowTop 是数字的同一个文件，
        /// 那种文件用 jq 之类的工具处理时会报类型不一致。
        ///
        /// 认的范围严格按 JSON 数字语法：可选负号、整数部分、可选小数、可选指数。
        /// 空串、带空白的、正号开头的（JSON 不允许 +123）一律按字符串写出。
        /// </summary>
        private static bool IsJsonNumber(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;

            int i = 0;
            int n = value.Length;

            if (value[i] == '-') i++;
            if (i >= n) return false;

            // 整数部分：JSON 不允许前导零（"0123" 非法），但单个 0 合法。
            if (value[i] == '0')
            {
                i++;
            }
            else if (value[i] >= '1' && value[i] <= '9')
            {
                while (i < n && value[i] >= '0' && value[i] <= '9') i++;
            }
            else
            {
                return false;
            }

            // 小数部分
            if (i < n && value[i] == '.')
            {
                i++;
                int digits = 0;
                while (i < n && value[i] >= '0' && value[i] <= '9') { i++; digits++; }
                if (digits == 0) return false;
            }

            // 指数部分
            if (i < n && (value[i] == 'e' || value[i] == 'E'))
            {
                i++;
                if (i < n && (value[i] == '+' || value[i] == '-')) i++;
                int digits = 0;
                while (i < n && value[i] >= '0' && value[i] <= '9') { i++; digits++; }
                if (digits == 0) return false;
            }

            return i == n;
        }

        /// <summary>只转义 JSON 规定必须转义的字符；中文按 UTF-8 原样写出，保持文件可读。</summary>
        private static void AppendEscaped(StringBuilder builder, string value)
        {
            if (value == null) return;

            foreach (char ch in value)
            {
                switch (ch)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (ch < ' ')
                        {
                            builder.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(ch);
                        }
                        break;
                }
            }
        }

        /// <summary>写入文件：UTF-8 无 BOM。</summary>
        public static void WriteFile(string path, string content)
        {
            // 无 BOM：带 BOM 的文件会让不少解析器把首个键当成无效内容。
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }

        #endregion
    }
}
