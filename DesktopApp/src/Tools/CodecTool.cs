// 灵枢桌面版 —— 编码解码工具
//
// CTF 里「这一坨是什么编码」出现的频率极高，几乎每道 misc/crypto 的第一句都是它。
//
// 之前的做法是让模型 `shell` 里调 Python：
//     python -c "import base64; print(base64.b64decode('...'))"
//
// 问题：
//   · 目标机器不一定装了 Python（Windows 上尤其）
//   · 每条命令一个新进程，来回几十次很慢
//   · 引号转义经常搞错（实测模型在这上面翻过车）
//
// 所以做成原生实现，一次调用就能「识别 + 解码」。
//
// ## 支持的编码
//
//   base64 / base64url / base32 / hex / url / html / rot13 / morse /
//   binary（010101）/ unicode 转义（\uXXXX）/ 反转
//
// ## 设计取舍
//
// `auto` 动作不走「猜」——它是**按顺序试**每个解码器，**能成功且有意义的**才返回。
// 「有意义」的判断是：解出来大部分是可打印字符。否则 `AAAA` 用 base64 解是
// 三个 `\0`，也「成功」了，但毫无意义。
//
// 不做评分排序（哪种编码更像答案），因为那需要人判断。这里只给出**候选**。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SpiritScout.Tools
{
    public sealed class CodecTool : ITool
    {
        public string Name { get { return "codec"; } }

        public string Description
        {
            get
            {
                return "编码与解码：base64 / base64url / base32 / hex / url / html / rot13 / morse / "
                     + "binary / unicode 转义 / 反转。"
                     + "actions: encode | decode | auto（按顺序试所有解码器）| identify（只判断是什么）。"
                     + "不需要 Python，原生实现。";
            }
        }

        public string ParametersJson
        {
            get
            {
                return @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": { ""type"": ""string"", ""enum"": [""encode"", ""decode"", ""auto"", ""identify""], ""description"": ""做什么"" },
    ""scheme"": { ""type"": ""string"", ""enum"": [""base64"", ""base64url"", ""base32"", ""hex"", ""url"", ""html"", ""rot13"", ""morse"", ""binary"", ""unicode"", ""reverse""], ""description"": ""encode/decode 时用哪种"" },
    ""text"": { ""type"": ""string"", ""description"": ""输入内容"" },
    ""times"": { ""type"": ""integer"", ""description"": ""重复几次（多层编码时用），默认 1，最多 10"" }
  },
  ""required"": [""action"", ""text""]
}";
            }
        }

        public Task<ToolResult> InvokeAsync(IDictionary<string, object> args, CancellationToken ct)
        {
            string action = (Arg.Str(args, "action") ?? "").Trim().ToLowerInvariant();
            string text = Arg.Str(args, "text") ?? "";
            string scheme = (Arg.Str(args, "scheme") ?? "").Trim().ToLowerInvariant();
            int times = Arg.Int(args, "times", 1);
            if (times < 1) times = 1;
            if (times > 10) times = 10;

            if (text.Length == 0) return Task.FromResult(ToolResult.Fail("text 不能为空"));

            try
            {
                if (action == "encode") return Task.FromResult(DoEncode(text, scheme, times));
                if (action == "decode") return Task.FromResult(DoDecode(text, scheme, times));
                if (action == "auto") return Task.FromResult(DoAuto(text));
                if (action == "identify") return Task.FromResult(DoIdentify(text));
                return Task.FromResult(ToolResult.Fail("action 只能是 encode / decode / auto / identify"));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail("处理失败：" + ex.Message));
            }
        }

        // ---------- encode / decode ----------

        private static ToolResult DoEncode(string text, string scheme, int times)
        {
            string cur = text;
            for (int i = 0; i < times; i++)
            {
                string next = EncodeOne(cur, scheme);
                if (next == null) return ToolResult.Fail("不支持的编码：" + scheme);
                cur = next;
            }
            return new ToolResult
            {
                Ok = true,
                Output = cur,
                Summary = "encode " + scheme + (times > 1 ? " ×" + times : "") + " · " + text.Length + " → " + cur.Length + " 字符",
            };
        }

        private static ToolResult DoDecode(string text, string scheme, int times)
        {
            string cur = text.Trim();
            var steps = new List<string>();
            for (int i = 0; i < times; i++)
            {
                string note;
                string next = DecodeOne(cur, scheme, out note);
                if (next == null) return ToolResult.Fail("解码失败（" + scheme + "）：" + (note ?? "格式不对"));
                steps.Add(next);
                cur = next;
            }
            return new ToolResult
            {
                Ok = true,
                Output = cur,
                Summary = "decode " + scheme + (times > 1 ? " ×" + times : "") + " · " + text.Length + " → " + cur.Length + " 字符",
            };
        }

        private static string EncodeOne(string s, string scheme)
        {
            switch (scheme)
            {
                case "base64":
                    return Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
                case "base64url":
                    return Convert.ToBase64String(Encoding.UTF8.GetBytes(s))
                        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                case "base32":
                    return ToBase32(Encoding.UTF8.GetBytes(s));
                case "hex":
                    {
                        var sb = new StringBuilder(s.Length * 2);
                        foreach (byte b in Encoding.UTF8.GetBytes(s)) sb.Append(b.ToString("x2"));
                        return sb.ToString();
                    }
                case "url":
                    return Uri.EscapeDataString(s);
                case "html":
                    {
                        var sb = new StringBuilder(s.Length);
                        foreach (char c in s)
                        {
                            if (c == '&') sb.Append("&amp;");
                            else if (c == '<') sb.Append("&lt;");
                            else if (c == '>') sb.Append("&gt;");
                            else if (c == '"') sb.Append("&quot;");
                            else if (c > 127) sb.Append("&#").Append((int)c).Append(';');
                            else sb.Append(c);
                        }
                        return sb.ToString();
                    }
                case "rot13":
                    return Rot13(s);
                case "morse":
                    return ToMorse(s);
                case "binary":
                    {
                        var sb = new StringBuilder();
                        foreach (byte b in Encoding.UTF8.GetBytes(s))
                        {
                            for (int i = 7; i >= 0; i--) sb.Append(((b >> i) & 1) == 1 ? '1' : '0');
                            sb.Append(' ');
                        }
                        return sb.ToString().TrimEnd();
                    }
                case "unicode":
                    {
                        var sb = new StringBuilder();
                        foreach (char c in s)
                        {
                            if (c > 127 || c == '\\') sb.Append("\\u").Append(((int)c).ToString("x4"));
                            else sb.Append(c);
                        }
                        return sb.ToString();
                    }
                case "reverse":
                    {
                        var arr = s.ToCharArray();
                        Array.Reverse(arr);
                        return new string(arr);
                    }
                default:
                    return null;
            }
        }

        private static string DecodeOne(string s, string scheme, out string note)
        {
            note = null;
            try
            {
                switch (scheme)
                {
                    case "base64":
                        {
                            string t = s.Replace("\r", "").Replace("\n", "").Replace(" ", "");
                            // 补齐 padding
                            int pad = t.Length % 4;
                            if (pad == 2) t += "==";
                            else if (pad == 3) t += "=";
                            else if (pad == 1) { note = "长度不对（%4==1）"; return null; }
                            return Encoding.UTF8.GetString(Convert.FromBase64String(t));
                        }
                    case "base64url":
                        {
                            string t = s.Replace("-", "+").Replace("_", "/").Replace("\r", "").Replace("\n", "").Replace(" ", "");
                            int pad = t.Length % 4;
                            if (pad == 2) t += "==";
                            else if (pad == 3) t += "=";
                            return Encoding.UTF8.GetString(Convert.FromBase64String(t));
                        }
                    case "base32":
                        return FromBase32(s);
                    case "hex":
                        {
                            string t = s.Replace(" ", "").Replace("0x", "").Replace("0X", "")
                                        .Replace("\r", "").Replace("\n", "").Replace(":", "");
                            if (t.Length % 2 != 0) { note = "十六进制长度是奇数"; return null; }
                            var bytes = new byte[t.Length / 2];
                            for (int i = 0; i < bytes.Length; i++)
                                bytes[i] = byte.Parse(t.Substring(i * 2, 2), NumberStyles.HexNumber);
                            return Encoding.UTF8.GetString(bytes);
                        }
                    case "url":
                        return Uri.UnescapeDataString(s.Replace("+", " "));
                    case "html":
                        return System.Net.WebUtility.HtmlDecode(s);
                    case "rot13":
                        return Rot13(s);
                    case "morse":
                        return FromMorse(s, out note);
                    case "binary":
                        {
                            string t = Regex.Replace(s, @"[^01]", "");
                            if (t.Length == 0) { note = "没有 0/1"; return null; }
                            if (t.Length % 8 != 0) { note = "二进制长度不是 8 的倍数"; return null; }
                            var bytes = new byte[t.Length / 8];
                            for (int i = 0; i < bytes.Length; i++)
                                bytes[i] = Convert.ToByte(t.Substring(i * 8, 8), 2);
                            return Encoding.UTF8.GetString(bytes);
                        }
                    case "unicode":
                        return Regex.Unescape(s);
                    case "reverse":
                        {
                            var arr = s.ToCharArray();
                            Array.Reverse(arr);
                            return new string(arr);
                        }
                    default:
                        note = "不支持的 scheme：" + scheme;
                        return null;
                }
            }
            catch (Exception ex)
            {
                note = ex.Message;
                return null;
            }
        }

        // ---------- auto ----------

        /// <summary>按顺序试，能解出「大部分可打印」的才算数。</summary>
        private static ToolResult DoAuto(string text)
        {
            // 分两组：
            //   real   —— 真正的编码，解码后如果可读就是强信号
            //   lossless —— rot13 / reverse 这类**无损变换**，对任何字符串都"成功"，
            //               base64 字符串 rot13 一下也能得到一堆可读字符。
            //               所以它们只在 real 组全军覆没时才试。
            var real = new[]
            {
                "base64", "base64url", "hex", "url", "html", "unicode",
                "binary", "base32", "morse",
            };
            var lossless = new[] { "rot13", "reverse" };

            var hits = new List<string>();
            int tried = 0;

            foreach (var scheme in real)
            {
                tried++;
                string note;
                string r = DecodeOne(text.Trim(), scheme, out note);
                if (r == null) continue;
                if (r == text) continue;                      // 解出来和输入一样
                if (!LooksMeaningful(r)) continue;
                hits.Add(scheme + " → " + r);
            }

            if (hits.Count == 0)
            {
                foreach (var scheme in lossless)
                {
                    tried++;
                    string note;
                    string r = DecodeOne(text.Trim(), scheme, out note);
                    if (r == null || r == text) continue;
                    if (!LooksMeaningful(r)) continue;
                    hits.Add(scheme + " → " + r + "（无损变换，任何输入都能「成功」，只作参考）");
                }
            }

            var sb = new StringBuilder();
            sb.Append("尝试了 ").Append(tried).Append(" 种解码器，").Append(hits.Count).Append(" 种解出有意义的内容\n\n");
            if (hits.Count == 0)
            {
                sb.Append("都没有解出可读内容。可能是：\n");
                sb.Append("  · 加密（不是编码），需要密钥\n");
                sb.Append("  · 自定义编码，得先看题目提示\n");
                sb.Append("  · 多层编码，先用 identify 看结构");
            }
            else
            {
                for (int i = 0; i < hits.Count; i++)
                    sb.Append(i + 1).Append(". ").Append(hits[i]).Append('\n');
                sb.Append("\n（多个命中时看哪个像明文；也可能是多层，把结果再解一次）");
            }

            return new ToolResult
            {
                Ok = true,
                Output = sb.ToString(),
                Summary = "auto 解码 · " + hits.Count + " 种命中" + (hits.Count > 0 ? "：" + hits[0].Split(' ')[0] : ""),
            };
        }

        /// <summary>解出来的东西像不像人写的。</summary>
        private static bool LooksMeaningful(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            int printable = 0, total = 0, replacements = 0;
            foreach (char c in s)
            {
                total++;
                if (c >= 0x20 && c != 0x7F) printable++;      // 可打印（含中文）
                else if (c == '\n' || c == '\r' || c == '\t') printable++;
                // U+FFFD = REPLACEMENT CHARACTER。
                // Encoding.UTF8.GetString 遇到非法字节**不抛异常**，而是塞一个 U+FFFD 进去。
                // 我一开始以为会变成 '?'（0x3F），所以去数问号 —— 数出来永远是 0。
                // 实际测：`666c6167...` 用 base64 解出 10 个字符，其中 8 个是 U+FFFD。
                if (c == '\uFFFD') replacements++;
            }
            if (total == 0) return false;
            double ratio = printable * 1.0 / total;
            // 至少要 85% 可打印
            if (ratio < 0.85) return false;

            // 替换符占比高 = 这不是文本，是被错误解码的二进制。
            if (replacements * 1.0 / total > 0.10) return false;

            // 全是同一个字（比如 "aaaa"）也不算有意义
            var set = new HashSet<char>(s);
            return set.Count >= 2 || s.Length <= 3;
        }

        // ---------- identify ----------

        private static ToolResult DoIdentify(string text)
        {
            string t = text.Trim();
            // 按确定性从高到低列。越具体的特征排越前，泛化的字符集匹配排后面。
            var guesses = new List<string>();

            // 先判最具体的（有明确标志的），最后判「纯字符集匹配」的。
            if (t.IndexOf('%') >= 0 && Regex.IsMatch(t, @"%[0-9a-fA-F]{2}"))
                guesses.Add("URL 编码（含 %XX，确定）");
            if (t.IndexOf('&') >= 0 && Regex.IsMatch(t, @"&(#\d+|#x[0-9a-fA-F]+|\w+);"))
                guesses.Add("HTML 实体（含 &...;，确定）");
            if (Regex.IsMatch(t, @"\\u[0-9a-fA-F]{4}"))
                guesses.Add("Unicode 转义（含 \\uXXXX，确定）");
            if (Regex.IsMatch(t, @"^[01\s]+$") && Regex.Replace(t, @"\s", "").Length % 8 == 0
                && Regex.Replace(t, @"\s", "").Length >= 8)
                guesses.Add("二进制（只有 0/1 且长度是 8 的倍数，确定）");
            if (Regex.IsMatch(t, @"^[.\-\s/]+$") && t.Length >= 3)
                guesses.Add("摩斯电码（只有点和划，确定）");

            // 以下都是「字符集匹配」—— 仅凭字符集不足以断定，所以标注「可能」。
            bool hexOnly = Regex.IsMatch(t, @"^[0-9a-fA-F\s]+$")
                           && Regex.Replace(t, @"\s", "").Length % 2 == 0;
            bool b64Only = Regex.IsMatch(t, @"^[A-Za-z0-9+/]*={0,2}$") && t.Length % 4 == 0 && t.Length >= 4;
            bool b32Only = Regex.IsMatch(t, @"^[A-Z2-7]+=*$") && t.Length % 8 == 0;

            if (hexOnly)
                guesses.Add("hex（全是十六进制字符、长度为偶数）");
            if (b64Only)
                guesses.Add("base64（字符集和长度都对）");
            if (b32Only)
                guesses.Add("base32（A-Z 和 2-7，长度是 8 的倍数）");

            // 哈希长度
            string h = Regex.Replace(t, @"\s", "");
            if (Regex.IsMatch(h, @"^[0-9a-fA-F]+$"))
            {
                switch (h.Length)
                {
                    case 32: guesses.Add("可能是 MD5（32 位十六进制）"); break;
                    case 40: guesses.Add("可能是 SHA-1（40 位）"); break;
                    case 64: guesses.Add("可能是 SHA-256（64 位）"); break;
                    case 96: guesses.Add("可能是 SHA-384（96 位）"); break;
                    case 128: guesses.Add("可能是 SHA-512（128 位）"); break;
                }
            }

            var sb = new StringBuilder();
            sb.Append("输入 ").Append(t.Length).Append(" 字符\n\n");
            if (guesses.Count == 0)
            {
                sb.Append("没有匹配到常见编码特征。\n");
                sb.Append("可能是：加密数据、压缩数据、自定义格式，或者普通的文本。");
            }
            else
            {
                sb.Append("可能是什么：\n");
                foreach (var g in guesses) sb.Append("  · ").Append(g).Append('\n');
                sb.Append("\n下一步：用 auto 动作试解，或者指定 scheme 精确解码。");
            }

            return new ToolResult
            {
                Ok = true,
                Output = sb.ToString(),
                Summary = "identify · " + (guesses.Count > 0 ? guesses[0] : "无匹配"),
            };
        }

        // ---------- 具体编码 ----------

        private static string Rot13(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c >= 'a' && c <= 'z') sb.Append((char)('a' + (c - 'a' + 13) % 26));
                else if (c >= 'A' && c <= 'Z') sb.Append((char)('A' + (c - 'A' + 13) % 26));
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static readonly Dictionary<char, string> MorseMap = BuildMorse();

        private static Dictionary<char, string> BuildMorse()
        {
            var d = new Dictionary<char, string>();
            string[] table = {
                "A.-","B-...","C-.-.","D-..","E.","F..-.","G--.","H....","I..","J.---",
                "K-.-","L.-..","M--","N-.","O---","P.--.","Q--.-","R.-.","S...","T-",
                "U..-","V...-","W.--","X-..-","Y-.--","Z--..",
                "0-----","1.----","2..---","3...--","4....-","5.....","6-....","7--...","8---..","9----.",
                ".-.-.-","--..--","..--..",".----.","-.-.--","-..-.","-.--.","-.--.-",".-...","---...",
                "-.-.-.","-....-",".-.-.","..--.-",".-..-.","...-..-",".--.-.",
            };
            char[] punct = { '.', ',', '?', '\'', '!', '/', '(', ')', '&', ':', ';', '-', '_', '"', '@', '=', '+' };
            for (int i = 0; i < table.Length; i++)
            {
                char ch = i < 26 ? (char)('A' + i)
                        : i < 36 ? (char)('0' + (i - 26))
                        : punct[Math.Min(i - 36, punct.Length - 1)];
                // table 里写的是 "S..." 这种「字符 + 码」连写（方便肉眼核对），
                // 所以取值要跳过第一个字符，否则 d['S'] 会是 "S..."，
                // 反查表里就成了 rev["S..."] = 'S' —— 查 "..." 永远查不到。
                if (!d.ContainsKey(ch)) d[ch] = table[i].Substring(1);
            }
            return d;
        }

        private static string ToMorse(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s.ToUpperInvariant())
            {
                if (c == ' ') { sb.Append(" / "); continue; }
                string code;
                if (MorseMap.TryGetValue(c, out code)) sb.Append(code).Append(' ');
                else sb.Append('?').Append(' ');
            }
            return sb.ToString().TrimEnd();
        }

        private static string FromMorse(string s, out string note)
        {
            note = null;
            var rev = new Dictionary<string, char>();
            foreach (var kv in MorseMap) if (!rev.ContainsKey(kv.Value)) rev[kv.Value] = kv.Key;

            var sb = new StringBuilder();
            // 用 " / " 或 "|" 分隔单词，空格分隔字母
            var words = s.Replace("|", " / ").Split(new[] { " / " }, StringSplitOptions.None);
            for (int w = 0; w < words.Length; w++)
            {
                if (w > 0) sb.Append(' ');
                foreach (var token in words[w].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    char ch;
                    if (rev.TryGetValue(token, out ch)) sb.Append(ch);
                    else { note = "无法识别的摩斯码：" + token; return null; }
                }
            }
            return sb.ToString();
        }

        private const string B32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        private static string ToBase32(byte[] data)
        {
            var sb = new StringBuilder();
            int bits = 0, value = 0;
            foreach (byte b in data)
            {
                value = (value << 8) | b;
                bits += 8;
                while (bits >= 5)
                {
                    sb.Append(B32Alphabet[(value >> (bits - 5)) & 31]);
                    bits -= 5;
                }
            }
            if (bits > 0) sb.Append(B32Alphabet[(value << (5 - bits)) & 31]);
            while (sb.Length % 8 != 0) sb.Append('=');
            return sb.ToString();
        }

        private static string FromBase32(string s)
        {
            string t = s.Trim().TrimEnd('=').ToUpperInvariant().Replace(" ", "");
            int bits = 0, value = 0;
            var bytes = new List<byte>();
            foreach (char c in t)
            {
                int idx = B32Alphabet.IndexOf(c);
                if (idx < 0) return null;
                value = (value << 5) | idx;
                bits += 5;
                if (bits >= 8)
                {
                    bytes.Add((byte)((value >> (bits - 8)) & 0xFF));
                    bits -= 8;
                }
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }
    }
}
