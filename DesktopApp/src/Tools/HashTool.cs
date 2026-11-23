// 灵枢桌面版 —— 哈希工具
//
// CTF 里「给你一串 32 位十六进制，问我是什么、明文是什么」是经典题目。
//
// `file` 工具能算哈希（md5/sha1/sha256），但：
//   · 不能**识别**一段字符串是什么哈希
//   · 不能**比对**弱口令
//
// 这个工具补上这两块。
//
// ## 三个动作
//
//   identify  —— 判断哈希类型（按长度 + 字符集 + 前缀特征）
//   compute   —— 算哈希（MD5 / SHA1 / SHA256 / SHA384 / SHA512 / CRC32）
//   crack     —— 弱口令比对：内置常见口令 + 可指定外部字典
//
// ## 关于 crack
//
// **不是暴力破解**，是**字典比对**。所以它只能命中字典里有的东西。
//
// 内置字典是 CTF 里最常出现的那批（admin / 123456 / flag / root / test ...），
// 加上题面常见的变体（后面加年份、加 `!`）。真要做完整字典，得用户自己
// 提供 rockyou.txt 之类的路径。
//
// 这一点在描述里写清楚——免得模型以为给了个 md5 就能还原出任意明文。

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpiritScout.Tools
{
    public sealed class HashTool : ITool
    {
        public string Name { get { return "hash"; } }

        public string Description
        {
            get
            {
                return "哈希：识别类型 / 计算 / 弱口令比对。"
                     + "actions: identify（判断是什么哈希）| compute（算 MD5/SHA1/SHA256/SHA384/SHA512/CRC32）| crack（字典比对）。"
                     + "**crack 是字典比对不是暴力破解**——只能命中内置常见口令或你给的 wordlist。";
            }
        }

        public string ParametersJson
        {
            get
            {
                return @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": { ""type"": ""string"", ""enum"": [""identify"", ""compute"", ""crack""], ""description"": ""做什么"" },
    ""text"": { ""type"": ""string"", ""description"": ""identify/crack 时是要分析的哈希串；compute 时是要算哈希的原文"" },
    ""algo"": { ""type"": ""string"", ""enum"": [""md5"", ""sha1"", ""sha256"", ""sha384"", ""sha512"", ""crc32""], ""description"": ""compute 时用哪种，默认 md5；identify 时可指定以缩小范围"" },
    ""wordlist"": { ""type"": ""string"", ""description"": ""crack 时的外部字典路径（一行一个），不给就用内置字典"" },
    ""max_try"": { ""type"": ""integer"", ""description"": ""crack 最多试多少个，默认 5000"" }
  },
  ""required"": [""action"", ""text""]
}";
            }
        }

        /// <summary>
        /// 内置弱口令。挑的是 CTF 里最常出现的那批，加上题面常见的变体。
        /// 数量不多（约 120 个）—— 目的是「一眼能命中」，不是为了全面覆盖。
        /// </summary>
        private static readonly string[] BuiltinWords =
        {
            "123456", "123456789", "12345678", "1234567", "12345", "1234", "123",
            "password", "passwd", "pass", "p@ssw0rd", "Password", "PASSWORD",
            "admin", "administrator", "root", "toor", "guest", "user", "test",
            "qwerty", "qwerty123", "abc123", "111111", "000000", "666666", "888888",
            "iloveyou", "letmein", "welcome", "monkey", "dragon", "master",
            "sunshine", "princess", "football", "baseball", "shadow", "superman",
            "michael", "jennifer", "trustno1", "hello", "world", "helloworld",
            "flag", "ctf", "challenge", "secret", "key", "default", "changeme",
            "temp", "temporary", "anonymous", "nobody", "system", "service",
            "mysql", "postgres", "oracle", "redis", "mongo", "sa", "sys",
            "123qwe", "qwe123", "1qaz2wsx", "zaq12wsx", "asdfgh", "zxcvbn",
            "1q2w3e4r", "q1w2e3r4", "passw0rd", "pass123", "admin123", "root123",
            "test123", "guest123", "user123", "abc123456", "123456a", "a123456",
            "1234567890", "0123456789", "987654321", "112233", "121212", "131313",
            "5201314", "woaini", "woaini1314", "520520", "1314520",
            "admin888", "admin666", "adminadmin", "rootroot", "testtest",
            "flag{test}", "flag", "FLAG", "CTF", "ctf2024", "ctf2025", "ctf2026",
            "summer2024", "winter2024", "spring2024", "autumn2024",
            "january", "february", "march", "april", "may", "june",
            "july", "august", "september", "october", "november", "december",
            "monday", "tuesday", "wednesday", "thursday", "friday",
            "computer", "internet", "network", "server", "client", "database",
            "security", "hacker", "hacking", "pentest", "exploit", "payload",
            "backdoor", "shellcode", "overflow", "injection", "xss", "sqli",
        };

        public Task<ToolResult> InvokeAsync(IDictionary<string, object> args, CancellationToken ct)
        {
            string action = (Arg.Str(args, "action") ?? "").Trim().ToLowerInvariant();
            string text = Arg.Str(args, "text") ?? "";
            string algo = (Arg.Str(args, "algo") ?? "md5").Trim().ToLowerInvariant();
            string wordlist = Arg.Str(args, "wordlist") ?? "";
            int maxTry = Arg.Int(args, "max_try", 5000);
            if (maxTry < 1) maxTry = 1;
            if (maxTry > 2000000) maxTry = 2000000;

            if (text.Length == 0) return Task.FromResult(ToolResult.Fail("text 不能为空"));

            try
            {
                if (action == "identify") return Task.FromResult(DoIdentify(text, algo));
                if (action == "compute") return Task.FromResult(DoCompute(text, algo));
                if (action == "crack") return Task.FromResult(DoCrack(text, algo, wordlist, maxTry));
                return Task.FromResult(ToolResult.Fail("action 只能是 identify / compute / crack"));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail("处理失败：" + ex.Message));
            }
        }

        // ---------- identify ----------

        private static ToolResult DoIdentify(string text, string hint)
        {
            string h = text.Trim().Replace(" ", "").Replace("-", "");
            var sb = new StringBuilder();
            sb.Append("输入 ").Append(h.Length).Append(" 字符\n\n");

            var guesses = new List<string>();

            // 按前缀判断（最确定）
            if (h.StartsWith("$2a$") || h.StartsWith("$2b$") || h.StartsWith("$2y$"))
                guesses.Add("bcrypt（$2a$/$2b$/$2y$ 前缀，62 字符，无法字典快速比对）");
            if (h.StartsWith("$1$")) guesses.Add("MD5 crypt（$1$ 前缀，Linux /etc/shadow 老格式）");
            if (h.StartsWith("$5$")) guesses.Add("SHA256 crypt（$5$ 前缀）");
            if (h.StartsWith("$6$")) guesses.Add("SHA512 crypt（$6$ 前缀）");
            if (h.StartsWith("$argon2")) guesses.Add("Argon2");
            if (h.StartsWith("$pbkdf2")) guesses.Add("PBKDF2");
            if (h.StartsWith("{")) guesses.Add("可能是 LDAP 的 {SSHA}/{SHA} 格式");

            // 按长度（字符集对的话）
            bool hexOnly = IsHex(h);
            if (hexOnly)
            {
                switch (h.Length)
                {
                    case 8:  guesses.Add("CRC32（8 位十六进制）"); break;
                    case 16: guesses.Add("可能是 MySQL 4.1 之前的 PASSWORD() 或半截 MD5"); break;
                    case 32: guesses.Add("MD5 / NTLM / LM（32 位十六进制，这三者长度相同）"); break;
                    case 40: guesses.Add("SHA1（40 位）"); break;
                    case 48: guesses.Add("可能是 MySQL 5.7 的 PASSWORD()（* + 40 位）"); break;
                    case 56: guesses.Add("SHA224（56 位）—— 本工具算不了，.NET 4.8 没内置"); break;
                    case 64: guesses.Add("SHA256（64 位）"); break;
                    case 96: guesses.Add("SHA384（96 位）"); break;
                    case 128: guesses.Add("SHA512（128 位）"); break;
                }
            }
            if (h.StartsWith("*") && h.Length == 41) guesses.Add("MySQL 5.7 PASSWORD()（* 开头 + 40 位十六进制）");

            // Base64 形态的哈希（有些系统会再编码一次）
            if (h.Length == 24 && IsBase64ish(h)) guesses.Add("可能是 Base64 编码后的 16 字节哈希（如 MD5 再 base64）");
            if (h.Length == 44 && IsBase64ish(h)) guesses.Add("可能是 Base64 编码后的 32 字节哈希（如 SHA256 再 base64）");

            if (guesses.Count == 0)
            {
                sb.Append("没有匹配到已知的哈希形态。\n");
                if (!hexOnly) sb.Append("（不含纯十六进制字符，也不是 bcrypt/crypt 前缀）\n");
            }
            else
            {
                sb.Append("可能是：\n");
                foreach (var g in guesses) sb.Append("  · ").Append(g).Append('\n');
            }

            // 长度相同的那几种怎么区分
            if (hexOnly && h.Length == 32)
            {
                sb.Append("\n怎么区分 MD5 / NTLM：\n");
                sb.Append("  · NTLM 是 UTF-16LE 的 MD5，只能算出来比对，不能从长度看出来\n");
                sb.Append("  · 用 crack 动作试一下——两种都算，命中就说明是这个\n");
            }

            return new ToolResult
            {
                Ok = true,
                Output = sb.ToString(),
                Summary = "identify · " + (guesses.Count > 0 ? guesses[0] : "无匹配"),
            };
        }

        // ---------- compute ----------

        private static ToolResult DoCompute(string text, string algo)
        {
            byte[] input = Encoding.UTF8.GetBytes(text);
            string result;
            switch (algo)
            {
                case "md5":    result = Md5(input); break;
                case "sha1":   result = Sha1(input); break;
                case "sha256": result = Sha256(input); break;
                case "sha384": result = Sha384(input); break;
                case "sha512": result = Sha512(input); break;
                case "crc32":  result = Crc32(input).ToString("x8"); break;
                case "ntlm":   result = Md5(Encoding.Unicode.GetBytes(text)); break;
                default:
                    return ToolResult.Fail("algo 不支持：" + algo);
            }
            return new ToolResult
            {
                Ok = true,
                Output = algo + "(\"" + text + "\") = " + result,
                Summary = algo + " = " + result,
            };
        }

        // ---------- crack ----------

        private static ToolResult DoCrack(string target, string algo, string wordlistPath, int maxTry)
        {
            string h = target.Trim();
            var sb = new StringBuilder();

            // 要试哪些算法：明确了就只试那个，没明确就把可能的都试
            var algos = new List<string>();
            if (!string.IsNullOrEmpty(algo) && algo != "md5") algos.Add(algo);
            else
            {
                // 按长度推
                switch (h.Length)
                {
                    case 32: algos.Add("md5"); algos.Add("ntlm"); break;
                    case 40: algos.Add("sha1"); break;
                    case 56: algos.Add("sha224"); break;
                    case 64: algos.Add("sha256"); break;
                    case 96: algos.Add("sha384"); break;
                    case 128: algos.Add("sha512"); break;
                    default: algos.Add("md5"); break;
                }
            }

            // 字典：外部优先
            var words = new List<string>();
            string dictName;
            if (!string.IsNullOrEmpty(wordlistPath))
            {
                if (!File.Exists(wordlistPath))
                    return ToolResult.Fail("字典不存在：" + wordlistPath);
                try
                {
                    foreach (var line in File.ReadLines(wordlistPath, Encoding.UTF8))
                    {
                        if (words.Count >= maxTry) break;
                        string w = line.TrimEnd('\r', '\n');
                        if (w.Length > 0) words.Add(w);
                    }
                }
                catch (Exception ex)
                {
                    return ToolResult.Fail("读字典失败：" + ex.Message);
                }
                dictName = Path.GetFileName(wordlistPath) + "（" + words.Count + " 条）";
            }
            else
            {
                foreach (var w in BuiltinWords) words.Add(w);
                dictName = "内置字典（" + words.Count + " 条）";
            }

            sb.Append("目标 ").Append(h).Append('\n')
              .Append("算法 ").Append(string.Join(" / ", algos.ToArray())).Append(" · 字典 ").Append(dictName).Append("\n\n");

            string found = null;
            string foundAlgo = null;
            int tried = 0;

            foreach (var word in words)
            {
                tried++;
                foreach (var a in algos)
                {
                    string calc = ComputeFor(word, a);
                    if (calc == null) continue;
                    if (string.Equals(calc, h, StringComparison.OrdinalIgnoreCase))
                    {
                        found = word;
                        foundAlgo = a;
                        break;
                    }
                }
                if (found != null) break;
            }

            if (found != null)
            {
                sb.Append("命中：\"").Append(found).Append("\"（").Append(foundAlgo).Append("，试了 ").Append(tried).Append(" 个）");
                return new ToolResult
                {
                    Ok = true,
                    Output = sb.ToString(),
                    Summary = "crack 命中：" + found + "（" + foundAlgo + "）",
                };
            }

            sb.Append("没命中（试完 ").Append(tried).Append(" 个候选）。\n\n");
            sb.Append("注意：这是**字典比对**，不是暴力破解。没命中说明明文不在字典里。\n");
            sb.Append("下一步可以：\n");
            sb.Append("  · 换个更大的字典（rockyou.txt 之类），用 wordlist 参数指定\n");
            sb.Append("  · 如果题目给了提示（比如「密码是生日」），自己生成候选\n");
            sb.Append("  · 32 位的试试 base64 解码后是不是别的格式\n");

            return new ToolResult
            {
                Ok = true,
                Output = sb.ToString(),
                Summary = "crack 未命中（试了 " + tried + " 个）",
            };
        }

        private static string ComputeFor(string text, string algo)
        {
            try
            {
                switch (algo)
                {
                    case "md5":    return Md5(Encoding.UTF8.GetBytes(text));
                    case "ntlm":   return Md5(Encoding.Unicode.GetBytes(text));
                    case "sha1":   return Sha1(Encoding.UTF8.GetBytes(text));
                    // .NET 4.8 没有内置 SHA224。不假装支持——返回 null 让调用方跳过。
                    case "sha224": return null;
                    case "sha256": return Sha256(Encoding.UTF8.GetBytes(text));
                    case "sha384": return Sha384(Encoding.UTF8.GetBytes(text));
                    case "sha512": return Sha512(Encoding.UTF8.GetBytes(text));
                    default: return null;
                }
            }
            catch { return null; }
        }

        // ---------- 具体算法 ----------

        private static string Hex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        private static string Md5(byte[] data)
        {
            using (var md5 = MD5.Create()) return Hex(md5.ComputeHash(data));
        }

        private static string Sha1(byte[] data)
        {
            using (var sha = SHA1.Create()) return Hex(sha.ComputeHash(data));
        }

        private static string Sha256(byte[] data)
        {
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(data));
        }

        private static string Sha384(byte[] data)
        {
            using (var sha = SHA384.Create()) return Hex(sha.ComputeHash(data));
        }

        private static string Sha512(byte[] data)
        {
            using (var sha = SHA512.Create()) return Hex(sha.ComputeHash(data));
        }

        private static uint Crc32(byte[] data)
        {
            uint[] table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int j = 0; j < 8; j++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in data) crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }

        private static bool IsHex(string s)
        {
            if (s.Length == 0) return false;
            foreach (char c in s)
            {
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok) return false;
            }
            return true;
        }

        private static bool IsBase64ish(string s)
        {
            foreach (char c in s)
            {
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                       || (c >= '0' && c <= '9') || c == '+' || c == '/' || c == '=';
                if (!ok) return false;
            }
            return true;
        }
    }
}
