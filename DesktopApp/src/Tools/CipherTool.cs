// 灵枢桌面版 —— 古典密码与 XOR
//
// CTF 的 crypto 题里，**凯撒、XOR、栅栏、培根**这四种出现的频率远高于其他。
// 之前的做法是 `shell` 里调 Python：
//     python -c "print(''.join(chr(ord(c)^0x42) for c in '...'))"
//
// 三个问题：没装 Python 就废、引号转义容易错、**爆破要循环几十次**（每次一个进程）。
//
// 做成原生工具，一次调用给出全部候选。
//
// ## 六个动作
//
//   caesar     —— 凯撒，不给 shift 就爆破 1-25 全部
//   xor        —— 单字节 XOR，不给 key 就爆破 0x00-0xFF 全部
//   xorrepeat  —— 重复密钥 XOR（给 key）
//   railfence  —— 栅栏密码，不给栏数就爆破 2-10
//   bacon      —— 培根密码（A/B 或 0/1）
//   atbash     —— 埃特巴什（A↔Z 反向替换）
//   vigenere   —— 维吉尼亚（给 key 解密），以及自动猜 key 长度
//
// ## 「可读」怎么判断
//
// 爆破出来的候选很多都是乱码。判断标准是**英文常见字母占比**：
// ETAOIN SHRDLU 这 12 个字母在正常英文里占 30% 以上，乱码里只占 12% 左右。
// 这个判据比「可打印」准得多（很多乱码也是可打印的）。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpiritScout.Tools
{
    public sealed class CipherTool : ITool
    {
        public string Name { get { return "cipher"; } }

        public string Description
        {
            get
            {
                return "古典密码与 XOR。actions: caesar | xor | xorrepeat | railfence | bacon | atbash | vigenere。"
                     + "不给 shift/key/栏数时会**自动爆破**并只列出「像正常文本」的候选。"
                     + "xortarget：已知明文前缀，用于在爆破中精确定位。";
            }
        }

        public string ParametersJson
        {
            get
            {
                return @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": { ""type"": ""string"", ""enum"": [""caesar"", ""xor"", ""xorrepeat"", ""railfence"", ""bacon"", ""atbash"", ""vigenere""], ""description"": ""用哪种"" },
    ""text"": { ""type"": ""string"", ""description"": ""输入（明文或密文）"" },
    ""key"": { ""type"": ""string"", ""description"": ""密钥：xor 用单字节（如 0x42 或 66）；xorrepeat/vigenere 用字符串"" },
    ""shift"": { ""type"": ""integer"", ""description"": ""caesar 的位移；不给则爆破 1-25"" },
    ""rails"": { ""type"": ""integer"", ""description"": ""railfence 的栏数；不给则爆破 2-10"" },
    ""encrypt"": { ""type"": ""boolean"", ""description"": ""true = 加密方向，默认 false（解密）"" },
    ""xortarget"": { ""type"": ""string"", ""description"": ""已知明文开头（如 flag{），用于筛选爆破结果"" },
    ""max_show"": { ""type"": ""integer"", ""description"": ""最多列多少条候选，默认 20"" }
  },
  ""required"": [""action"", ""text""]
}";
            }
        }

        /// <summary>ETAOIN SHRDLU —— 英文里最高频的 12 个字母。正常英文占比约 30%+。</summary>
        private const string FreqLetters = "ETAOINSHRDLU";

        public Task<ToolResult> InvokeAsync(IDictionary<string, object> args, CancellationToken ct)
        {
            string action = (Arg.Str(args, "action") ?? "").Trim().ToLowerInvariant();
            string text = Arg.Str(args, "text") ?? "";
            string key = Arg.Str(args, "key") ?? "";
            int shift = Arg.Int(args, "shift", -1);
            int rails = Arg.Int(args, "rails", -1);
            bool encrypt = Arg.Bool(args, "encrypt", false);
            string xorTarget = Arg.Str(args, "xortarget") ?? "";
            int maxShow = Arg.Int(args, "max_show", 20);
            if (maxShow < 1) maxShow = 1;
            if (maxShow > 300) maxShow = 300;

            if (text.Length == 0) return Task.FromResult(ToolResult.Fail("text 不能为空"));

            try
            {
                switch (action)
                {
                    case "caesar":    return Task.FromResult(DoCaesar(text, shift, maxShow));
                    case "xor":       return Task.FromResult(DoXor(text, key, xorTarget, maxShow));
                    case "xorrepeat": return Task.FromResult(DoXorRepeat(text, key));
                    case "railfence": return Task.FromResult(DoRailFence(text, rails, encrypt, maxShow));
                    case "bacon":     return Task.FromResult(DoBacon(text));
                    case "atbash":    return Task.FromResult(DoAtbash(text));
                    case "vigenere":  return Task.FromResult(DoVigenere(text, key, encrypt, maxShow));
                    default:
                        return Task.FromResult(ToolResult.Fail("action 不认识：" + action));
                }
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail("处理失败：" + ex.Message));
            }
        }

        // ---------- 凯撒 ----------

        private static ToolResult DoCaesar(string text, int shift, int maxShow)
        {
            var sb = new StringBuilder();
            if (shift >= 0 && shift <= 25)
            {
                string r = Caesar(text, shift);
                sb.Append("shift=").Append(shift).Append("：\n").Append(r);
                return new ToolResult { Ok = true, Output = sb.ToString(), Summary = "caesar shift=" + shift + " · " + r };
            }

            // 爆破全部
            var hits = new List<KeyValuePair<int, string>>();
            for (int s = 1; s <= 25; s++)
            {
                string r = Caesar(text, s);
                if (LooksLikeText(r)) hits.Add(new KeyValuePair<int, string>(s, r));
            }
            hits.Sort((a, b) => Score(b.Value).CompareTo(Score(a.Value)));

            sb.Append("爆破 25 个位移，").Append(hits.Count).Append(" 个看起来像正常文本：\n\n");
            if (hits.Count == 0)
            {
                sb.Append("都不像。可能不是凯撒，或者原文本来就不是英文。\n");
                sb.Append("前 5 个位移的原文供参考：\n");
                for (int s = 1; s <= 5; s++)
                    sb.Append("  shift=").Append(s).Append("：").Append(Clip(Caesar(text, s), 100)).Append('\n');
            }
            else
            {
                int n = 0;
                foreach (var kv in hits)
                {
                    if (n++ >= maxShow) break;
                    sb.Append("shift=").Append(kv.Key).Append("：").Append(Clip(kv.Value, 200)).Append('\n');
                }
            }
            return new ToolResult
            {
                Ok = true,
                Output = sb.ToString(),
                Summary = "caesar 爆破 · " + hits.Count + " 个候选" + (hits.Count > 0 ? "（最可能 shift=" + hits[0].Key + "）" : ""),
            };
        }

        private static string Caesar(string s, int shift)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c >= 'a' && c <= 'z') sb.Append((char)('a' + ((c - 'a' + shift) % 26 + 26) % 26));
                else if (c >= 'A' && c <= 'Z') sb.Append((char)('A' + ((c - 'A' + shift) % 26 + 26) % 26));
                else sb.Append(c);
            }
            return sb.ToString();
        }

        // ---------- XOR ----------

        private static ToolResult DoXor(string text, string keySpec, string target, int maxShow)
        {
            bool wasHex;
            byte[] data = ToBytes(text, out wasHex);
            if (data == null) return ToolResult.Fail("输入既不是文本也不是合法的十六进制");

            var sb = new StringBuilder();
            sb.Append("输入 ").Append(data.Length).Append(" 字节")
              .Append(wasHex ? "（按十六进制解析）" : "（按文本）").Append(" · ");

            // 指定了 key 就直接解
            if (!string.IsNullOrEmpty(keySpec))
            {
                int k;
                if (!TryParseByte(keySpec, out k))
                    return ToolResult.Fail("key 要写成 0x42 或者 66");
                byte[] r = XorSingle(data, (byte)k);
                sb.Append("单字节 XOR 0x").Append(k.ToString("X2")).Append("\n\n");
                sb.Append(Render(r));
                return new ToolResult { Ok = true, Output = sb.ToString(), Summary = "xor 0x" + k.ToString("X2") + " · " + Clip(Render(r), 60) };
            }

            // 爆破 0x00-0xFF
            var hits = new List<KeyValuePair<byte, byte[]>>();
            for (int k = 0; k <= 255; k++)
            {
                byte[] r = XorSingle(data, (byte)k);
                string s = Render(r);
                if (!LooksLikeText(s)) continue;
                // 有已知前缀就只留匹配的
                if (!string.IsNullOrEmpty(target) && s.IndexOf(target, StringComparison.OrdinalIgnoreCase) < 0) continue;
                hits.Add(new KeyValuePair<byte, byte[]>((byte)k, r));
            }
            hits.Sort((a, b) => Score(Render(b.Value)).CompareTo(Score(Render(a.Value))));

            sb.Append("爆破 256 个字节，").Append(hits.Count).Append(" 个像正常文本")
              .Append(string.IsNullOrEmpty(target) ? "" : "（且含「" + target + "」）").Append("：\n\n");

            if (hits.Count == 0)
            {
                sb.Append("都不像。可能：\n");
                sb.Append("  · 密钥不是单字节（试试 xorrepeat）\n");
                sb.Append("  · 原文不是英文（中文 XOR 出来后字母占比很低，会被过滤）\n");
                sb.Append("  · 加了压缩或 base64 外层\n");
            }
            else
            {
                int n = 0;
                foreach (var kv in hits)
                {
                    if (n++ >= maxShow) break;
                    string s = Render(kv.Value);
                    sb.Append("0x").Append(kv.Key.ToString("X2"))
                      .Append(" (").Append(kv.Key).Append(")：").Append(Clip(s, 180)).Append('\n');
                }
            }

            return new ToolResult
            {
                Ok = true,
                Output = sb.ToString(),
                Summary = "xor 爆破 · " + hits.Count + " 个候选" + (hits.Count > 0 ? "（最可能 0x" + hits[0].Key.ToString("X2") + "）" : ""),
            };
        }

        private static ToolResult DoXorRepeat(string text, string key)
        {
            if (string.IsNullOrEmpty(key)) return ToolResult.Fail("xorrepeat 需要给 key");
            bool wasHex;
            byte[] data = ToBytes(text, out wasHex);
            if (data == null) return ToolResult.Fail("输入不是合法的十六进制或文本");

            byte[] k = Encoding.UTF8.GetBytes(key);
            var r = new byte[data.Length];
            for (int i = 0; i < data.Length; i++) r[i] = (byte)(data[i] ^ k[i % k.Length]);
            string s = Render(r);
            return new ToolResult
            {
                Ok = true,
                Output = "重复密钥 XOR（key=\"" + key + "\"，" + k.Length + " 字节）\n\n" + s,
                Summary = "xorrepeat · " + Clip(s, 60),
            };
        }

        private static byte[] XorSingle(byte[] data, byte k)
        {
            var r = new byte[data.Length];
            for (int i = 0; i < data.Length; i++) r[i] = (byte)(data[i] ^ k);
            return r;
        }

        /// <summary>把输入变成字节：看起来像十六进制就按十六进制，否则按 UTF-8 文本。</summary>
        private static byte[] ToBytes(string text, out bool wasHex)
        {
            wasHex = false;
            string t = text.Trim().Replace(" ", "").Replace("\r", "").Replace("\n", "")
                           .Replace("0x", "").Replace("0X", "");
            if (t.Length >= 2 && t.Length % 2 == 0)
            {
                bool allHex = true;
                foreach (char c in t)
                {
                    bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                    if (!ok) { allHex = false; break; }
                }
                if (allHex)
                {
                    var bytes = new byte[t.Length / 2];
                    for (int i = 0; i < bytes.Length; i++)
                        bytes[i] = byte.Parse(t.Substring(i * 2, 2), NumberStyles.HexNumber);
                    wasHex = true;
                    return bytes;
                }
            }
            return Encoding.UTF8.GetBytes(text);
        }

        /// <summary>把字节渲染成可读字符串。非法 UTF-8 用 · 代替，避免一堆 U+FFFD。</summary>
        private static string Render(byte[] data)
        {
            var sb = new StringBuilder(data.Length);
            foreach (byte b in data)
            {
                if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
                else if (b == '\n') sb.Append('\n');
                else if (b == '\r') { }
                else if (b == '\t') sb.Append('\t');
                else if (b >= 0x80) sb.Append('·');   // 非 ASCII 统一用点，避免乱码
                else sb.Append('·');
            }
            return sb.ToString();
        }

        private static bool TryParseByte(string s, out int value)
        {
            value = 0;
            s = (s ?? "").Trim();
            if (s.Length == 0) return false;
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return int.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
                       && value >= 0 && value <= 255;
            if (int.TryParse(s, out value)) return value >= 0 && value <= 255;
            if (s.Length == 1) { value = (int)s[0]; return true; }
            return false;
        }

        // ---------- 栅栏 ----------

        private static ToolResult DoRailFence(string text, int rails, bool encrypt, int maxShow)
        {
            var sb = new StringBuilder();
            if (rails >= 2 && rails <= 50)
            {
                string r = encrypt ? RailEncrypt(text, rails) : RailDecrypt(text, rails);
                return new ToolResult { Ok = true, Output = "栏数 " + rails + "：\n" + r, Summary = "railfence " + rails + " · " + Clip(r, 60) };
            }

            var hits = new List<KeyValuePair<int, string>>();
            for (int n = 2; n <= 10; n++)
            {
                string r = RailDecrypt(text, n);
                if (LooksLikeText(r)) hits.Add(new KeyValuePair<int, string>(n, r));
            }
            hits.Sort((a, b) => Score(b.Value).CompareTo(Score(a.Value)));

            sb.Append("爆破 2-10 栏，").Append(hits.Count).Append(" 个像正常文本：\n\n");
            if (hits.Count == 0) sb.Append("都不像。\n");
            else
            {
                int n = 0;
                foreach (var kv in hits)
                {
                    if (n++ >= maxShow) break;
                    sb.Append("栏数 ").Append(kv.Key).Append("：").Append(Clip(kv.Value, 180)).Append('\n');
                }
            }
            return new ToolResult { Ok = true, Output = sb.ToString(), Summary = "railfence 爆破 · " + hits.Count + " 个候选" };
        }

        private static string RailEncrypt(string s, int rails)
        {
            if (rails < 2) return s;
            var rows = new StringBuilder[rails];
            for (int i = 0; i < rails; i++) rows[i] = new StringBuilder();
            int row = 0, dir = 1;
            foreach (char c in s)
            {
                rows[row].Append(c);
                if (row == 0) dir = 1;
                else if (row == rails - 1) dir = -1;
                row += dir;
            }
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < rails; i++) sb.Append(rows[i]);
            return sb.ToString();
        }

        private static string RailDecrypt(string s, int rails)
        {
            if (rails < 2 || s.Length == 0) return s;
            // 先算出每一行有多少字符
            int[] counts = new int[rails];
            int row = 0, dir = 1;
            for (int i = 0; i < s.Length; i++)
            {
                counts[row]++;
                if (row == 0) dir = 1;
                else if (row == rails - 1) dir = -1;
                row += dir;
            }
            var pos = new int[rails];
            int acc = 0;
            for (int i = 0; i < rails; i++) { pos[i] = acc; acc += counts[i]; }

            var result = new char[s.Length];
            row = 0; dir = 1;
            for (int i = 0; i < s.Length; i++)
            {
                result[i] = s[pos[row]++];
                if (row == 0) dir = 1;
                else if (row == rails - 1) dir = -1;
                row += dir;
            }
            return new string(result);
        }

        // ---------- 培根 ----------

        private static ToolResult DoBacon(string text)
        {
            // 支持两种形态：AB 形式（A=0 B=1）和 01 形式
            string t = text.ToUpperInvariant();
            var bits = new StringBuilder();
            foreach (char c in t)
            {
                if (c == 'A' || c == '0') bits.Append('0');
                else if (c == 'B' || c == '1') bits.Append('1');
            }
            if (bits.Length < 5) return ToolResult.Fail("培根密码至少要 5 位（A/B 或 0/1）");

            var sb = new StringBuilder();
            sb.Append("提取到 ").Append(bits.Length).Append(" 位，每 5 位一组：\n\n");
            var decoded = new StringBuilder();
            for (int i = 0; i + 5 <= bits.Length; i += 5)
            {
                int v = Convert.ToInt32(bits.ToString(i, 5), 2);
                decoded.Append(v < 26 ? (char)('A' + v) : '?');
            }
            sb.Append(decoded.ToString());

            // 常见变体：I/J 和 U/V 合并（24 字母表）
            var decoded24 = new StringBuilder();
            for (int i = 0; i + 5 <= bits.Length; i += 5)
            {
                int v = Convert.ToInt32(bits.ToString(i, 5), 2);
                if (v < 24)
                {
                    char ch = (char)('A' + v + (v >= 8 ? 1 : 0));   // 跳过 I
                    if (ch >= 'U') ch = (char)(ch + 1);              // 跳过 U
                    decoded24.Append(ch);
                }
                else decoded24.Append('?');
            }
            sb.Append("\n\n24 字母变体（I/J、U/V 合并）：\n").Append(decoded24.ToString());

            return new ToolResult
            {
                Ok = true,
                Output = sb.ToString(),
                Summary = "bacon · " + decoded,
            };
        }

        // ---------- 埃特巴什 ----------

        private static ToolResult DoAtbash(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (c >= 'a' && c <= 'z') sb.Append((char)('z' - (c - 'a')));
                else if (c >= 'A' && c <= 'Z') sb.Append((char)('Z' - (c - 'A')));
                else sb.Append(c);
            }
            string r = sb.ToString();
            return new ToolResult { Ok = true, Output = r, Summary = "atbash · " + Clip(r, 60) };
        }

        // ---------- 维吉尼亚 ----------

        private static ToolResult DoVigenere(string text, string key, bool encrypt, int maxShow)
        {
            if (string.IsNullOrEmpty(key))
            {
                // 没给 key 就猜密钥长度（Kasiski 太复杂，这里用「重合指数」的简化版）
                var sb = new StringBuilder();
                sb.Append("没给 key。用「重合指数」猜密钥长度（正常英文约 0.066）：\n\n");
                var results = new List<KeyValuePair<int, double>>();
                for (int len = 1; len <= 20 && len < text.Length / 2; len++)
                    results.Add(new KeyValuePair<int, double>(len, IndexOfCoincidence(text, len)));
                results.Sort((a, b) => Math.Abs(b.Value - 0.066).CompareTo(Math.Abs(a.Value - 0.066)));
                int shown = 0;
                foreach (var kv in results)
                {
                    if (shown++ >= 5) break;
                    sb.Append("  长度 ").Append(kv.Key).Append("：IC = ").Append(kv.Value.ToString("F4")).Append('\n');
                }
                sb.Append("\nIC 最接近 0.066 的长度最可能是密钥长度。拿到长度后可以按列做凯撒爆破。");
                return new ToolResult { Ok = true, Output = sb.ToString(), Summary = "vigenere · 猜测密钥长度" };
            }

            string k = key.ToUpperInvariant();
            var outSb = new StringBuilder(text.Length);
            int ki = 0;
            foreach (char c in text)
            {
                if (c >= 'a' && c <= 'z')
                {
                    int shift = k[ki % k.Length] - 'A';
                    outSb.Append((char)('a' + (((c - 'a') + (encrypt ? shift : -shift)) % 26 + 26) % 26));
                    ki++;
                }
                else if (c >= 'A' && c <= 'Z')
                {
                    int shift = k[ki % k.Length] - 'A';
                    outSb.Append((char)('A' + (((c - 'A') + (encrypt ? shift : -shift)) % 26 + 26) % 26));
                    ki++;
                }
                else outSb.Append(c);
            }
            string r = outSb.ToString();
            return new ToolResult
            {
                Ok = true,
                Output = "key=\"" + key + "\"（" + (encrypt ? "加密" : "解密") + "）\n\n" + r,
                Summary = "vigenere · " + Clip(r, 60),
            };
        }

        /// <summary>重合指数：把文本按 step 分列，算每一列的 IC 再取平均。</summary>
        private static double IndexOfCoincidence(string text, int step)
        {
            double total = 0;
            int cols = 0;
            for (int off = 0; off < step; off++)
            {
                var counts = new int[26];
                int n = 0;
                for (int i = off; i < text.Length; i += step)
                {
                    char c = char.ToUpperInvariant(text[i]);
                    if (c >= 'A' && c <= 'Z') { counts[c - 'A']++; n++; }
                }
                if (n < 2) continue;
                double sum = 0;
                foreach (int v in counts) sum += v * (v - 1);
                total += sum / ((double)n * (n - 1));
                cols++;
            }
            return cols == 0 ? 0 : total / cols;
        }

        // ---------- 文本判定 ----------

        /// <summary>字母频率打分。ETAOIN SHRDLU 占比越高分越高。</summary>
        private static double Score(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int freq = 0, letters = 0, printable = 0;
            foreach (char c in s)
            {
                if (c >= 0x20 && c < 0x7F) printable++;
                char u = char.ToUpperInvariant(c);
                if (u >= 'A' && u <= 'Z')
                {
                    letters++;
                    if (FreqLetters.IndexOf(u) >= 0) freq++;
                }
            }
            if (letters == 0) return 0;
            double printableRatio = printable * 1.0 / s.Length;
            double freqRatio = freq * 1.0 / letters;
            // 空格占比也加分（正常文本有空格）
            int spaces = 0;
            foreach (char c in s) if (c == ' ') spaces++;
            double spaceRatio = spaces * 1.0 / s.Length;

            return printableRatio * 40 + freqRatio * 60 + Math.Min(spaceRatio * 100, 15);
        }

        /// <summary>像不像正常文本（英文为主）。</summary>
        private static bool LooksLikeText(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length < 4) return false;
            int letters = 0, freq = 0, printable = 0;
            foreach (char c in s)
            {
                if (c >= 0x20 && c < 0x7F) printable++;
                char u = char.ToUpperInvariant(c);
                if (u >= 'A' && u <= 'Z')
                {
                    letters++;
                    if (FreqLetters.IndexOf(u) >= 0) freq++;
                }
            }
            // 至少一半是可打印
            if (printable * 2 < s.Length) return false;
            // 字母太少说明可能是中文/符号/乱码 —— 但中文原文 XOR 后也是"可打印"的乱码，
            // 所以要求有一定比例的字母
            if (letters < 3) return false;
            // 高频字母占该文本含字母的 25% 以上（乱码一般 15% 左右）
            return freq * 1.0 / letters >= 0.25;
        }

        private static string Clip(string s, int n)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }
    }
}
