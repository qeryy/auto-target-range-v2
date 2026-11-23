// 灵枢桌面版（原生）—— 极简 JSON
//
// .NET Framework 4.8 没有 System.Text.Json。这里用两种手段：
//   * 读：System.Web.Extensions 的 JavaScriptSerializer（Windows 自带程序集）；
//   * 写：手写一个逃逸函数（请求体都很小，不需要序列化器）。
//
// 刻意不引 Newtonsoft：那会让 exe 多带一个 DLL，违背「一个文件就能跑」。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Web.Script.Serialization;

namespace SpiritScout.Native
{
    public static class SimpleJson
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer
        {
            MaxJsonLength = 64 * 1024 * 1024,
            RecursionLimit = 128,
        };

        /// <summary>解析成 Dictionary&lt;string,object&gt; / List&lt;object&gt; / 基本类型。</summary>
        public static object Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                return Serializer.DeserializeObject(json);
            }
            catch (Exception ex)
            {
                throw new FormatException("服务端返回的不是合法 JSON：" + ex.Message);
            }
        }

        /// <summary>把对象序列化（只用于请求体）。</summary>
        public static string Stringify(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object v)
        {
            if (v == null) { sb.Append("null"); return; }

            if (v is string) { WriteString(sb, (string)v); return; }
            if (v is bool) { sb.Append(((bool)v) ? "true" : "false"); return; }
            if (v is int || v is long || v is short || v is byte)
            {
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                return;
            }
            if (v is double || v is float || v is decimal)
            {
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                return;
            }

            var dict = v as IDictionary<string, object>;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (var kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, kv.Key);
                    sb.Append(':');
                    Write(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }

            var list = v as System.Collections.IEnumerable;
            if (list != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Write(sb, item);
                }
                sb.Append(']');
                return;
            }

            WriteString(sb, v.ToString());
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
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---------- 便捷读取 ----------

        public static Dictionary<string, object> AsDict(object v)
        {
            return v as Dictionary<string, object>;
        }

        public static List<object> AsList(object v)
        {
            return v as List<object>;
        }

        public static string Str(Dictionary<string, object> d, string key, string fallback = "")
        {
            if (d == null) return fallback;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return fallback;
            return v.ToString();
        }

        public static int Int(Dictionary<string, object> d, string key, int fallback = 0)
        {
            if (d == null) return fallback;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return fallback;
            int n;
            return int.TryParse(v.ToString(), out n) ? n : fallback;
        }
    }
}
