// 灵枢桌面版 —— 纯文本处理
//
// 这些函数原来在四个地方各有一份：
//   Theme.OneLine（Ui 层，160 字）
//   Experience.OneLine（Knowledge 层，150 字）
//   AgentLoop.OneLine（Session 层，200 字）
//   WebSearchTool.OneLine（Tools 层，长度可变）
//
// 内容几乎一样，只是截断长度不同。之所以各写一份，是因为 **Theme 在 Ui 层，
// 而 Session / Tools / Knowledge 不该引用 Ui 层**（分层原则）。缺的是一个
// 谁都能用的地方。
//
// 放 Core 层就解决了：Core 是最底层，谁都可以引用它，它不引用任何人。
//
// 收敛之后：
//   · 改一次就全生效（比如以后要给省略号改样式、要处理 emoji 宽度）
//   · 少 4 份重复代码
//   · 各层的调用方从「自己实现」变成「用同一个约定」

using System;
using System.Text;

namespace SpiritScout.Core
{
    public static class Text
    {
        /// <summary>
        /// 压成一行并截断。用于状态栏、日志、工具摘要这类「只显示一行」的地方。
        /// </summary>
        /// <summary>
        /// 关键词是否命中任意一个字段（**大小写不敏感**）。
        ///
        /// 三个页面（任务 / 经验 / 技能）原来各写一遍筛选，语义还不完全一致：
        /// 有的先判 `kw.Length > 0`，有的把字段拼成一个 haystack 再找。
        /// 收成一处之后语义固定：
        ///
        ///   · **空白关键词 → true**（不筛，等价于「全部显示」）
        ///   · 任一字段包含关键词（忽略大小写）→ true
        ///   · `null` 字段当作空串
        ///
        /// ```csharp
        /// Text.Hit("jwt", s.Id, s.Description, s.Body)
        /// ```
        /// </summary>
        public static bool Hit(string keyword, params string[] fields)
        {
            string kw = (keyword ?? "").Trim();
            if (kw.Length == 0) return true;          // 不筛
            if (fields == null || fields.Length == 0) return false;

            foreach (var f in fields)
            {
                if (string.IsNullOrEmpty(f)) continue;
                if (f.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        public static string OneLine(string s, int max = 160)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            if (max <= 0) return s;
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        /// <summary>
        /// 截断但保留头尾（头 2/3、尾 1/3），中间用 ... 省略。
        ///
        /// 和 OneLine 不同：这个**保留尾部**。日志里的关键信息常在末尾
        /// （错误码、耗时、退出状态），只截头会把最有用的部分丢掉。
        /// </summary>
        public static string Clip(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            if (n <= 0) return "";
            if (s.Length <= n) return s;
            int head = n * 2 / 3;
            int tail = n - head;
            if (tail <= 0) return s.Substring(0, head) + "...";
            return s.Substring(0, head) + "..." + s.Substring(s.Length - tail);
        }

        /// <summary>
        /// 把任意字符串变成可用作文件名/标识符的形式。
        ///
        /// 保留：字母、数字、`-`、`_`、以及所有非 ASCII（中文文件名是合法的）。
        /// 其余（`\ / : * ? " &lt; &gt; |` 空格 等）换成 `_`。
        /// </summary>
        public static string SafeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c > 0x7F) sb.Append(c);
                else sb.Append('_');
            }
            return sb.ToString();
        }

        /// <summary>文件大小（B / KB / MB / GB）。</summary>
        public static string Size(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024L * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("F1") + " MB";
            return (bytes / 1024.0 / 1024 / 1024).ToString("F2") + " GB";
        }

        /// <summary>
        /// 显示用的「多少秒/分钟/小时」。
        ///
        /// 只给到两个量级：`3 秒`、`5 分钟`、`2 小时 15 分钟`。再多一个量级
        /// 读者反而要在脑子里换算。
        /// </summary>
        public static string Duration(TimeSpan t)
        {
            if (t.TotalSeconds < 1) return "不到 1 秒";
            if (t.TotalMinutes < 1) return Math.Round(t.TotalSeconds) + " 秒";
            if (t.TotalHours < 1) return Math.Round(t.TotalMinutes) + " 分钟";
            return Math.Floor(t.TotalHours) + " 小时 " + Math.Round((double)t.Minutes) + " 分钟";
        }
    }
}
