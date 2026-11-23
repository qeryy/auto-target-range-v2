// 灵枢桌面版 —— 图片分析工具
//
// CTF misc 的图片题套路：
//   · 改高度藏内容（PNG 的 IHDR 高度被改小，下面还有数据）
//   · LSB 隐写（最低位藏信息）
//   · 文件尾部附加数据（图片结束标记之后还跟着东西）
//   · 多图拼接 / 不同通道藏不同图
//
// 之前只能 `shell` 里调 python + PIL，Windows 上得先装 Python。
// 这里用 System.Drawing 做原生实现。
//
// ## 四个动作
//
//   info    —— 宽高、像素格式、DPI、文件头尾、尾部附加数据检测
//   lsb     —— 从最低位提数据（可按通道/位平面）
//   tail    —— 提取结束标记之后的附加数据（常用于藏 zip）
//   strings —— 在图片文件里找可打印串（有时 flag 直接明文躺在注释段）

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SpiritScout.Tools
{
    public sealed class ImageTool : ITool
    {
        public string Name { get { return "img"; } }

        public string Description
        {
            get
            {
                return "图片隐写分析（不需要 Python）。actions: "
                     + "info（尺寸/格式/文件尾附加数据）| lsb（从最低位提隐藏数据）| "
                     + "tail（导出结束标记之后的数据，常是藏进去的 zip）| strings（文件内可打印串）。";
            }
        }

        public string ParametersJson
        {
            get
            {
                return @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": { ""type"": ""string"", ""enum"": [""info"", ""lsb"", ""tail"", ""strings""], ""description"": ""做什么"" },
    ""path"": { ""type"": ""string"", ""description"": ""图片路径"" },
    ""channel"": { ""type"": ""string"", ""enum"": [""rgb"", ""r"", ""g"", ""b"", ""a""], ""description"": ""lsb 取哪个通道，默认 rgb（按 R,G,B 顺序拼）"" },
    ""max_bytes"": { ""type"": ""integer"", ""description"": ""lsb 最多提多少字节，默认 256"" },
    ""out_path"": { ""type"": ""string"", ""description"": ""tail 把提取的数据写到哪；不给则只报告大小和开头几个字节"" },
    ""min_len"": { ""type"": ""integer"", ""description"": ""strings 最短长度，默认 4"" }
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
                switch (action)
                {
                    case "info": return Task.FromResult(DoInfo(path));
                    case "lsb": return Task.FromResult(DoLsb(path,
                        Arg.Str(args, "channel") ?? "rgb", Arg.Int(args, "max_bytes", 256)));
                    case "tail": return Task.FromResult(DoTail(path, Arg.Str(args, "out_path") ?? ""));
                    case "strings": return Task.FromResult(DoStrings(path, Arg.Int(args, "min_len", 4)));
                    default: return Task.FromResult(ToolResult.Fail("action 只能是 info / lsb / tail / strings"));
                }
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail("处理失败：" + ex.Message));
            }
        }

        // ---------- info ----------

        private static ToolResult DoInfo(string path)
        {
            var fi = new FileInfo(path);
            var sb = new StringBuilder();
            sb.Append("文件 : ").Append(fi.Name).Append('\n');
            sb.Append("大小 : ").Append(fi.Length).Append(" 字节\n\n");

            int w = 0, h = 0;
            string fmt = "?";
            PixelFormat pf = PixelFormat.Format24bppRgb;
            try
            {
                using (var img = Image.FromFile(path))
                {
                    w = img.Width; h = img.Height;
                    pf = img.PixelFormat;
                    // **不能直接用 img.RawFormat.ToString()**：它有时返回
                    // "[ImageFormat: b96b3caf-...]" 这种 GUID 形式而不是 "Png"，
                    // 于是后面 FindImageEnd 的 `f.Contains("png")` 永远匹配不上，
                    // 尾部附加数据检测就废了（实测踩过）。
                    // 用文件头 magic 判断，稳定可靠。
                    fmt = MagicKind(path) ?? img.RawFormat.ToString();
                    sb.Append("尺寸 : ").Append(w).Append(" × ").Append(h)
                      .Append("  (").Append((long)w * h).Append(" 像素)\n");
                    sb.Append("格式 : ").Append(fmt).Append('\n');
                    sb.Append("像素 : ").Append(pf).Append('\n');
                    try
                    {
                        sb.Append("DPI  : ").Append(img.HorizontalResolution.ToString("F0"))
                          .Append(" × ").Append(img.VerticalResolution.ToString("F0")).Append('\n');
                    }
                    catch { }
                    try
                    {
                        var props = img.PropertyItems;
                        if (props != null && props.Length > 0)
                        {
                            sb.Append("元数据 : ").Append(props.Length).Append(" 项\n");
                            int shown = 0;
                            foreach (var p in props)
                            {
                                if (shown++ >= 5) break;
                                sb.Append("    id=0x").Append(p.Id.ToString("x4"))
                                  .Append(" len=").Append(p.Len).Append('\n');
                            }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                return ToolResult.Fail("不是能识别的图片：" + ex.Message);
            }

            // 文件尾附加数据
            byte[] all = File.ReadAllBytes(path);
            int endMark = FindImageEnd(all, fmt);
            sb.Append("\n=== 尾部检查 ===\n");
            sb.Append("文件头 : ").Append(HexStr(all, 0, Math.Min(16, all.Length))).Append('\n');
            if (endMark > 0 && endMark < all.Length - 1)
            {
                long extra = all.Length - endMark - 1;
                sb.Append("结束标记在偏移 0x").Append(endMark.ToString("x")).Append('\n');
                sb.Append("标记之后还有 **").Append(extra).Append(" 字节**");
                if (extra > 100) sb.Append("  ← 很可能藏了东西，用 tail 动作导出");
                sb.Append('\n');
                sb.Append("  ").Append(HexStr(all, endMark + 1, Math.Min(32, (int)extra))).Append('\n');
            }
            else
            {
                sb.Append("没找到结束标记，或标记就在文件末尾（无附加数据）\n");
            }

            // 尺寸异常提示
            if (w == 0 || h == 0) sb.Append("\n⚠ 宽或高为 0 —— 可能是被改坏的高度（常见隐写手法）\n");
            long px = (long)w * h;
            if (px > 0 && px < 100) sb.Append("\n⚠ 尺寸极小 —— 内容可能藏在高度之外的数据里\n");

            return new ToolResult
            {
                Ok = true,
                Output = sb.ToString(),
                Summary = fi.Name + " · " + w + "×" + h + " · " + fmt,
            };
        }

        // ---------- lsb ----------

        private static ToolResult DoLsb(string path, string channel, int maxBytes)
        {
            if (maxBytes < 1) maxBytes = 1;
            if (maxBytes > 65536) maxBytes = 65536;

            using (var bmp = new Bitmap(path))
            {
                int w = bmp.Width, h = bmp.Height;
                var bits = new List<byte>();     // 收集所有最低位
                int need = maxBytes * 8;

                using (var work = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(work))
                        g.DrawImage(bmp, new Rectangle(0, 0, w, h));

                    // 逐像素取，不整张 LockBits（避免大图内存爆）
                    for (int y = 0; y < h && bits.Count < need; y++)
                    {
                        for (int x = 0; x < w && bits.Count < need; x++)
                        {
                            Color c = work.GetPixel(x, y);
                            switch (channel)
                            {
                                case "r": bits.Add((byte)(c.R & 1)); break;
                                case "g": bits.Add((byte)(c.G & 1)); break;
                                case "b": bits.Add((byte)(c.B & 1)); break;
                                case "a": bits.Add((byte)(c.A & 1)); break;
                                default:
                                    bits.Add((byte)(c.R & 1));
                                    bits.Add((byte)(c.G & 1));
                                    bits.Add((byte)(c.B & 1));
                                    break;
                            }
                        }
                    }
                }

                int byteCount = bits.Count / 8;
                var data = new byte[byteCount];
                for (int i = 0; i < byteCount; i++)
                {
                    int v = 0;
                    for (int b = 0; b < 8; b++) v = (v << 1) | bits[i * 8 + b];
                    data[i] = (byte)v;
                }

                var sb = new StringBuilder();
                sb.Append(Path.GetFileName(path)).Append(" · 通道 ").Append(channel)
                  .Append(" · 提取 ").Append(byteCount).Append(" 字节\n\n");

                // 前 128 字节按 hex + 可打印显示
                int show = Math.Min(byteCount, 128);
                sb.Append("=== 前 ").Append(show).Append(" 字节 ===\n");
                sb.Append(HexBlock(data, show)).Append('\n');

                // 如果有可读文本，单独列出来
                string text = ExtractText(data);
                if (text.Length >= 4)
                {
                    sb.Append("=== 可读文本 ===\n").Append(text).Append("\n\n");
                }

                // 是否像文件（magic）
                string magic = GuessMagic(data);
                if (magic != null) sb.Append("开头像：").Append(magic).Append('\n');

                if (text.Length < 4 && magic == null)
                    sb.Append("提示：没看出文本也没看出文件头。\n")
                      .Append("  · LSB 可能只用了某一个通道 → 换 channel 试 r / g / b\n")
                      .Append("  · 也可能根本不是 LSB 隐写\n");

                return new ToolResult
                {
                    Ok = true,
                    Output = sb.ToString(),
                    Summary = "LSB(" + channel + ") · " + byteCount + " 字节" + (text.Length >= 4 ? " · 有可读文本" : ""),
                };
            }
        }

        // ---------- tail ----------

        private static ToolResult DoTail(string path, string outPath)
        {
            byte[] all = File.ReadAllBytes(path);
            // 扩展名不可信（题目经常改），优先用文件头
            string ext = MagicKind(path) ?? Path.GetExtension(path).ToLowerInvariant();
            int endMark = FindImageEnd(all, ext);

            if (endMark <= 0 || endMark >= all.Length - 1)
                return new ToolResult
                {
                    Ok = true,
                    Output = "文件 " + all.Length + " 字节，结束标记在 "
                           + (endMark < 0 ? "未找到" : "0x" + endMark.ToString("x"))
                           + "，后面没有附加数据。",
                    Summary = "无附加数据",
                };

            int start = endMark + 1;
            int len = all.Length - start;
            var extra = new byte[len];
            Array.Copy(all, start, extra, 0, len);

            var sb = new StringBuilder();
            sb.Append("结束标记 0x").Append(endMark.ToString("x"))
              .Append(" 之后有 ").Append(len).Append(" 字节\n\n");
            sb.Append("开头 32 字节：\n").Append(HexBlock(extra, Math.Min(32, len))).Append('\n');

            string magic = GuessMagic(extra);
            if (magic != null) sb.Append("这段数据像：").Append(magic).Append('\n');

            if (!string.IsNullOrEmpty(outPath))
            {
                try
                {
                    string dir = Path.GetDirectoryName(outPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllBytes(outPath, extra);
                    sb.Append("\n已导出到：").Append(outPath).Append('\n');
                    if (magic != null)
                        sb.Append("下一步：用 bin 工具 info 看它到底是什么。\n");
                }
                catch (Exception ex)
                {
                    sb.Append("\n导出失败：").Append(ex.Message).Append('\n');
                }
            }
            else
            {
                sb.Append("\n（没给 out_path，只报告不导出。给个路径就能把这段存成文件）\n");
            }

            return new ToolResult
            {
                Ok = true,
                Output = sb.ToString(),
                Summary = "尾部 " + len + " 字节" + (magic != null ? " · " + magic : ""),
            };
        }

        // ---------- strings ----------

        private static ToolResult DoStrings(string path, int minLen)
        {
            if (minLen < 3) minLen = 3;
            byte[] all = File.ReadAllBytes(path);
            var hits = new List<string>();
            var cur = new StringBuilder();
            long pos = 0;

            for (int i = 0; i < all.Length; i++)
            {
                byte b = all[i];
                if ((b >= 0x20 && b < 0x7F) || b == '\t')
                {
                    if (cur.Length == 0) pos = i;
                    cur.Append((char)b);
                }
                else
                {
                    if (cur.Length >= minLen) hits.Add("0x" + pos.ToString("x8") + "  " + Clip(cur.ToString(), 160));
                    cur.Length = 0;
                }
            }
            if (cur.Length >= minLen) hits.Add("0x" + pos.ToString("x8") + "  " + Clip(cur.ToString(), 160));

            var sb = new StringBuilder();
            sb.Append(Path.GetFileName(path)).Append(" · 最短 ").Append(minLen)
              .Append(" · 命中 ").Append(hits.Count).Append(" 条\n\n");
            int show = Math.Min(hits.Count, 300);
            for (int i = 0; i < show; i++) sb.Append(hits[i]).Append('\n');
            if (hits.Count > show) sb.Append("…（还有 ").Append(hits.Count - show).Append(" 条）\n");

            return new ToolResult
            {
                Ok = true,
                Output = sb.ToString(),
                Summary = "strings · " + hits.Count + " 条",
            };
        }

        // ---------- 工具 ----------

        /// <summary>按文件头判断图片格式，返回 "png"/"jpeg"/"gif"/"bmp"，认不出返回 null。</summary>
        private static string MagicKind(string path)
        {
            try
            {
                var head = new byte[8];
                using (var fs = File.OpenRead(path))
                {
                    int n = fs.Read(head, 0, head.Length);
                    if (n < 4) return null;
                }
                if (head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47) return "png";
                if (head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return "jpeg";
                if (head[0] == 0x47 && head[1] == 0x49 && head[2] == 0x46) return "gif";
                if (head[0] == 0x42 && head[1] == 0x4D) return "bmp";
                if (head[0] == 0x52 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x46) return "riff";
                return null;
            }
            catch { return null; }
        }

        /// <summary>找图片的结束标记偏移。找不到返回 -1。</summary>
        private static int FindImageEnd(byte[] d, string fmt)
        {
            string f = (fmt ?? "").ToLowerInvariant();

            if (f.Contains("jpeg") || f.Contains("jpg"))
            {
                // JPEG 以 FF D9 结束，从后往前找
                for (int i = d.Length - 2; i >= 0; i--)
                    if (d[i] == 0xFF && d[i + 1] == 0xD9) return i + 1;   // 返回 } 的第二个字节位置
                return -1;
            }
            if (f.Contains("png"))
            {
                // PNG 以 IEND + 4 字节 CRC 结束：49 45 4E 44 AE 42 60 82（CRC 可能有变，只找 IEND）
                for (int i = d.Length - 8; i >= 0; i--)
                {
                    if (d[i] == 0x49 && d[i + 1] == 0x45 && d[i + 2] == 0x4E && d[i + 3] == 0x44)
                        return Math.Min(i + 7, d.Length - 1);
                }
                return -1;
            }
            if (f.Contains("gif"))
            {
                // GIF 以 0x3B 结束
                for (int i = d.Length - 1; i >= 0; i--)
                    if (d[i] == 0x3B) return i;
                return -1;
            }
            if (f.Contains("bmp"))
            {
                // BMP 的 pixel data 大小在头部，通常到文件末尾正好
                return d.Length - 1;
            }
            // 其他格式：按末尾找 FF D9 兜底
            for (int i = d.Length - 2; i >= 0; i--)
                if (d[i] == 0xFF && d[i + 1] == 0xD9) return i + 1;
            return -1;
        }

        private static string GuessMagic(byte[] d)
        {
            if (d == null || d.Length < 4) return null;
            if (d[0] == 0x50 && d[1] == 0x4B && d[2] == 0x03 && d[3] == 0x04) return "ZIP 压缩包（很可能是藏进去的 flag.zip）";
            if (d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return "PNG 图片";
            if (d[0] == 0xFF && d[1] == 0xD8) return "JPEG 图片";
            if (d[0] == 0x47 && d[1] == 0x49 && d[2] == 0x46) return "GIF 图片";
            if (d[0] == 0x52 && d[1] == 0x61 && d[2] == 0x72) return "RAR 压缩包";
            if (d[0] == 0x37 && d[1] == 0x7A && d[2] == 0xBC) return "7z 压缩包";
            if (d[0] == 0x1F && d[1] == 0x8B) return "GZIP";
            if (d[0] == 0x7F && d[1] == 0x45 && d[2] == 0x4C) return "ELF";
            if (d[0] == 0x4D && d[1] == 0x5A) return "PE 可执行";
            if (d[0] == 0x25 && d[1] == 0x50 && d[2] == 0x44) return "PDF";
            if (d[0] == 0x66 && d[1] == 0x6C && d[2] == 0x61 && d[3] == 0x67) return "文本（开头是 flag）";
            // 全可打印
            bool text = true;
            int lim = Math.Min(d.Length, 32);
            for (int i = 0; i < lim; i++)
                if (!((d[i] >= 0x20 && d[i] < 0x7F) || d[i] == '\n' || d[i] == '\r' || d[i] == '\t')) { text = false; break; }
            if (text) return "纯文本";
            return null;
        }

        private static string ExtractText(byte[] d)
        {
            var sb = new StringBuilder();
            var cur = new StringBuilder();
            foreach (byte b in d)
            {
                if (b >= 0x20 && b < 0x7F) cur.Append((char)b);
                else
                {
                    if (cur.Length >= 4) sb.Append(cur).Append(' ');
                    cur.Length = 0;
                }
            }
            if (cur.Length >= 4) sb.Append(cur);
            string s = sb.ToString().Trim();
            return s.Length > 300 ? s.Substring(0, 300) + "…" : s;
        }

        private static string HexStr(byte[] d, int off, int len)
        {
            var sb = new StringBuilder();
            for (int i = off; i < off + len && i < d.Length; i++) sb.Append(d[i].ToString("x2")).Append(' ');
            return sb.ToString().TrimEnd();
        }

        private static string HexBlock(byte[] d, int len)
        {
            var sb = new StringBuilder();
            int n = Math.Min(len, d.Length);
            for (int i = 0; i < n; i += 16)
            {
                sb.Append(i.ToString("x4")).Append("  ");
                int end = Math.Min(i + 16, n);
                for (int j = i; j < i + 16; j++)
                {
                    sb.Append(j < end ? d[j].ToString("x2") + " " : "   ");
                    if (j == i + 7) sb.Append(' ');
                }
                sb.Append(" |");
                for (int j = i; j < end; j++) sb.Append(d[j] >= 0x20 && d[j] < 0x7F ? (char)d[j] : '.');
                sb.Append("|\n");
            }
            return sb.ToString();
        }

        private static string Clip(string s, int n)
        {
            if (s == null) return "";
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }
    }
}
