// 灵枢桌面版 —— 敏感信息脱敏
//
// 会话文件是**明文 JSON**，用户随时能打开看，也可能把整个会话导出成报告发给别人。
// 而工具摘要（Summary）会原样落进会话文件——比如 shell 的摘要就是
// `exit 0 · <完整命令>`。
//
// 问题在于：**模型自己会在命令里带凭据**。
//   curl -H "Authorization: Bearer sk-abc123..." https://api.example.com
//   python -c "requests.get(url, params={'token': 'xxx'})"
//
// 这些是模型的正常行为（它只是在用它知道的 key 调接口），但**不该原样存下来**。
//
// 所以摘要落盘前先过一遍 Redact。规则刻意保守——**只打明显的凭据形态**，
// 不做「猜哪些词是密码」这种模糊判断，那会误伤正常命令。
//
// 注意：这**不是**在防恶意写入，只是避免「正常的凭据使用」被无意留痕。

using System;
using System.Text.RegularExpressions;

namespace SpiritScout.Core
{
    public static class Redact
    {
        /// <summary>凭据形态的匹配规则。顺序有意义——先长的、更具体的。</summary>
        private static readonly Regex[] Rules =
        {
            // Authorization: Bearer xxx / Basic xxx
            new Regex(@"((?:Authorization|Proxy-Authorization)\s*:\s*(?:Bearer|Basic|Token)\s+)[^\s""';,)]+",
                RegexOptions.IgnoreCase),

            // -H "X-Api-Key: xxx" 这类自定义头
            new Regex(@"((?:X-Api-Key|Api-Key|X-Auth-Token|X-Access-Token)\s*:\s*)[^\s""';,)]+",
                RegexOptions.IgnoreCase),

            // --password xxx / --token xxx / --api-key xxx
            new Regex(@"((?:--password|--passwd|--token|--api-key|--apikey|--secret|--auth)\s+)[^\s""';,)]+",
                RegexOptions.IgnoreCase),

            // URL 查询参数：?token=xxx / &api_key=xxx / &access_token=xxx
            // URL 参数形态（?token=xxx）。**也接受空格/行首后的裸 password=xxx**——
            // 原来只认 `[?&]` 前缀，`另外密码是 password=hunter2` 这种就漏了。
            // 脱敏宁可多不可少：多打一个 *** 不碍事，漏一个就是事故。
            new Regex(@"((?:[?&]|\b)(?:token|access_token|api_key|apikey|secret|password|passwd|auth|key)=)[^&\s""';,)]+",
                RegexOptions.IgnoreCase),

            // --password=xxx / --token=xxx（用等号而不是空格分隔的那种）
            new Regex(@"((?:--password|--passwd|--token|--api-key|--apikey|--secret|--auth)=)[^\s""';,)]+",
                RegexOptions.IgnoreCase),

            // JSON / Python 字典形态："api_key": "xxx" / 'password': 'xxx'
            new Regex(@"((?:[""']?(?:api[_-]?key|access[_-]?token|auth[_-]?token|secret[_-]?key|password|passwd)[""']?\s*[:=]\s*[""']))[^""']+",
                RegexOptions.IgnoreCase),

            // 常见前缀的 key 本体：sk-xxxx / ghp_xxxx / AKIAxxxx / xoxb-xxxx
            //
            // **前缀必须单独成组**：替换逻辑是 `Groups[1].Value + "***"`，
            // 如果第 1 组就是整个 key，替换出来是「原文 + ***」——等于没脱敏。
            // 155 轮起的版本就是这样，导出的报告里 sk-xxx 原样躺着（165 轮测出来）。
            new Regex(@"(\bsk-)[A-Za-z0-9_\-]{8,}", RegexOptions.IgnoreCase),
            new Regex(@"(\bghp_)[A-Za-z0-9_]{8,}", RegexOptions.IgnoreCase),
            new Regex(@"(\bAKIA)[A-Z0-9]{12,}"),
            new Regex(@"(\bxox[baprs]-)[A-Za-z0-9\-]{8,}", RegexOptions.IgnoreCase),
        };

        /// <summary>
        /// 把字符串里的凭据形态换成 `***`。没有敏感内容时原样返回（不分配新字符串）。
        /// </summary>
        public static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            string result = s;
            foreach (var re in Rules)
            {
                result = re.Replace(result, m => m.Groups[1].Value + "***");
            }
            return result;
        }

        /// <summary>
        /// 打码后再截断。工具摘要落盘前统一走这个。
        /// </summary>
        public static string CleanAndClip(string s, int max)
        {
            return Text.OneLine(Clean(s), max);
        }

        /// <summary>这段文本里有没有看起来像凭据的东西（用于界面提示，不发日志）。</summary>
        public static bool HasSecret(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (var re in Rules)
            {
                if (re.IsMatch(s)) return true;
            }
            return false;
        }
    }
}
