// Json.cs
// 手写极简 JSON 解析器：不引第三方库（契约要求 csc 直接编译、无 NuGet）。
// 为什么自己写：.NET Framework 4.8 没有内置 System.Text.Json 的可用引用来路，
// 而状态文件只有一种非常固定的形状，没必要上大库。
// 设计原则：凡是读不懂的东西一律回落默认值，绝不让伴侣进程因为坏 JSON 崩掉。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DshPetFloat
{
    internal enum JsonKind
    {
        Null,
        Bool,
        Number,
        String,
        Array,
        Object
    }

    /// <summary>JSON 值容器。字段全部公开，取数据时用下面的容错访问器。</summary>
    internal sealed class JsonValue
    {
        public JsonKind Kind;
        public bool BoolValue;
        public double NumberValue;
        public string StringValue;
        public List<JsonValue> Items;
        public Dictionary<string, JsonValue> Fields;

        public static readonly JsonValue NullValue = new JsonValue();

        public static JsonValue MakeString(string s)
        {
            JsonValue v = new JsonValue();
            v.Kind = JsonKind.String;
            v.StringValue = s == null ? "" : s;
            return v;
        }

        public static JsonValue MakeNumber(double d)
        {
            JsonValue v = new JsonValue();
            v.Kind = JsonKind.Number;
            v.NumberValue = d;
            return v;
        }

        public static JsonValue MakeBool(bool b)
        {
            JsonValue v = new JsonValue();
            v.Kind = JsonKind.Bool;
            v.BoolValue = b;
            return v;
        }

        public static JsonValue MakeObject()
        {
            JsonValue v = new JsonValue();
            v.Kind = JsonKind.Object;
            v.Fields = new Dictionary<string, JsonValue>();
            return v;
        }

        public static JsonValue MakeArray()
        {
            JsonValue v = new JsonValue();
            v.Kind = JsonKind.Array;
            v.Items = new List<JsonValue>();
            return v;
        }

        public bool IsNull { get { return Kind == JsonKind.Null; } }
        public bool IsObject { get { return Kind == JsonKind.Object && Fields != null; } }
        public bool IsArray { get { return Kind == JsonKind.Array && Items != null; } }
        public bool IsString { get { return Kind == JsonKind.String; } }

        /// <summary>取子字段；不存在或类型不符返回 null。</summary>
        public JsonValue Get(string name)
        {
            JsonValue v;
            if (Fields != null && Fields.TryGetValue(name, out v))
                return v;
            return null;
        }

        public string Str(string name, string fallback)
        {
            JsonValue v = Get(name);
            if (v == null)
                return fallback;
            switch (v.Kind)
            {
                case JsonKind.String:
                    return v.StringValue == null ? fallback : v.StringValue;
                case JsonKind.Number:
                    return v.NumberValue.ToString("0.###", CultureInfo.InvariantCulture);
                case JsonKind.Bool:
                    return v.BoolValue ? "true" : "false";
                default:
                    return fallback;
            }
        }

        public long Long(string name, long fallback)
        {
            JsonValue v = Get(name);
            if (v == null)
                return fallback;
            if (v.Kind == JsonKind.Number)
            {
                double d = v.NumberValue;
                if (double.IsNaN(d) || double.IsInfinity(d))
                    return fallback;
                if (d > 9007199254740992.0) return long.MaxValue;
                if (d < -9007199254740992.0) return long.MinValue;
                return (long)Math.Round(d);
            }
            if (v.Kind == JsonKind.String)
            {
                long parsed;
                if (long.TryParse(v.StringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                    return parsed;
                double d;
                if (double.TryParse(v.StringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                    return (long)Math.Round(d);
            }
            return fallback;
        }

        public int Int(string name, int fallback)
        {
            long v = Long(name, fallback);
            if (v > int.MaxValue) return int.MaxValue;
            if (v < int.MinValue) return int.MinValue;
            return (int)v;
        }

        public double Num(string name, double fallback)
        {
            JsonValue v = Get(name);
            if (v == null)
                return fallback;
            if (v.Kind == JsonKind.Number)
            {
                double d = v.NumberValue;
                if (double.IsNaN(d) || double.IsInfinity(d))
                    return fallback;
                return d;
            }
            if (v.Kind == JsonKind.String)
            {
                double d;
                if (double.TryParse(v.StringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                    return d;
            }
            return fallback;
        }

        public bool Bool(string name, bool fallback)
        {
            JsonValue v = Get(name);
            if (v == null)
                return fallback;
            switch (v.Kind)
            {
                case JsonKind.Bool:
                    return v.BoolValue;
                case JsonKind.Number:
                    return v.NumberValue != 0;
                case JsonKind.String:
                    string s = (v.StringValue ?? "").Trim().ToLowerInvariant();
                    if (s == "true" || s == "1" || s == "yes") return true;
                    if (s == "false" || s == "0" || s == "no") return false;
                    return fallback;
                default:
                    return fallback;
            }
        }

        public JsonValue Obj(string name)
        {
            JsonValue v = Get(name);
            return (v != null && v.IsObject) ? v : null;
        }

        public JsonValue Arr(string name)
        {
            JsonValue v = Get(name);
            return (v != null && v.IsArray) ? v : null;
        }

        /// <summary>数组元素，非数组时返回空表（调用方不必判空）。</summary>
        public List<JsonValue> SafeItems()
        {
            if (Items != null)
                return Items;
            return EmptyItems;
        }

        private static readonly List<JsonValue> EmptyItems = new List<JsonValue>();
    }

    internal static class JsonParser
    {
        public static bool TryParse(string text, out JsonValue value)
        {
            value = null;
            if (text == null)
                return false;
            try
            {
                JsonReader reader = new JsonReader(text);
                value = reader.ReadValue(0);
                return value != null;
            }
            catch (Exception)
            {
                // 坏 JSON 属于预期情况（宿主正在写、文件被截断等），静默走离线逻辑。
                value = null;
                return false;
            }
        }

        private sealed class JsonReader
        {
            private const int MaxDepth = 64;
            private readonly string _s;
            private int _i;

            public JsonReader(string s)
            {
                _s = s;
                _i = 0;
                // 跳过 UTF-8 BOM（File.ReadAllText 一般已去掉，这里兜一层）
                if (_s.Length > 0 && _s[0] == '\uFEFF')
                    _i = 1;
            }

            private void SkipWhitespace()
            {
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                        _i++;
                    else
                        break;
                }
            }

            public JsonValue ReadValue(int depth)
            {
                if (depth > MaxDepth)
                    throw new FormatException("json 嵌套过深");
                SkipWhitespace();
                if (_i >= _s.Length)
                    throw new FormatException("json 提前结束");
                char c = _s[_i];
                switch (c)
                {
                    case '{':
                        return ReadObject(depth);
                    case '[':
                        return ReadArray(depth);
                    case '"':
                        return JsonValue.MakeString(ReadString());
                    case 't':
                        Expect("true");
                        return JsonValue.MakeBool(true);
                    case 'f':
                        Expect("false");
                        return JsonValue.MakeBool(false);
                    case 'n':
                        Expect("null");
                        return JsonValue.NullValue;
                    default:
                        return ReadNumber();
                }
            }

            private void Expect(string literal)
            {
                if (string.CompareOrdinal(_s, _i, literal, 0, literal.Length) != 0)
                    throw new FormatException("json 字面量错误");
                _i += literal.Length;
            }

            private JsonValue ReadObject(int depth)
            {
                JsonValue obj = JsonValue.MakeObject();
                _i++; // '{'
                SkipWhitespace();
                if (_i < _s.Length && _s[_i] == '}')
                {
                    _i++;
                    return obj;
                }
                while (true)
                {
                    SkipWhitespace();
                    if (_i >= _s.Length || _s[_i] != '"')
                        throw new FormatException("json 对象键必须是字符串");
                    string key = ReadString();
                    SkipWhitespace();
                    if (_i >= _s.Length || _s[_i] != ':')
                        throw new FormatException("json 对象缺少冒号");
                    _i++;
                    JsonValue val = ReadValue(depth + 1);
                    obj.Fields[key] = val;
                    SkipWhitespace();
                    if (_i < _s.Length && _s[_i] == ',')
                    {
                        _i++;
                        SkipWhitespace();
                        // 容忍尾逗号
                        if (_i < _s.Length && _s[_i] == '}')
                        {
                            _i++;
                            return obj;
                        }
                        continue;
                    }
                    if (_i < _s.Length && _s[_i] == '}')
                    {
                        _i++;
                        return obj;
                    }
                    throw new FormatException("json 对象格式错误");
                }
            }

            private JsonValue ReadArray(int depth)
            {
                JsonValue arr = JsonValue.MakeArray();
                _i++; // '['
                SkipWhitespace();
                if (_i < _s.Length && _s[_i] == ']')
                {
                    _i++;
                    return arr;
                }
                while (true)
                {
                    JsonValue val = ReadValue(depth + 1);
                    arr.Items.Add(val);
                    SkipWhitespace();
                    if (_i < _s.Length && _s[_i] == ',')
                    {
                        _i++;
                        SkipWhitespace();
                        if (_i < _s.Length && _s[_i] == ']')
                        {
                            _i++;
                            return arr;
                        }
                        continue;
                    }
                    if (_i < _s.Length && _s[_i] == ']')
                    {
                        _i++;
                        return arr;
                    }
                    throw new FormatException("json 数组格式错误");
                }
            }

            private string ReadString()
            {
                _i++; // 开引号
                StringBuilder sb = new StringBuilder();
                while (_i < _s.Length)
                {
                    char c = _s[_i++];
                    if (c == '"')
                        return sb.ToString();
                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }
                    if (_i >= _s.Length)
                        break;
                    char esc = _s[_i++];
                    switch (esc)
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
                            if (_i + 4 > _s.Length)
                                throw new FormatException("json unicode 转义不完整");
                            string hex = _s.Substring(_i, 4);
                            _i += 4;
                            int code;
                            if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                                throw new FormatException("json unicode 转义非法");
                            sb.Append((char)code);
                            break;
                        default:
                            // 未知转义原样保留，宁可难看也不要崩
                            sb.Append(esc);
                            break;
                    }
                }
                throw new FormatException("json 字符串未闭合");
            }

            private JsonValue ReadNumber()
            {
                int start = _i;
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E')
                        _i++;
                    else
                        break;
                }
                string raw = _s.Substring(start, _i - start);
                double d;
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                    throw new FormatException("json 数字非法: " + raw);
                return JsonValue.MakeNumber(d);
            }
        }
    }

    /// <summary>生成命令文件用的最小 JSON 文本工具。</summary>
    internal static class JsonText
    {
        public static string Quote(string s)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('"');
            if (s != null)
            {
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\b': sb.Append("\\b"); break;
                        case '\f': sb.Append("\\f"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 0x20)
                                sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            else
                                sb.Append(c);
                            break;
                    }
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
