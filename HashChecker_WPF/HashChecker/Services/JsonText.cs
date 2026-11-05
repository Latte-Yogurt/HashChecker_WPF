using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace HashChecker.Services
{
    /// <summary>JSON 值的类型。</summary>
    public enum JsonKind
    {
        Null,
        Bool,
        Number,
        String,
        Array,
        Object
    }

    /// <summary>
    /// 一个已解析的 JSON 值。只支持设置文件需要的那一小撮：对象、数组、字符串、
    /// 数字、布尔与 null。另提供带默认值的取值方法，让「键缺失 / 类型不对」
    /// 都安静地退回默认，而不是抛异常把整份设置废掉。
    /// </summary>
    public sealed class JsonValue
    {
        private readonly Dictionary<string, JsonValue> _members;
        private readonly List<JsonValue> _items;

        private JsonValue(JsonKind kind)
        {
            Kind = kind;
            if (kind == JsonKind.Object) _members = new Dictionary<string, JsonValue>(StringComparer.OrdinalIgnoreCase);
            if (kind == JsonKind.Array) _items = new List<JsonValue>();
        }

        /// <summary>值类型。</summary>
        public JsonKind Kind { get; private set; }

        /// <summary>字符串、数字与布尔的原始文本。</summary>
        public string Text { get; private set; }

        public bool Bool { get; private set; }

        public double Number { get; private set; }

        public static JsonValue NewObject() { return new JsonValue(JsonKind.Object); }

        public static JsonValue NewArray() { return new JsonValue(JsonKind.Array); }

        public static JsonValue NewString(string value)
        {
            return new JsonValue(JsonKind.String) { Text = value ?? string.Empty };
        }

        public static JsonValue NewBool(bool value)
        {
            return new JsonValue(JsonKind.Bool) { Bool = value, Text = value ? "true" : "false" };
        }

        public static JsonValue NewNumber(double value)
        {
            return new JsonValue(JsonKind.Number) { Number = value, Text = value.ToString("R", CultureInfo.InvariantCulture) };
        }

        public static JsonValue NewNull()
        {
            return new JsonValue(JsonKind.Null) { Text = "null" };
        }

        /// <summary>对象成员数或数组元素数。</summary>
        public int Count
        {
            get
            {
                if (_members != null) return _members.Count;
                if (_items != null) return _items.Count;
                return 0;
            }
        }

        /// <summary>对象的全部键。</summary>
        public IEnumerable<string> Keys
        {
            get { return _members == null ? new string[0] : (IEnumerable<string>)_members.Keys; }
        }

        /// <summary>数组元素；非数组时返回空列表。</summary>
        public IList<JsonValue> Items
        {
            get { return _items ?? new List<JsonValue>(); }
        }

        /// <summary>取对象成员；不存在返回 null。</summary>
        public JsonValue Get(string key)
        {
            if (_members == null || string.IsNullOrEmpty(key)) return null;
            JsonValue value;
            return _members.TryGetValue(key, out value) ? value : null;
        }

        /// <summary>设置对象成员（仅对象类型有效）。</summary>
        public void Set(string key, JsonValue value)
        {
            if (_members == null || string.IsNullOrEmpty(key)) return;
            _members[key] = value;
        }

        /// <summary>追加数组元素（仅数组类型有效）。</summary>
        public void Add(JsonValue value)
        {
            if (_items != null && value != null) _items.Add(value);
        }

        /// <summary>按字符串取值：不是字符串时返回默认值。</summary>
        public string GetString(string key, string fallback)
        {
            JsonValue value = Get(key);
            return value != null && value.Kind == JsonKind.String ? value.Text : fallback;
        }

        /// <summary>按字符串取值，没有时返回 null。</summary>
        public string GetString(string key)
        {
            return GetString(key, null);
        }

        /// <summary>按整数取值：也接受写成字符串的整数，以及整数值的浮点。</summary>
        public int GetInt(string key, int fallback)
        {
            JsonValue value = Get(key);
            if (value == null) return fallback;
            return ToInt(value, fallback);
        }

        /// <summary>按布尔取值：也接受写成字符串的 true / false。</summary>
        public bool GetBool(string key, bool fallback)
        {
            JsonValue value = Get(key);
            if (value == null) return fallback;
            return ToBool(value, fallback);
        }

        /// <summary>按浮点取值。</summary>
        public double GetDouble(string key, double fallback)
        {
            JsonValue value = Get(key);
            if (value == null) return fallback;
            return ToDouble(value, fallback);
        }

        private static int ToInt(JsonValue value, int fallback)
        {
            if (value.Kind == JsonKind.Number)
            {
                double number = value.Number;
                if (double.IsNaN(number) || number < int.MinValue || number > int.MaxValue) return fallback;
                return (int)Math.Round(number);
            }

            if (value.Kind == JsonKind.String)
            {
                int parsed;
                return int.TryParse(value.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                    ? parsed
                    : fallback;
            }

            return fallback;
        }

        private static bool ToBool(JsonValue value, bool fallback)
        {
            if (value.Kind == JsonKind.Bool) return value.Bool;

            if (value.Kind == JsonKind.String)
            {
                string text = value.Text.Trim().ToLowerInvariant();
                if (text == "true" || text == "1" || text == "yes" || text == "on") return true;
                if (text == "false" || text == "0" || text == "no" || text == "off") return false;
            }

            if (value.Kind == JsonKind.Number) return value.Number != 0;

            return fallback;
        }

        private static double ToDouble(JsonValue value, double fallback)
        {
            if (value.Kind == JsonKind.Number) return value.Number;

            if (value.Kind == JsonKind.String)
            {
                double parsed;
                return double.TryParse(value.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
                    ? parsed
                    : fallback;
            }

            return fallback;
        }

        public override string ToString()
        {
            return Kind + ":" + Text;
        }
    }

    /// <summary>
    /// 极简 JSON 解析器。手写而不引库的原因见 AppSettings 的注释。
    /// 容错取向：允许行尾与文尾的多余逗号，允许 // 与 /* */ 注释——配置是人手改的，
    /// 这两条最容易被写进去。语法真错时抛 JsonException，由调用方兜底。
    /// </summary>
    public static class JsonParser
    {
        /// <summary>解析一段 JSON 文本。空文本返回 null。</summary>
        public static JsonValue Parse(string text)
        {
            if (text == null) return null;

            var parser = new Cursor(text);
            parser.SkipWhitespace();

            if (parser.AtEnd) return null;

            JsonValue value = ParseValue(parser);
            parser.SkipWhitespace();

            return value;
        }

        private static JsonValue ParseValue(Cursor cursor)
        {
            if (cursor.AtEnd) throw JsonError(cursor, "意外的结尾");

            char c = cursor.Current;

            switch (c)
            {
                case '{': return ParseObject(cursor);
                case '[': return ParseArray(cursor);
                case '"': return JsonValue.NewString(ParseString(cursor));
                case 't': return ParseLiteral(cursor, "true", JsonValue.NewBool(true));
                case 'f': return ParseLiteral(cursor, "false", JsonValue.NewBool(false));
                case 'n': return ParseLiteral(cursor, "null", JsonValue.NewNull());
                default:
                    if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber(cursor);
                    throw JsonError(cursor, "无法识别的字符 '" + c + "'");
            }
        }

        private static JsonValue ParseObject(Cursor cursor)
        {
            JsonValue result = JsonValue.NewObject();
            cursor.Advance();

            while (true)
            {
                cursor.SkipWhitespace();
                if (cursor.AtEnd) throw JsonError(cursor, "对象没有闭合");

                if (cursor.Current == '}')
                {
                    cursor.Advance();
                    return result;
                }

                string key = ParseString(cursor);
                cursor.SkipWhitespace();

                if (cursor.AtEnd || cursor.Current != ':') throw JsonError(cursor, "键后面缺少冒号");
                cursor.Advance();

                cursor.SkipWhitespace();
                JsonValue value = ParseValue(cursor);
                result.Set(key, value);

                cursor.SkipWhitespace();
                if (cursor.AtEnd) throw JsonError(cursor, "对象没有闭合");

                if (cursor.Current == ',')
                {
                    cursor.Advance();
                    continue;
                }

                if (cursor.Current == '}')
                {
                    cursor.Advance();
                    return result;
                }

                throw JsonError(cursor, "对象成员之间缺少逗号");
            }
        }

        private static JsonValue ParseArray(Cursor cursor)
        {
            JsonValue result = JsonValue.NewArray();
            cursor.Advance();

            while (true)
            {
                cursor.SkipWhitespace();
                if (cursor.AtEnd) throw JsonError(cursor, "数组没有闭合");

                if (cursor.Current == ']')
                {
                    cursor.Advance();
                    return result;
                }

                result.Add(ParseValue(cursor));

                cursor.SkipWhitespace();
                if (cursor.AtEnd) throw JsonError(cursor, "数组没有闭合");

                if (cursor.Current == ',')
                {
                    cursor.Advance();
                    continue;
                }

                if (cursor.Current == ']')
                {
                    cursor.Advance();
                    return result;
                }

                throw JsonError(cursor, "数组元素之间缺少逗号");
            }
        }

        private static string ParseString(Cursor cursor)
        {
            if (cursor.Current != '"') throw JsonError(cursor, "字符串缺少引号");
            cursor.Advance();

            var sb = new StringBuilder();

            while (!cursor.AtEnd)
            {
                char c = cursor.Current;

                if (c == '"')
                {
                    cursor.Advance();
                    return sb.ToString();
                }

                if (c != '\\')
                {
                    sb.Append(c);
                    cursor.Advance();
                    continue;
                }

                cursor.Advance();
                if (cursor.AtEnd) throw JsonError(cursor, "转义序列不完整");

                char escape = cursor.Current;
                cursor.Advance();

                switch (escape)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        sb.Append(ParseHex4(cursor));
                        break;
                    default:
                        throw JsonError(cursor, "不支持的转义 '\\" + escape + "'");
                }
            }

            throw JsonError(cursor, "字符串没有闭合");
        }

        private static char ParseHex4(Cursor cursor)
        {
            int value = 0;
            for (int i = 0; i < 4; i++)
            {
                if (cursor.AtEnd) throw JsonError(cursor, "\\u 后面不足 4 位十六进制");
                char c = cursor.Current;
                int digit = HexDigit(c);
                if (digit < 0) throw JsonError(cursor, "\\u 后面不是十六进制");
                value = value * 16 + digit;
                cursor.Advance();
            }
            return (char)value;
        }

        private static int HexDigit(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        private static JsonValue ParseNumber(Cursor cursor)
        {
            int start = cursor.Position;

            if (cursor.Current == '-') cursor.Advance();

            while (!cursor.AtEnd)
            {
                char c = cursor.Current;
                if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-')
                {
                    cursor.Advance();
                    continue;
                }
                break;
            }

            string text = cursor.Slice(start);
            double value;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                throw JsonError(cursor, "不是合法数字 '" + text + "'");
            }

            return JsonValue.NewNumber(value);
        }

        private static JsonValue ParseLiteral(Cursor cursor, string literal, JsonValue value)
        {
            if (cursor.Position + literal.Length > cursor.Length)
            {
                throw JsonError(cursor, "不完整的字面量");
            }

            for (int i = 0; i < literal.Length; i++)
            {
                if (cursor.Text[cursor.Position + i] != literal[i])
                {
                    throw JsonError(cursor, "无法识别 '" + literal + "'");
                }
            }

            cursor.Advance(literal.Length);
            return value;
        }

        private static JsonException JsonError(Cursor cursor, string message)
        {
            return new JsonException(message + "（位置 " + cursor.Position + "）");
        }

        /// <summary>解析游标：负责位置推进、空白与注释跳过。</summary>
        private sealed class Cursor
        {
            public readonly string Text;
            private int _position;

            public Cursor(string text)
            {
                Text = text;
            }

            public int Position { get { return _position; } }

            public int Length { get { return Text.Length; } }

            public bool AtEnd { get { return _position >= Text.Length; } }

            public char Current { get { return _position < Text.Length ? Text[_position] : '\0'; } }

            public void Advance()
            {
                Advance(1);
            }

            public void Advance(int count)
            {
                _position += count;
                if (_position > Text.Length) _position = Text.Length;
            }

            public string Slice(int start)
            {
                int end = _position;
                if (end > Text.Length) end = Text.Length;
                if (start < 0) start = 0;
                return start >= end ? string.Empty : Text.Substring(start, end - start);
            }

            /// <summary>跳过空白、// 行注释与 /* */ 块注释。</summary>
            public void SkipWhitespace()
            {
                while (!AtEnd)
                {
                    char c = Current;

                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\uFEFF')
                    {
                        Advance();
                        continue;
                    }

                    if (c == '/' && _position + 1 < Text.Length)
                    {
                        char next = Text[_position + 1];

                        if (next == '/')
                        {
                            while (!AtEnd && Current != '\n') Advance();
                            continue;
                        }

                        if (next == '*')
                        {
                            Advance(2);
                            while (_position + 1 < Text.Length
                                   && !(Text[_position] == '*' && Text[_position + 1] == '/'))
                            {
                                Advance();
                            }
                            Advance(2);
                            continue;
                        }
                    }

                    break;
                }
            }
        }
    }

    /// <summary>JSON 语法错误。</summary>
    public sealed class JsonException : Exception
    {
        public JsonException(string message) : base(message)
        {
        }
    }
}
