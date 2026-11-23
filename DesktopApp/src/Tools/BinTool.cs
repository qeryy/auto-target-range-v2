// 灵枢桌面版 —— 二进制 / 文件分析工具
//
// CTF 的 misc 和 reverse 里，「这文件是什么、里面有没有藏东西」是固定第一步。
// 之前只能 `shell` 里调 file / strings / xxd——Windows 上前两个都没有。
//
// 这个工具做四件事：
//   info    —— 按 magic number 识别真实类型（不看扩展名）、大小、熵
//   strings —— 提出可打印串（等价 strings -n N），支持按正则过滤
//   hexdump —— 十六进制 + ASCII 对照（等价 xxd），可指定偏移和长度
//   entropy —— 分段算熵，找「哪一段被压缩/加密了」（藏数据的典型特征）
//
// ## 熵怎么用
//
// 正常文本/代码的熵约 4–5 bit/byte；压缩或加密数据接近 8.0。
// 一个文件**大部分是 5.x 但某一段是 7.9**，那段就是塞进去的东西。
// 这是免拆包定位隐藏数据最快的办法。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SpiritScout.Tools
{
    public sealed class BinTool : ITool
    {
        public string Name { get { return "bin"; } }

        public string Description
        {
            get
            {
                return "二进制/文件分析（Windows 上没有 file/strings/xxd）。"
                     + "actions: info（magic 识别真实类型 + 大小 + 整体熵）| "
                     + "strings（提可打印串，可正则过滤）| hexdump（16 字节一行 + ASCII）| "
                     + "entropy（分段算熵，定位藏起来的数据）。";
            }
        }

        public string ParametersJson
        {
            get
            {
                return @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": { ""type"": ""string"", ""enum"": [""info"", ""strings"", ""hexdump"", ""entropy""], ""description"": ""做什么"" },
    ""path"": { ""type"": ""string"", ""description"": ""文件路径"" },
    ""min_len"": { ""type"": ""integer"", ""description"": ""strings 的最短长度，默认 4"" },
    ""pattern"": { ""type"": ""string"", ""description"": ""strings 的过滤正则（只输出匹配的串）"" },
    ""offset"": { ""type"": ""integer"", ""description"": ""hexdump 起始偏移，默认 0"" },
    ""length"": { ""type"": ""integer"", ""description"": ""hexdump 读多少字节，默认 512，上限 65536"" },
    ""chunk"": { ""type"": ""integer"", ""description"": ""entropy 的分段大小，默认 1024"" },
    ""max_hits"": { ""type"": ""integer"", ""description"": ""strings 最多输出多少条，默认 200"" }
  },
  ""required"": [""action"", ""path""]
}";
            }
        }

        public Task<ToolResult> InvokeAsync(IDictionary<string, object> args, CancellationToken ct)
        {
            string action = (Arg.Str(args, "action") ?? "").Trim().ToLowerInvariant();
            string path = Arg.Str(args, "path") ?? "";

            if (path.Length == 0) return Task.FromResult(ToolResult.Fail("path 不能为空"));
            if (!File.Exists(path)) return Task.FromResult(ToolResult.Fail("文件不存在：" + path));

            try
            {
                var fi = new FileInfo(path);
                switch (action)
                {
                    case "info":
                        return Task.FromResult(DoInfo(path, fi));
                    case "strings":
                        return Task.FromResult(DoStrings(path, fi,
                            Arg.Int(args, "min_len", 4), Arg.Str(args, "pattern") ?? "",
                            Arg.Int(args, "max_hits", 200)));
                    case "hexdump":
                        return Task.FromResult(DoHexDump(path, fi,
                            Arg.Int(args, "offset", 0), Arg.Int(args, "length", 512)));
                    case "entropy":
                        return Task.FromResult(DoEntropy(path, fi, Arg.Int(args, "chunk", 1024)));
                    default:
                        return Task.FromResult(ToolResult.Fail("action 只能是 info / strings / hexdump / entropy"));
                }
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail("处理失败：" + ex.Message));
            }
        }

        // ---------- info ----------

        private static ToolResult DoInfo(string path, FileInfo fi)
        {
            byte[] head = new byte[Math.Min(64, (int)Math.Min(fi.Length, 64))];
            using (var fs = File.OpenRead(path))
            {
                int n = fs.Read(head, 0, head.Length);
                if (n < head.Length) Array.Resize(ref head, n);
            }

            string kind = IdentifyMagic(head);
            double ent = FileEntropy(path, Math.Min(fi.Length, 4 * 1024 * 1024));

            var sb = new StringBuilder();
            sb.Append("文件 : ").Append(fi.Name).Append('\n');
            sb.Append("大小 : ").Append(fi.Length).Append(" 字节");
            if (fi.Length >= 1024) sb.Append(" (").Append((fi.Length / 1024.0).ToString("F1")).Append(" KB)");
            if (fi.Length >= 1024 * 1024) sb.Append(" (").Append((fi.Length / 1024.0 / 1024).ToString("F2")).Append(" MB)");
            sb.Append('\n');
            sb.Append("真实类型 : ").Append(kind).Append('\n');
            sb.Append("扩展名 : ").Append(fi.Extension).Append("（不可信——很多题故意改扩展名）\n");
            sb.Append("整体熵 : ").Append(ent.ToString("F3")).Append(" bit/byte  ")
              .Append(EntropyHint(ent)).Append("\n\n");

            sb.Append("=== 前 64 字节 ===\n");
            sb.Append(HexBlock(head, 0));

            return new ToolResult
            {
                Ok = true,
                Output = sb.ToString(),
                Summary = fi.Name + " · " + kind + " · " + fi.Length + "B · 熵 " + ent.ToString("F2"),
            };
        }

        // ---------- strings ----------

        private static ToolResult DoStrings(string path, FileInfo fi, int minLen, string pattern, int maxHits)
        {
            if (minLen < 2) minLen = 2;
            if (minLen > 64) minLen = 64;
            if (maxHits < 1) maxHits = 1;
            if (maxHits > 5000) maxHits = 5000;

            Regex filter = null;
            if (!string.IsNullOrEmpty(pattern))
            {
                try { filter = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5)); }
                catch (Exception ex) { return ToolResult.Fail("过滤正则写错了：" + ex.Message); }
            }

            var hits = new List<string>();
            int total = 0;
            var cur = new StringBuilder();

            using (var fs = File.OpenRead(path))
            {
                var buf = new byte[64 * 1024];
                int n;
                long pos = 0;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    for (int i = 0; i < n; i++)
                    {
                        byte b = buf[i];
                        // 可打印 ASCII + 制表符
                        if ((b >= 0x20 && b < 0x7F) || b == '\t')
                        {
                            cur.Append((char)b);
                        }
                        else
                        {
                            if (cur.Length >= minLen)
                            {
                                string s = cur.ToString();
                                if (filter == null || filter.IsMatch(s))
                                {
                                    total++;
                                    if (hits.Count < maxHits)
                                        hits.Add("0x" + pos.ToString("x8") + "  " + Clip(s, 200));
                                }
                            }
                            cur.Length = 0;
                        }
                    }
                    pos += n;
                }
                // 收尾
                if (cur.Length >= minLen)
                {
                    string s = cur.ToString();
                    if (filter == null || filter.IsMatch(s))
                    {
                        total++;
                        if (hits.Count < maxHits) hits.Add("0x" + pos.ToString("x8") + "  " + Clip(s, 200));
                    }
                }
            }

            var sb = new StringBuilder();
            sb.Append(fi.Name).Append(" · 最短 ").Append(minLen).Append(" 字符");
            if (filter != null) sb.Append(" · 过滤 /").Append(pattern).Append('/');
            sb.Append(" · 命中 ").Append(total).Append(" 条");
            if (total > hits.Count) sb.Append("（只显示前 ").Append(hits.Count).Append("）");
            sb.Append("\n\n");
            if (hits.Count == 0) sb.Append("没有可打印串。可能是压缩/加密过的。\n试试 entropy 看哪一段熵高。");
            else foreach (var h in hits) sb.Append(h).Append('\n');

            return new ToolResult
            {
                Ok = true,
                Output = sb.ToString(),
                Summary = "strings · " + total + " 条",
            };
        }

        // ---------- hexdump ----------

        private static ToolResult DoHexDump(string path, FileInfo fi, int offset, int length)
        {
            if (offset < 0) offset = 0;
            if (length < 1) length = 1;
            if (length > 65536) length = 65536;
            if (offset >= fi.Length) return ToolResult.Fail("offset 超出文件大小（" + fi.Length + "）");

            int want = (int)Math.Min(length, fi.Length - offset);
            var data = new byte[want];
            using (var fs = File.OpenRead(path))
            {
                fs.Seek(offset, SeekOrigin.Begin);
                int read = 0;
                while (read < want)
                {
                    int n = fs.Read(data, read, want - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read < want) Array.Resize(ref data, read);
            }

            var sb = new StringBuilder();
            sb.Append(fi.Name).Append(" · 偏移 0x").Append(offset.ToString("x")).Append(" · ")
              .Append(data.Length).Append(" 字节\n\n");
            sb.Append(HexBlock(data, offset));
            return new ToolResult
            {
                Ok = true,
                Output = sb.ToString(),
                Summary = "hexdump 0x" + offset.ToString("x") + " · " + data.Length + "B",
            };
        }

        // ---------- entropy ----------

        private static ToolResult DoEntropy(string path, FileInfo fi, int chunk)
        {
            if (chunk < 64) chunk = 64;
            if (chunk > 1024 * 1024) chunk = 1024 * 1024;

            var rows = new List<string>();
            double maxEnt = 0;
            int maxIdx = -1;
            long idx = 0;

            using (var fs = File.OpenRead(path))
            {
                var buf = new byte[chunk];
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    double e = Entropy(buf, n);
                    if (e > maxEnt) { maxEnt = e; maxIdx = (int)idx; }
                    // 熵 > 7.5 标红（压缩/加密特征）
                    string flag = e >= 7.5 ? "  ← 高熵（压缩或加密）" : (e >= 6.5 ? "  ← 偏高" : "");
                    rows.Add("  #" + idx.ToString().PadLeft(4) + "  偏移 0x" + (idx * chunk).ToString("x8")
                           + "  熵 " + e.ToString("F2") + flag);
                    idx++;
                    if (idx > 20000) break;   // 20GB 以上就别全扫了
                }
            }

            var sb = new StringBuilder();
            sb.Append(fi.Name).Append(" · ").Append(rows.Count).Append(" 段 × ").Append(chunk).Append(" 字节\n");
            sb.Append("参考：纯文本/代码约 4–5；压缩/加密接近 8.0\n\n");

            if (maxIdx >= 0)
                sb.Append("最高熵段：#").Append(maxIdx).Append(" · 0x").Append((maxIdx * chunk).ToString("x"))
                  .Append(" · ").Append(maxEnt.ToString("F3")).Append(" bit/byte\n\n");

            // 只列高熵段，全列太吵
            var hot = new List<string>();
            foreach (var r in rows) if (r.IndexOf("高熵", StringComparison.Ordinal) >= 0) hot.Add(r);
            if (hot.Count == 0)
            {
                sb.Append("没有明显的高熵段（整份数据的熵都不高）。\n");
                if (rows.Count <= 40) foreach (var r in rows) sb.Append(r).Append('\n');
            }
            else
            {
                sb.Append("高熵段共 ").Append(hot.Count).Append(" 个，前 40 个：\n");
                for (int i = 0; i < hot.Count && i < 40; i++) sb.Append(hot[i]).Append('\n');
            }

            return new ToolResult
            {
                Ok = true,
                Output = sb.ToString(),
                Summary = "entropy · 最高 " + maxEnt.ToString("F2") + (hot.Count > 0 ? " · " + hot.Count + " 段高熵" : ""),
            };
        }

        // ---------- magic ----------

        private static string IdentifyMagic(byte[] b)
        {
            if (b == null || b.Length < 2) return "空文件 / 太小";

            // 文件头（按特征长度从长到短）
            if (b.Length >= 4 && b[0] == 0x7F && b[1] == 0x45 && b[2] == 0x4C && b[3] == 0x46) return "ELF 可执行文件";
            if (b.Length >= 2 && b[0] == 0x4D && b[1] == 0x5A) return "PE / DOS 可执行文件（Windows）";
            if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "PNG 图片";
            if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "JPEG 图片";
            if (b.Length >= 4 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x38) return "GIF 图片";
            if (b.Length >= 2 && b[0] == 0x42 && b[1] == 0x4D) return "BMP 图片";
            if (b.Length >= 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46
                && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return "WebP 图片";
            if (b.Length >= 4 && b[0] == 0x50 && b[1] == 0x4B && b[2] == 0x03 && b[3] == 0x04) return "ZIP / JAR / APK / DOCX（zip 容器）";
            if (b.Length >= 5 && b[0] == 0x25 && b[1] == 0x50 && b[2] == 0x44 && b[3] == 0x46) return "PDF 文档";
            if (b.Length >= 4 && b[0] == 0x1F && b[1] == 0x8B) return "GZIP 压缩";
            if (b.Length >= 6 && b[0] == 0x37 && b[1] == 0x7A && b[2] == 0xBC && b[3] == 0xAF) return "7z 压缩";
            if (b.Length >= 6 && b[0] == 0x52 && b[1] == 0x61 && b[2] == 0x72 && b[3] == 0x21) return "RAR 压缩";
            if (b.Length >= 4 && b[0] == 0xFD && b[1] == 0x37 && b[2] == 0x7A && b[3] == 0x58) return "XZ 压缩";
            if (b.Length >= 4 && b[0] == 0x28 && b[1] == 0xB5 && b[2] == 0x2F && b[3] == 0xFD) return "Zstandard 压缩";
            if (b.Length >= 4 && b[0] == 0x7F && b[1] == 0x45 && b[2] == 0x4C) return "ELF";
            if (b.Length >= 4 && b[0] == 0x00 && b[1] == 0x61 && b[2] == 0x73 && b[3] == 0x6D) return "WebAssembly（wasm）";
            if (b.Length >= 4 && b[0] == 0xCF && b[1] == 0xFA && b[2] == 0xED && b[3] == 0xFE) return "Mach-O（macOS）";
            if (b.Length >= 8 && b[0] == 0xD0 && b[1] == 0xCF && b[2] == 0x11 && b[3] == 0xE0) return "OLE 复合文档（doc/xls/msi）";
            if (b.Length >= 4 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46) return "RIFF 容器（wav/avi）";
            if (b.Length >= 3 && b[0] == 0x49 && b[1] == 0x44 && b[2] == 0x33) return "MP3 音频（ID3）";
            if (b.Length >= 4 && b[0] == 0x4F && b[1] == 0x67 && b[2] == 0x67 && b[3] == 0x53) return "OGG 音频";
            if (b.Length >= 8 && b[0] == 0x66 && b[1] == 0x4C && b[2] == 0x61 && b[3] == 0x43) return "FLAC 音频";
            if (b.Length >= 4 && b[0] == 0x64 && b[1] == 0x65 && b[2] == 0x78 && b[3] == 0x0A) return "Android DEX";
            if (b.Length >= 4 && b[0] == 0x03 && b[1] == 0x00 && b[2] == 0x00 && b[3] == 0x00) return "可能是 Mach-O fat / 未知";
            if (b.Length >= 2 && b[0] == 0x1F && b[1] == 0x9D) return "tar.gz";
            if (b.Length >= 5 && b[0] == 0x42 && b[1] == 0x5A && b[2] == 0x68) return "bzip2 压缩";

            // 文本类
            bool allText = true;
            int limit = Math.Min(b.Length, 64);
            for (int i = 0; i < limit; i++)
            {
                byte c = b[i];
                if (!((c >= 0x20 && c < 0x7F) || c == '\n' || c == '\r' || c == '\t')) { allText = false; break; }
            }
            if (allText)
            {
                string head = Encoding.ASCII.GetString(b, 0, Math.Min(b.Length, 32)).TrimStart();
                if (head.StartsWith("<?xml")) return "XML 文本";
                if (head.StartsWith("<!DOCTYPE") || head.StartsWith("<html", StringComparison.OrdinalIgnoreCase)) return "HTML 文本";
                if (head.StartsWith("{") || head.StartsWith("[")) return "JSON 文本";
                if (head.StartsWith("#!")) return "脚本（shebang）";
                return "纯文本";
            }

            // 高字节比例
            int high = 0;
            for (int i = 0; i < limit; i++) if (b[i] >= 0x80) high++;
            if (high * 10 / Math.Max(limit, 1) >= 9) return "未知二进制（高字节为主，可能是压缩/加密数据）";

            return "未知格式";
        }

        // ---------- 工具 ----------

        private static double Entropy(byte[] data, int len)
        {
            if (len <= 0) return 0;
            var count = new int[256];
            for (int i = 0; i < len; i++) count[data[i]]++;
            double e = 0;
            for (int i = 0; i < 256; i++)
            {
                if (count[i] == 0) continue;
                double p = count[i] / (double)len;
                e -= p * Math.Log(p, 2);
            }
            return e;
        }

        private static double FileEntropy(string path, long cap)
        {
            using (var fs = File.OpenRead(path))
            {
                var buf = new byte[64 * 1024];
                var count = new long[256];
                long total = 0;
                int n;
                while (total < cap && (n = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    for (int i = 0; i < n; i++) count[buf[i]]++;
                    total += n;
                }
                if (total == 0) return 0;
                double e = 0;
                for (int i = 0; i < 256; i++)
                {
                    if (count[i] == 0) continue;
                    double p = count[i] / (double)total;
                    e -= p * Math.Log(p, 2);
                }
                return e;
            }
        }

        private static string HexBlock(byte[] data, long baseOffset)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < data.Length; i += 16)
            {
                sb.Append((baseOffset + i).ToString("x8")).Append("  ");
                int end = Math.Min(i + 16, data.Length);
                for (int j = i; j < i + 16; j++)
                {
                    if (j < end) sb.Append(data[j].ToString("x2")).Append(' ');
                    else sb.Append("   ");
                    if (j == i + 7) sb.Append(' ');
                }
                sb.Append(" |");
                for (int j = i; j < end; j++)
                {
                    byte c = data[j];
                    sb.Append(c >= 0x20 && c < 0x7F ? (char)c : '.');
                }
                sb.Append("|\n");
            }
            return sb.ToString();
        }

        private static string EntropyHint(double e)
        {
            if (e >= 7.5) return "（很高 —— 基本是压缩/加密）";
            if (e >= 6.5) return "（偏高）";
            if (e >= 4.0) return "（文本/代码的典型水平）";
            return "（很低 —— 大量重复字节，可能是空洞或未初始化）";
        }

        private static string Clip(string s, int n)
        {
            if (s == null) return "";
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }
    }
}
