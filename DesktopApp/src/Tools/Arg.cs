// 灵枢桌面版 —— 参数解析辅助
//
// 从 IDictionary<string, object> 里安全地取字符串/整数。
// 模型返回的 JSON 反序列化后就是这种字典，字段可能缺失或类型不对。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpiritScout.Tools
{
    internal static class Arg
    {
        public static string Str(IDictionary<string, object> d, string key, string fallback = "")
        {
            if (d == null) return fallback;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return fallback;
            return v.ToString();
        }

        public static int Int(IDictionary<string, object> d, string key, int fallback)
        {
            if (d == null) return fallback;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return fallback;
            int n;
            if (int.TryParse(v.ToString(), out n)) return n;
            double f;
            if (double.TryParse(v.ToString(), out f)) return (int)f;
            return fallback;
        }

        /// <summary>
        /// 取布尔值。模型可能传 true / "true" / "1" / 1，都要认。
        /// </summary>
        public static bool Bool(IDictionary<string, object> d, string key, bool fallback)
        {
            if (d == null || !d.ContainsKey(key)) return fallback;
            object v = d[key];
            if (v == null) return fallback;
            if (v is bool) return (bool)v;
            string str = v as string;
            if (str != null)
            {
                str = str.Trim();
                if (string.Equals(str, "true", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(str, "false", StringComparison.OrdinalIgnoreCase)) return false;
                if (str == "1") return true;
                if (str == "0") return false;
                return fallback;
            }
            try { return Convert.ToInt32(v) != 0; }
            catch { return fallback; }
        }

        public static Dictionary<string, string> Dict(IDictionary<string, object> d, string key)
        {
            if (d == null) return null;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return null;
            var src = v as IDictionary<string, object>;
            if (src == null) return null;
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in src)
                result[kv.Key] = kv.Value == null ? "" : kv.Value.ToString();
            return result;
        }
    }
}
