// 灵枢桌面版 —— JSON 读取工具
//
// .NET Framework 4.8 自带 JavaScriptSerializer，但它的两个行为容易踩坑：
//   1. JSON 数组反序列化成 object[]，不是 List<object>
//   2. 数字可能给 int 也可能给 decimal/long
//
// 这里把「取数组 / 取字典 / 取字符串 / 取数字」收成一组方法，
// 各页面直接用，不用每处都写 `as IList` 再判空。

using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace SpiritScout.Core
{
    public static class Json
    {
        private static readonly JavaScriptSerializer Ser = new JavaScriptSerializer
        {
            MaxJsonLength = 256 * 1024 * 1024,
            RecursionLimit = 256,
        };

        public static object Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return Ser.DeserializeObject(json); }
            catch { return null; }
        }

        public static Dictionary<string, object> AsDict(object v)
        {
            return v as Dictionary<string, object>;
        }

        /// <summary>
        /// 取数组字段。兼容 object[] 与 List&lt;object&gt;。
        /// （JavaScriptSerializer 给的是 object[]，只判 IList 会永远 null）
        /// </summary>
        public static List<object> AsList(object v)
        {
            if (v == null || v is string) return null;
            var list = v as List<object>;
            if (list != null) return list;

            var en = v as System.Collections.IEnumerable;
            if (en == null) return null;
            var result = new List<object>();
            foreach (var item in en) result.Add(item);
            return result;
        }

        public static List<object> Array(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            object v;
            return d.TryGetValue(key, out v) ? AsList(v) : null;
        }

        public static Dictionary<string, object> Dict(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            object v;
            return d.TryGetValue(key, out v) ? AsDict(v) : null;
        }

        public static string Str(Dictionary<string, object> d, string key, string fallback = "")
        {
            if (d == null) return fallback;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return fallback;
            return v.ToString();
        }

        public static long Long(Dictionary<string, object> d, string key, long fallback = 0)
        {
            if (d == null) return fallback;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return fallback;
            long n;
            if (long.TryParse(v.ToString(), out n)) return n;
            double f;
            if (double.TryParse(v.ToString(), out f)) return (long)f;
            return fallback;
        }

        public static int Int(Dictionary<string, object> d, string key, int fallback = 0)
        {
            return (int)Long(d, key, fallback);
        }

        /// <summary>截断字符串用于日志（保留头尾，中间省略）。</summary>
        public static string Clip(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            if (s.Length <= n) return s;
            int head = n * 2 / 3;
            int tail = n - head;
            return s.Substring(0, head) + "..." + s.Substring(s.Length - tail);
        }

        public static bool Bool(Dictionary<string, object> d, string key, bool fallback = false)
        {
            if (d == null) return fallback;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return fallback;
            bool b;
            if (bool.TryParse(v.ToString(), out b)) return b;
            return v.ToString() == "1" || v.ToString().ToLowerInvariant() == "true";
        }
    }
}
