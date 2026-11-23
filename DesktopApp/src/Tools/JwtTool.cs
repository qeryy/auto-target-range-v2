// 灵枢桌面版 —— JWT 工具
//
// CTF web 题里 JWT 出现频率极高，而且套路就那么几种：
//   · alg:none 绕过
//   · HS256 弱密钥爆破
//   · 把 RS256 改成 HS256 用公钥当 HMAC 密钥（算法混淆）
//   · kid 注入 / jku 劫持
//
// 之前的做法是 `shell` 里调 python-jwt 或者手工 base64url 解——都别扭。
// 这个工具做四件事：解析、验签、爆破弱密钥、按需重新签发（改 claim 用于提权测试）。
//
// ## 关于「爆破」
//
// 只做 HS256/HS384/HS512 的**字典比对**，不做真正意义的暴破。
// 字典是内置常见弱密钥（secret/key/ctf/flag 那批）+ 支持外部字典。
// 这一点和 hash 工具的 crack 一致——**别指望能还原任意密钥**。

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SpiritScout.Tools
{
    public sealed class JwtTool : ITool
    {
        public string Name { get { return "jwt"; } }

        public string Description
        {
            get
            {
                return "JWT 分析与利用。actions: parse（解 header/payload）| verify（验签）| "
                     + "crack（HS256/384/512 弱密钥字典比对）| forge（改 claim 重新签发，用于越权测试）。"
                     + "**crack 是字典比对不是暴力破解**。";
            }
        }

        public string ParametersJson
        {
            get
            {
                return @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": { ""type"": ""string"", ""enum"": [""parse"", ""verify"", ""crack"", ""forge""], ""description"": ""做什么"" },
    ""token"": { ""type"": ""string"", ""description"": ""JWT 字符串（三段用 . 分隔）"" },
    ""secret"": { ""type"": ""string"", ""description"": ""verify/forge 用的密钥（HMAC 密钥或 PEM 公钥）"" },
    ""claim_name"": { ""type"": ""string"", ""description"": ""forge 时要改的 claim 名（如 role / admin / sub）"" },
    ""claim_value"": { ""type"": ""string"", ""description"": ""forge 时该 claim 的新值；给 null 表示删除"" },
    ""alg"": { ""type"": ""string"", ""description"": ""forge 时指定 alg（如 none / HS256）"" },
    ""wordlist"": { ""type"": ""string"", ""description"": ""crack 时的外部字典路径"" },
    ""max_try"": { ""type"": ""integer"", ""description"": ""crack 最多试多少个，默认 3000"" }
  },
  ""required"": [""action""]
}";
            }
        }

        /// <summary>内置弱密钥字典。JWT 出题最常用的那批。</summary>
        private static readonly string[] BuiltinSecrets =
        {
            "secret", "SECRET", "Secret", "jwt", "JWT", "jwtsecret", "jwt_secret",
            "key", "KEY", "Key", "mykey", "my_key", "mysecret", "my_secret",
            "password", "passwd", "pass", "123456", "1234567890", "admin", "root",
            "flag", "ctf", "CTF", "challenge", "test", "demo", "dev", "development",
            "production", "prod", "staging", "local", "default", "changeme",
            "your-256-bit-secret", "your-secret-key", "your_secret_key",
            "supersecret", "super_secret", "topsecret", "top_secret",
            "hs256", "HS256", "sha256", "hmac", "hmacsecret",
            "signature", "sign", "signing", "signingkey", "signing_key",
            "app", "app_secret", "appsecret", "application", "server", "service",
            "token", "token_secret", "tokensecret", "auth", "authkey", "authorization",
            "1", "12", "123", "1234", "12345", "0000", "1111", "admin123",
            "iloveyou", "welcome", "letmein", "qwerty", "abc123",
            "灵枢", "spiritscout", "SpiritScout",
        };

        public Task<ToolResult> InvokeAsync(IDictionary<string, object> args, CancellationToken ct)
        {
            string action = (Arg.Str(args, "action") ?? "").Trim().ToLowerInvariant();
            string token = (Arg.Str(args, "token") ?? "").Trim();

            if (token.Length == 0) return Task.FromResult(ToolResult.Fail("token 不能为空"));

            string[] parts = token.Split('.');
            if (parts.Length != 3)
                return Task.FromResult(ToolResult.Fail(
                    "不是标准 JWT（应该是 aaa.bbb.ccc 三段，实际 " + parts.Length + " 段）"));

            try
            {
                if (action == "parse") return Task.FromResult(DoParse(parts));
                if (action == "verify") return Task.FromResult(DoVerify(parts, (Arg.Str(args, "secret") ?? "").Trim()));
                if (action == "crack") return Task.FromResult(DoCrack(parts,
                    Arg.Str(args, "wordlist") ?? "", Arg.Int(args, "max_try", 3000)));
                if (action == "forge") return Task.FromResult(DoForge(parts,
                    Arg.Str(args, "alg") ?? "", Arg.Str(args, "secret") ?? "",
                    Arg.Str(args, "claim_name") ?? "", Arg.Str(args, "claim_value")));
                return Task.FromResult(ToolResult.Fail("action 只能是 parse / verify / crack / forge"));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail("处理失败：" + ex.Message));
            }
        }

        // ---------- parse ----------

        private static ToolResult DoParse(string[] parts)
        {
            string headerJson = B64UrlDecode(parts[0]);
            string payloadJson = B64UrlDecode(parts[1]);

            var sb = new StringBuilder();
            sb.Append("=== header ===\n").Append(PrettyJson(headerJson)).Append("\n\n");
            sb.Append("=== payload ===\n").Append(PrettyJson(payloadJson)).Append("\n\n");

            string alg = ExtractJsonString(headerJson, "alg");
            sb.Append("=== 分析 ===\n");
            sb.Append("  算法 : ").Append(string.IsNullOrEmpty(alg) ? "(未声明)" : alg).Append('\n');

            if (string.Equals(alg, "none", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append("  ⚠ **alg=none** —— 服务端如果接受它，签名校验可完全绕过。\n");
                sb.Append("    试：把第三段（签名）留空，直接发这个 token。\n");
            }
            else if (alg != null && alg.StartsWith("HS", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append("  对称签名 → 可试 crack 动作爆弱密钥。\n");
            }
            else if (alg != null && alg.StartsWith("RS", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append("  非对称签名 → 可试「算法混淆」：改成 HS256、用公钥当 HMAC 密钥。\n");
            }

            // 常见提权线索
            var interesting = new[] { "role", "admin", "is_admin", "user", "sub", "uid", "id", "scope", "permissions" };
            var found = new List<string>();
            foreach (var k in interesting)
            {
                string v = ExtractJsonString(payloadJson, k);
                if (v != null) found.Add(k + " = " + v);
                else if (Regex.IsMatch(payloadJson, "\"" + k + "\"\\s*:\\s*(true|false|\\d+)", RegexOptions.IgnoreCase))
                {
                    var m = Regex.Match(payloadJson, "\"" + k + "\"\\s*:\\s*([^,}]+)", RegexOptions.IgnoreCase);
                    if (m.Success) found.Add(k + " = " + m.Groups[1].Value.Trim());
                }
            }
            if (found.Count > 0)
            {
                sb.Append("\n  可提权的 claim：\n");
                foreach (var f in found) sb.Append("    ").Append(f).Append('\n');
                sb.Append("    用 forge 动作改它们（如 role=admin）。\n");
            }

            return new ToolResult
            {
                Ok = true,
                Output = sb.ToString(),
                Summary = "JWT 解析 · alg=" + (string.IsNullOrEmpty(alg) ? "?" : alg),
            };
        }

        // ---------- verify ----------

        private static ToolResult DoVerify(string[] parts, string secret)
        {
            if (secret.Length == 0) return ToolResult.Fail("verify 需要 secret");

            string alg = ExtractJsonString(B64UrlDecode(parts[0]), "alg");
            if (string.IsNullOrEmpty(alg)) alg = "HS256";

            string expected = HmacSign(parts[0] + "." + parts[1], secret, alg);
            if (expected == null)
                return ToolResult.Fail("暂不支持该算法验签：" + alg + "（当前支持 HS256/384/512）");

            bool ok = string.Equals(expected, parts[2], StringComparison.Ordinal);
            return new ToolResult
            {
                Ok = true,
                Output = (ok ? "✓ 签名有效" : "✗ 签名不匹配")
                       + "\n  算法: " + alg
                       + "\n  期望签名: " + expected
                       + "\n  实际签名: " + parts[2],
                Summary = "JWT 验签 · " + (ok ? "有效" : "不匹配"),
            };
        }

        // ---------- crack ----------

        private static ToolResult DoCrack(string[] parts, string wordlistPath, int maxTry)
        {
            string alg = ExtractJsonString(B64UrlDecode(parts[0]), "alg");
            if (string.IsNullOrEmpty(alg)) alg = "HS256";
            if (!alg.StartsWith("HS", StringComparison.OrdinalIgnoreCase))
                return ToolResult.Fail("只有 HS* 算法能字典比对（当前是 " + alg + "）");

            var words = new List<string>();
            string dictName;
            if (!string.IsNullOrEmpty(wordlistPath))
            {
                if (!File.Exists(wordlistPath)) return ToolResult.Fail("字典不存在：" + wordlistPath);
                foreach (var line in File.ReadLines(wordlistPath, Encoding.UTF8))
                {
                    if (words.Count >= maxTry) break;
                    string w = line.TrimEnd('\r', '\n');
                    if (w.Length > 0) words.Add(w);
                }
                dictName = Path.GetFileName(wordlistPath) + "（" + words.Count + " 条）";
            }
            else
            {
                foreach (var w in BuiltinSecrets) words.Add(w);
                dictName = "内置字典（" + words.Count + " 条）";
            }

            string signingInput = parts[0] + "." + parts[1];
            int tried = 0;
            foreach (var w in words)
            {
                tried++;
                string sig = HmacSign(signingInput, w, alg);
                if (sig != null && string.Equals(sig, parts[2], StringComparison.Ordinal))
                {
                    return new ToolResult
                    {
                        Ok = true,
                        Output = "命中：密钥 = \"" + w + "\"（" + alg + "，试了 " + tried + " 个）\n"
                               + "字典：" + dictName + "\n\n"
                               + "下一步：用 forge 动作 + secret=" + w + " 改 claim 重签。",
                        Summary = "JWT 爆破命中：" + w,
                    };
                }
            }

            return new ToolResult
            {
                Ok = true,
                Output = "未命中（试完 " + tried + " 个，字典：" + dictName + "）。\n\n"
                       + "这是**字典比对**，不是暴力破解。没命中说明密钥不在字典里。\n"
                       + "可以：换更大的字典（wordlist 参数）、或回到 parse 看 alg 是否可改成 none。",
                Summary = "JWT 爆破未命中（" + tried + " 个）",
            };
        }

        // ---------- forge ----------

        private static ToolResult DoForge(string[] parts, string algOverride, string secret,
            string claimName, string claimValue)
        {
            string headerJson = B64UrlDecode(parts[0]);
            string payloadJson = B64UrlDecode(parts[1]);

            // 改 alg
            if (!string.IsNullOrEmpty(algOverride))
                headerJson = SetJsonString(headerJson, "alg", algOverride);

            // 改 claim
            if (!string.IsNullOrEmpty(claimName))
            {
                if (claimValue == null)
                    payloadJson = RemoveJsonKey(payloadJson, claimName);
                else
                {
                    // 数字/布尔按原样塞，其余加引号
                    string v = claimValue;
                    bool bare = Regex.IsMatch(v, @"^(true|false|null|-?\d+(\.\d+)?)$");
                    payloadJson = SetJsonValue(payloadJson, claimName, bare ? v : QuoteJson(v));
                }
            }

            string newHeader = B64UrlEncode(headerJson);
            string newPayload = B64UrlEncode(payloadJson);
            string signingInput = newHeader + "." + newPayload;

            string alg = ExtractJsonString(headerJson, "alg");
            string sig;
            if (string.IsNullOrEmpty(alg) || string.Equals(alg, "none", StringComparison.OrdinalIgnoreCase))
            {
                sig = "";   // alg=none 时签名为空
            }
            else
            {
                if (string.IsNullOrEmpty(secret))
                    return ToolResult.Fail("alg=" + alg + " 需要 secret 才能重签");
                sig = HmacSign(signingInput, secret, alg);
                if (sig == null) return ToolResult.Fail("不支持的算法：" + alg);
            }

            string token = signingInput + "." + sig;
            return new ToolResult
            {
                Ok = true,
                Output = "=== 新 token ===\n" + token + "\n\n"
                       + "=== header ===\n" + PrettyJson(headerJson) + "\n\n"
                       + "=== payload ===\n" + PrettyJson(payloadJson),
                Summary = "JWT 重签 · alg=" + (string.IsNullOrEmpty(alg) ? "none" : alg)
                        + (string.IsNullOrEmpty(claimName) ? "" : " · " + claimName + "=" + claimValue),
            };
        }

        // ---------- base64url ----------

        private static string B64UrlDecode(string s)
        {
            string t = s.Replace('-', '+').Replace('_', '/');
            switch (t.Length % 4)
            {
                case 2: t += "=="; break;
                case 3: t += "="; break;
                case 1: throw new FormatException("base64url 长度非法");
            }
            return Encoding.UTF8.GetString(Convert.FromBase64String(t));
        }

        private static string B64UrlEncode(string s)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(s))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        // ---------- HMAC ----------

        private static string HmacSign(string input, string secret, string alg)
        {
            byte[] key = Encoding.UTF8.GetBytes(secret);
            byte[] data = Encoding.UTF8.GetBytes(input);
            byte[] hash;
            switch ((alg ?? "").ToUpperInvariant())
            {
                case "HS256": using (var h = new HMACSHA256(key)) hash = h.ComputeHash(data); break;
                case "HS384": using (var h = new HMACSHA384(key)) hash = h.ComputeHash(data); break;
                case "HS512": using (var h = new HMACSHA512(key)) hash = h.ComputeHash(data); break;
                default: return null;
            }
            return ToB64Url(hash);
        }

        private static string ToB64Url(byte[] b)
        {
            return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        // ---------- 极简 JSON 处理 ----------
        //
        // 不引 JSON 库（System.Web.Extensions 的 Serializer 会把数字变成
        // "1" 这种字符串、还会重排键序，改 claim 时不好控制）。
        // JWT 的 header/payload 都是扁平的，正则足够。

        private static string ExtractJsonString(string json, string key)
        {
            var m = Regex.Match(json ?? "", "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"",
                RegexOptions.IgnoreCase);
            return m.Success ? Regex.Unescape(m.Groups[1].Value) : null;
        }

        private static string SetJsonString(string json, string key, string value)
        {
            return SetJsonValue(json, key, QuoteJson(value));
        }

        private static string QuoteJson(string v)
        {
            return "\"" + (v ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static string SetJsonValue(string json, string key, string rawValue)
        {
            json = json.Trim();
            bool exists = Regex.IsMatch(json, "\"" + Regex.Escape(key) + "\"\\s*:");
            if (exists)
            {
                return Regex.Replace(json,
                    "(\"" + Regex.Escape(key) + "\"\\s*:\\s*)(\"(?:[^\"\\\\]|\\\\.)*\"|true|false|null|-?\\d+(?:\\.\\d+)?|\\[[^\\]]*\\]|\\{[^}]*\\})",
                    m => m.Groups[1].Value + rawValue, RegexOptions.IgnoreCase);
            }
            // 追加：去掉尾部 }
            int close = json.LastIndexOf('}');
            if (close < 0) return json;
            string body = json.Substring(0, close).TrimEnd();
            string sep = body.EndsWith("{") ? "" : ", ";
            return body + sep + "\"" + key + "\": " + rawValue + "}";
        }

        private static string RemoveJsonKey(string json, string key)
        {
            string r = Regex.Replace(json,
                ",?\\s*\"" + Regex.Escape(key) + "\"\\s*:\\s*(\"(?:[^\"\\\\]|\\\\.)*\"|true|false|null|-?\\d+(?:\\.\\d+)?|\\[[^\\]]*\\]|\\{[^}]*\\})",
                "", RegexOptions.IgnoreCase).Trim();
            // 修掉可能出现的 {, ...}
            return Regex.Replace(r, "\\{\\s*,", "{");
        }

        /// <summary>把紧凑 JSON 排成一行一个键，方便人读。</summary>
        private static string PrettyJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return "(空)";
            try
            {
                var sb = new StringBuilder();
                int depth = 0;
                bool inStr = false;
                foreach (char c in json)
                {
                    if (c == '"' && (sb.Length == 0 || sb[sb.Length - 1] != '\\')) inStr = !inStr;
                    if (!inStr && (c == '{' || c == '['))
                    {
                        sb.Append(c).Append('\n');
                        depth++;
                        sb.Append(new string(' ', depth * 2));
                    }
                    else if (!inStr && (c == '}' || c == ']'))
                    {
                        sb.Append('\n');
                        depth--;
                        sb.Append(new string(' ', Math.Max(0, depth) * 2));
                        sb.Append(c);
                    }
                    else if (!inStr && c == ',')
                    {
                        sb.Append(c).Append('\n').Append(new string(' ', depth * 2));
                    }
                    else if (!inStr && c == ':' && depth <= 2)
                    {
                        sb.Append(": ");
                    }
                    else if (!inStr && c == ' ')
                    {
                        continue;
                    }
                    else sb.Append(c);
                }
                return sb.ToString();
            }
            catch { return json; }
        }
    }
}
