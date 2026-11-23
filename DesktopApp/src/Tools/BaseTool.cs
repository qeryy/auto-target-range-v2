// 灵枢桌面版 —— 进制转换
//
// CTF misc 里这类题很多：
//     "72 101 108 108 111"                    → Hello（十进制 ASCII）
//     "0x48 0x65 0x6c 0x6c 0x6f"              → Hello（十六进制）
//     "1101001 1100101 1101100 1101100 1101111" → Hello（二进制，7 位一组）
//     "48 65 6c 6c 6f"                        → Hello（十六进制，无 0x 前缀）
//
// `codec` 的 `binary` 动作只认「8 位一组」的 0/1，`hex` 只认连续的十六进制串，
// **处理不了「一堆十进制数字」和「零散的三位八进制」**。
//
// 这个工具专门管这件事。
//
// ## 四个动作
//
//   ascii    —— 一串数字 → 文本。**自动判断是十/十六/八/二进制**
//   tobase   —— 把整数或字符串转成指定进制
//   frombase —— 从指定进制转回
//   number   —— 同一个数的多种进制表示（方便核对）
//
// ## ascii 的自动判断
//
// 按顺序试：八进制（3 位一组）→ 十六进制（2 位一组）→ 十进制 → 二进制。
// 每种都试出结果，**哪个结果最像文本就选哪个**，但同时把所有候选列出来 ——
// 因为「像不像文本」的判断在短输入上不可靠，让调用方自己看更稳。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpiritScout.Tools
{
    public sealed class BaseTool : ITool
    {
        public string Name { get { return "base"; } }

        public string Description
        {
            get
            {
                return "进制转换。actions: ascii（数字串→文本，自动判断十/十六/八/二进制）| "
                     + "tobase（整数或字符串转成指定进制）| frombase | number（同一数的多种进制）。"
                     + "适合「72 101 108 108 111 → Hello」这类题。";
            }
        }

        public string ParametersJson
        {
            get
            {
                return @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": { ""type"": ""string"", ""enum"": [""ascii"", ""tobase"", ""frombase"", ""number""], ""description"": ""做什么"" },
    ""text"": { ""type"": ""string"", ""description"": ""输入"" },
    ""from"": { ""type"": ""integer"", ""description"": ""源进制（2-36），ascii 时可省略让它自动判断"" },
    ""to"": { ""type"": ""integer"", ""description"": ""目标进制（2-36），默认 10"" },
    ""width"": { ""type"": ""integer"", ""description"": ""tobase 时补零到几位，默认不补"" }
  },
  ""required"": [""action"", ""text""]
}";
            }
        }

        public Task<ToolResult> InvokeAsync(IDictionary<string, object> args, CancellationToken ct)
        {
            string action = (Arg.Str(args, "action") ?? "").Trim().ToLowerInvariant();
            string text = Arg.Str(args, "text") ?? "";
            int from = Arg.Int(args, "from", 0);
            int to = Arg.Int(args, "to", 10);
            int width = Arg.Int(args, "width", 0);

            if (text.Length == 0) return Task.FromResult(ToolResult.Fail("text 不能为空"));

            try
            {
                switch (action)
                {
                    case "ascii":    return Task.FromResult(DoAscii(text));
                    case "tobase":   return Task.FromResult(DoToBase(text, from, to, width));
                    case "frombase": return Task.FromResult(DoFromBase(text, from, to));
                    case "number":   return Task.FromResult(DoNumber(text, from));
                    default:
                        return Task.FromResult(ToolResult.Fail("action 只能是 ascii / tobase / frombase / number"));
                }
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail("处理失败：" + ex.Message));
            }
        }

        // ---------- ascii ----------

        private static ToolResult DoAscii(string text)
        {
            // 先把输入拆成 token：按空格/逗号/换行分，也支持 "0x48" 和连续十六进制串
            var tokens = SplitTokens(text);
            if (tokens.Count == 0) return ToolResult.Fail("没解析出任何数字");

            var sb = new StringBuilder();
            sb.Append("解析出 ").Append(tokens.Count).Append(" 个数字：\n\n");

            // 四种候选，全都试一遍
            var candidates = new List<KeyValuePair<string, string>>();

            string dec = TryDecode(tokens, 10);
            if (dec != null) candidates.Add(new KeyValuePair<string, string>("十进制（每 token 一个字节）", dec));

            string oct = TryDecode(tokens, 8);
            if (oct != null) candidates.Add(new KeyValuePair<string, string>("八进制（每 token 一个字节）", oct));

            // 十六进制：token 里本身带 0x，或者整体是连续十六进制串
            string hexFromTokens = TryDecode(tokens, 16);
            if (hexFromTokens != null && hexFromTokens != dec)
                candidates.Add(new KeyValuePair<string, string>("十六进制（每 token 一个字节）", hexFromTokens));

            string hexPacked = TryHexPacked(text);
            if (hexPacked != null) candidates.Add(new KeyValuePair<string, string>("十六进制（连续串，两位一字节）", hexPacked));

            string bin = TryBinaryPacked(text);
            if (bin != null) candidates.Add(new KeyValuePair<string, string>("二进制（8 位一组或整体连续）", bin));

            if (candidates.Count == 0)
            {
                sb.Append("四种解析方式都失败——数字可能超出字节范围（0-255）。\n");
                sb.Append("看看最大值：");
                int max = 0;
                foreach (var t in tokens) { int v; if (int.TryParse(t, out v) && v > max) max = v; }
                sb.Append(max).Append("\n\n");
                if (max > 255) sb.Append("超过 255，说明不是「每个数字一个字节」。可能是：\n")
                                 .Append("  · 一个数整体转 ASCII（用 number 动作看它的多种进制）\n")
                                 .Append("  · 需要先做别的变换\n");
                return new ToolResult { Ok = true, Output = sb.ToString(), Summary = "ascii · 解析失败（最大值 " + max + "）" };
            }

            // 按「像不像文本」排序
            candidates.Sort((a, b) => Score(b.Value).CompareTo(Score(a.Value)));

            int n = 0;
            foreach (var kv in candidates)
            {
                sb.Append("[").Append(n == 0 ? "最可能" : (n + 1).ToString()).Append("] ")
                  .Append(kv.Key).Append("：\n    ").Append(Render(kv.Value)).Append("\n\n");
                n++;
            }
            sb.Append("（多个候选时看哪个像明文；短输入的可读性判断不一定准）");

            return new ToolResult
            {
                Ok = true,
                Output = sb.ToString(),
                Summary = "ascii · " + candidates.Count + " 种解析，最可能：" + Clip(candidates[0].Value, 50),
            };
        }

        /// <summary>把输入拆成一堆数字 token。</summary>
        private static List<string> SplitTokens(string text)
        {
            var list = new List<string>();
            var cur = new StringBuilder();
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c) || c == ',' || c == ';' || c == '|')
                {
                    if (cur.Length > 0) { list.Add(cur.ToString()); cur.Length = 0; }
                }
                else cur.Append(c);
            }
            if (cur.Length > 0) list.Add(cur.ToString());
            return list;
        }

        /// <summary>把每个 token 当一个字节，按指定进制解析。</summary>
        private static string TryDecode(List<string> tokens, int radix)
        {
            var bytes = new List<byte>();
            foreach (var raw in tokens)
            {
                string t = raw.Trim();
                if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t.Substring(2);
                if (t.StartsWith("0o", StringComparison.OrdinalIgnoreCase)) t = t.Substring(2);
                if (t.StartsWith("0b", StringComparison.OrdinalIgnoreCase)) t = t.Substring(2);
                int v;
                try { v = Convert.ToInt32(t, radix); }
                catch { return null; }
                if (v < 0 || v > 255) return null;
                bytes.Add((byte)v);
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        /// <summary>把整个输入当连续的十六进制串，两位一字节。</summary>
        private static string TryHexPacked(string text)
        {
            string t = text.Replace(" ", "").Replace(",", "").Replace("\r", "").Replace("\n", "")
                           .Replace("0x", "").Replace("0X", "");
            if (t.Length < 2 || t.Length % 2 != 0) return null;
            var bytes = new byte[t.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                try { bytes[i] = Convert.ToByte(t.Substring(i * 2, 2), 16); }
                catch { return null; }
            }
            return Encoding.UTF8.GetString(bytes);
        }

        /// <summary>把整个输入当二进制串。8 位一组，或者 7 位一组（老 ASCII）。</summary>
        private static string TryBinaryPacked(string text)
        {
            string t = text.Replace(" ", "").Replace(",", "").Replace("\r", "").Replace("\n", "");
            if (t.Length == 0) return null;
            foreach (char c in t) if (c != '0' && c != '1') return null;

            // 优先 8 位
            if (t.Length % 8 == 0)
            {
                var bytes = new byte[t.Length / 8];
                for (int i = 0; i < bytes.Length; i++)
                    bytes[i] = Convert.ToByte(t.Substring(i * 8, 8), 2);
                return Encoding.UTF8.GetString(bytes);
            }
            // 再试 7 位（老式 ASCII，都是 < 128）
            if (t.Length % 7 == 0)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < t.Length; i += 7)
                {
                    int v = Convert.ToInt32(t.Substring(i, 7), 2);
                    sb.Append((char)v);
                }
                return sb.ToString();
            }
            return null;
        }

        // ---------- tobase / frombase ----------

        private static ToolResult DoToBase(string text, int from, int to, int width)
        {
            if (to < 2 || to > 36) return ToolResult.Fail("目标进制要在 2-36 之间");

            // 输入是数字就按数字转；否则按字符串转（每字节一个数）
            if (from >= 2)
            {
                long v;
                if (!TryParseLong(text.Trim(), from, out v))
                    return ToolResult.Fail("「" + text + "」不是合法的 " + from + " 进制整数");
                string r = ToBaseString(v, to);
                if (width > 0) r = r.PadLeft(width, '0');
                return new ToolResult { Ok = true, Output = r, Summary = from + " 进制 → " + to + " 进制：" + r };
            }

            // 没给源进制：当成字符串，逐字节转
            var sb = new StringBuilder();
            int n = 0;
            foreach (byte b in Encoding.UTF8.GetBytes(text))
            {
                string r = ToBaseString(b, to);
                if (width > 0) r = r.PadLeft(width, '0');
                if (n > 0) sb.Append(' ');
                sb.Append(r);
                n++;
            }
            return new ToolResult
            {
                Ok = true,
                Output = sb.ToString(),
                Summary = "字符串 → " + to + " 进制（" + n + " 字节）",
            };
        }

        private static ToolResult DoFromBase(string text, int from, int to)
        {
            if (from < 2 || from > 36) return ToolResult.Fail("源进制要在 2-36 之间");
            if (to < 2 || to > 36) return ToolResult.Fail("目标进制要在 2-36 之间");

            long v;
            if (!TryParseLong(text.Trim(), from, out v))
                return ToolResult.Fail("「" + text + "」不是合法的 " + from + " 进制整数");

            string r = ToBaseString(v, to);
            return new ToolResult { Ok = true, Output = r, Summary = "进制转换：" + r };
        }

        private static ToolResult DoNumber(string text, int from)
        {
            long v;
            if (!TryParseLong(text.Trim(), from >= 2 ? from : 10, out v))
                return ToolResult.Fail("「" + text + "」解析不成整数"
                    + (from >= 2 ? "（按 " + from + " 进制）" : "（按十进制；如果是其他进制请给 from）"));

            var sb = new StringBuilder();
            sb.Append("数值 ").Append(v).Append("\n\n");
            sb.Append("  二进制  ").Append(ToBaseString(v, 2)).Append('\n');
            sb.Append("  八进制  ").Append(ToBaseString(v, 8)).Append('\n');
            sb.Append("  十进制  ").Append(v).Append('\n');
            sb.Append("  十六进制 ").Append(ToBaseString(v, 16)).Append('\n');
            sb.Append("  三十二进制 ").Append(ToBaseString(v, 32)).Append("\n\n");

            // 如果是可打印 ASCII 就给出来
            if (v >= 0x20 && v < 0x7F) sb.Append("  ASCII 字符：'").Append((char)v).Append("'\n");
            else if (v >= 0 && v < 0x20) sb.Append("  ASCII 控制字符（0x").Append(v.ToString("x2")).Append("）\n");

            return new ToolResult
            {
                Ok = true,
                Output = sb.ToString(),
                Summary = "number · 0x" + ToBaseString(v, 16) + " = " + v,
            };
        }

        // ---------- 工具 ----------

        private static bool TryParseLong(string s, int radix, out long value)
        {
            value = 0;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();
            bool neg = false;
            if (s.StartsWith("-")) { neg = true; s = s.Substring(1); }
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { s = s.Substring(2); radix = 16; }
            if (s.StartsWith("0b", StringComparison.OrdinalIgnoreCase)) { s = s.Substring(2); radix = 2; }
            if (s.StartsWith("0o", StringComparison.OrdinalIgnoreCase)) { s = s.Substring(2); radix = 8; }
            if (s.Length == 0) return false;

            long acc = 0;
            foreach (char c in s)
            {
                int d;
                if (c >= '0' && c <= '9') d = c - '0';
                else if (c >= 'a' && c <= 'z') d = c - 'a' + 10;
                else if (c >= 'A' && c <= 'Z') d = c - 'A' + 10;
                else return false;
                if (d >= radix) return false;
                try { acc = checked(acc * radix + d); }
                catch { return false; }
            }
            value = neg ? -acc : acc;
            return true;
        }

        private const string Digits36 = "0123456789abcdefghijklmnopqrstuvwxyz";

        private static string ToBaseString(long v, int radix)
        {
            if (v == 0) return "0";
            bool neg = v < 0;
            if (neg) v = -v;
            var sb = new StringBuilder();
            while (v > 0)
            {
                sb.Insert(0, Digits36[(int)(v % radix)]);
                v /= radix;
            }
            return neg ? "-" + sb : sb.ToString();
        }

        // ---------- 可读性 ----------

        private static double Score(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int printable = 0, letters = 0, spaces = 0;
            foreach (char c in s)
            {
                if (c >= 0x20 && c < 0x7F) { printable++; if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) letters++; }
                else if (c == ' ') { printable++; spaces++; }
                else if (c >= 0x4E00 && c <= 0x9FFF) printable++;   // 中文算可打印
            }
            return printable * 100.0 / s.Length + letters * 2 + spaces * 3;
        }

        private static string Render(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c >= 0x20 && c != 0x7F) sb.Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else sb.Append("·");
            }
            return sb.ToString();
        }

        private static string Clip(string s, int n)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }
    }
}
