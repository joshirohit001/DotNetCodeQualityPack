using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DotNetCodeQualityPack.Ai
{
    /// <summary>
    /// Small, tolerant JSON reader/writer (no package dependency, works on .NET Framework inside Visual Studio).
    /// Objects become Dictionary&lt;string, object&gt;, arrays List&lt;object&gt;, numbers double.
    /// </summary>
    internal static class MiniJson
    {
        public static object Parse(string json)
        {
            if (json == null)
            {
                throw new FormatException("No JSON text.");
            }

            var parser = new Parser(json);
            parser.SkipWhitespace();
            object value = parser.ParseValue();
            parser.SkipWhitespace();
            if (!parser.AtEnd)
            {
                throw new FormatException("Unexpected text after the JSON value at position " + parser.Position + ".");
            }

            return value;
        }

        public static bool TryParse(string json, out object value)
        {
            try
            {
                value = Parse(json);
                return true;
            }
            catch (FormatException)
            {
                value = null;
                return false;
            }
        }

        public static string Serialize(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value);
            return sb.ToString();
        }

        // ------------------------------------------------------------------ helpers for reading

        public static object Get(object node, string key)
        {
            return node is Dictionary<string, object> dict && dict.TryGetValue(key, out object value) ? value : null;
        }

        public static string GetString(object node, string key)
        {
            return AsString(Get(node, key));
        }

        public static string AsString(object value)
        {
            switch (value)
            {
                case null: return null;
                case string s: return s;
                case double d: return d.ToString(CultureInfo.InvariantCulture);
                case bool b: return b ? "true" : "false";
                default: return value.ToString();
            }
        }

        public static int? AsInt(object value)
        {
            switch (value)
            {
                case double d:
                    return d >= int.MinValue && d <= int.MaxValue ? (int)d : (int?)null;
                case string s:
                    var digits = new StringBuilder();
                    foreach (char c in s)
                    {
                        if (char.IsDigit(c))
                        {
                            digits.Append(c);
                        }
                        else if (digits.Length > 0)
                        {
                            break;
                        }
                    }

                    return int.TryParse(digits.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : (int?)null;
                default:
                    return null;
            }
        }

        // ------------------------------------------------------------------ writer

        private static void Write(StringBuilder sb, object value)
        {
            switch (value)
            {
                case null:
                    sb.Append("null");
                    return;
                case string s:
                    WriteString(sb, s);
                    return;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    return;
                case double d:
                    sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                    return;
                case float f:
                    sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
                    return;
                case int _:
                case long _:
                case short _:
                case decimal _:
                    sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                    return;
                case IDictionary<string, object> dict:
                    sb.Append('{');
                    bool first = true;
                    foreach (KeyValuePair<string, object> pair in dict)
                    {
                        if (!first)
                        {
                            sb.Append(',');
                        }

                        first = false;
                        WriteString(sb, pair.Key);
                        sb.Append(':');
                        Write(sb, pair.Value);
                    }

                    sb.Append('}');
                    return;
                case IDictionary<string, string> stringDict:
                    var converted = new Dictionary<string, object>();
                    foreach (KeyValuePair<string, string> pair in stringDict)
                    {
                        converted[pair.Key] = pair.Value;
                    }

                    Write(sb, converted);
                    return;
                case IEnumerable list:
                    sb.Append('[');
                    bool firstItem = true;
                    foreach (object item in list)
                    {
                        if (!firstItem)
                        {
                            sb.Append(',');
                        }

                        firstItem = false;
                        Write(sb, item);
                    }

                    sb.Append(']');
                    return;
                default:
                    WriteString(sb, value.ToString());
                    return;
            }
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            sb.Append('"');
        }

        // ------------------------------------------------------------------ parser

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;

            public Parser(string s)
            {
                _s = s;
            }

            public bool AtEnd => _i >= _s.Length;

            public int Position => _i;

            public void SkipWhitespace()
            {
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i]))
                {
                    _i++;
                }
            }

            public object ParseValue()
            {
                SkipWhitespace();
                if (AtEnd)
                {
                    throw new FormatException("Unexpected end of JSON.");
                }

                char c = _s[_i];
                switch (c)
                {
                    case '{': return ParseObject();
                    case '[': return ParseArray();
                    case '"': return ParseString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default:
                        if (c == '-' || char.IsDigit(c))
                        {
                            return ParseNumber();
                        }

                        throw new FormatException("Unexpected character '" + c + "' at position " + _i + ".");
                }
            }

            private Dictionary<string, object> ParseObject()
            {
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                _i++; // {
                SkipWhitespace();
                if (Peek() == '}')
                {
                    _i++;
                    return result;
                }

                while (true)
                {
                    SkipWhitespace();
                    if (Peek() != '"')
                    {
                        throw new FormatException("Expected a property name at position " + _i + ".");
                    }

                    string key = ParseString();
                    SkipWhitespace();
                    if (Peek() != ':')
                    {
                        throw new FormatException("Expected ':' at position " + _i + ".");
                    }

                    _i++;
                    result[key] = ParseValue();
                    SkipWhitespace();

                    char next = Peek();
                    _i++;
                    if (next == '}')
                    {
                        return result;
                    }

                    if (next != ',')
                    {
                        throw new FormatException("Expected ',' or '}' at position " + (_i - 1) + ".");
                    }
                }
            }

            private List<object> ParseArray()
            {
                var result = new List<object>();
                _i++; // [
                SkipWhitespace();
                if (Peek() == ']')
                {
                    _i++;
                    return result;
                }

                while (true)
                {
                    result.Add(ParseValue());
                    SkipWhitespace();

                    char next = Peek();
                    _i++;
                    if (next == ']')
                    {
                        return result;
                    }

                    if (next != ',')
                    {
                        throw new FormatException("Expected ',' or ']' at position " + (_i - 1) + ".");
                    }
                }
            }

            private string ParseString()
            {
                var sb = new StringBuilder();
                _i++; // opening quote

                while (true)
                {
                    if (AtEnd)
                    {
                        throw new FormatException("Unterminated string.");
                    }

                    char c = _s[_i++];
                    if (c == '"')
                    {
                        return sb.ToString();
                    }

                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }

                    if (AtEnd)
                    {
                        throw new FormatException("Unterminated escape.");
                    }

                    char e = _s[_i++];
                    switch (e)
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
                            {
                                throw new FormatException("Bad \\u escape.");
                            }

                            sb.Append((char)int.Parse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            _i += 4;
                            break;
                        default:
                            sb.Append(e); // Be lenient with unknown escapes from language models.
                            break;
                    }
                }
            }

            private double ParseNumber()
            {
                int start = _i;
                while (_i < _s.Length && "+-0123456789.eE".IndexOf(_s[_i]) >= 0)
                {
                    _i++;
                }

                string token = _s.Substring(start, _i - start);
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                {
                    throw new FormatException("Bad number '" + token + "'.");
                }

                return value;
            }

            private void Expect(string word)
            {
                if (string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0)
                {
                    throw new FormatException("Expected '" + word + "' at position " + _i + ".");
                }

                _i += word.Length;
            }

            private char Peek()
            {
                return _i < _s.Length ? _s[_i] : '\0';
            }
        }
    }
}
