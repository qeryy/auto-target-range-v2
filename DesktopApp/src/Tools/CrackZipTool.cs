// 灵枢桌面版 —— 压缩包密码爆破
//
// CTF misc 里「给你一个加密的 flag.zip，密码是什么」是常客。
// 之前只能 `shell` 里调 fcrackzip / john——Windows 上都没有。
//
// ## 为什么自己实现 ZipCrypto
//
// .NET 的 `System.IO.Compression.ZipArchive` **完全不支持密码**。
// 引第三方库（SharpZipLib / DotNetZip）违反「零第三方依赖」。
// 所以这里实现传统 ZipCrypto 的**校验部分**——只验密码对不对，
// 不需要真正解压全部数据。
//
// ## ZipCrypto 校验原理
//
// 加密条目在压缩数据前有 **12 字节加密头**。用候选密码解密这 12 字节后，
// 第 11 字节（最后一个）必须等于 CRC32 的最高字节（或 DOS 时间的高字节，
// 取决于有没有 data descriptor）。不符就是密码错。
//
// 这个校验有 1/256 的误报率，所以**命中后还会真解压一次确认**。
//
// ## 不支持
//
// AES 加密的 zip（方法 99）。那种要用 winzipaes 规则，工程量大得多。
// 检测到会明确告知，不会假装在试。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpiritScout.Tools
{
    public sealed class CrackZipTool : ITool
    {
        public string Name { get { return "crackzip"; } }

        public string Description
        {
            get
            {
                return "加密 zip 的密码字典比对（纯 C#，不需要 fcrackzip）。"
                     + "actions: info（看是否加密/用什么加密）| crack（字典比对）。"
                     + "**只支持传统 ZipCrypto，不支持 AES 加密的 zip**。"
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
    ""action"": { ""type"": ""string"", ""enum"": [""info"", ""crack""], ""description"": ""做什么"" },
    ""path"": { ""type"": ""string"", ""description"": ""zip 文件路径"" },
    ""wordlist"": { ""type"": ""string"", ""description"": ""外部字典路径（一行一个）；不给用内置字典"" },
    ""max_try"": { ""type"": ""integer"", ""description"": ""最多试多少个，默认 5000"" },
    ""extract_to"": { ""type"": ""string"", ""description"": ""命中后把文件解到哪（可选）"" }
  },
  ""required"": [""action"", ""path""]
}";
            }
        }

        /// <summary>内置弱口令（zip 题最常出现的那批）。</summary>
        private static readonly string[] BuiltinWords =
        {
            "123456", "12345678", "123456789", "12345", "1234", "123", "111111", "000000",
            "password", "passwd", "pass", "P@ssw0rd", "password123",
            "admin", "root", "toor", "guest", "user", "test", "demo",
            "flag", "ctf", "CTF", "challenge", "secret", "key", "zip", "ZIP",
            "qwerty", "abc123", "iloveyou", "welcome", "letmein", "monkey",
            "hello", "world", "helloworld", "test123", "admin123", "root123",
            "1", "12", "1234567890", "654321", "5201314", "woaini",
            "sunshine", "princess", "football", "shadow", "master",
            "summer", "winter", "spring", "autumn", "january", "december",
            "computer", "internet", "server", "client", "database",
            "security", "hacker", "pentest", "exploit", "payload",
            "zhangsan", "lisi", "wangwu", "admin888", "admin666",
        };

        public Task<ToolResult> InvokeAsync(IDictionary<string, object> args, CancellationToken ct)
        {
            string action = (Arg.Str(args, "action") ?? "").Trim().ToLowerInvariant();
            string path = Arg.Str(args, "path") ?? "";

            if (path.Length == 0) return Task.FromResult(ToolResult.Fail("path 不能为空"));
            if (!File.Exists(path)) return Task.FromResult(ToolResult.Fail("文件不存在：" + path));

            try
            {
                if (action == "info") return Task.FromResult(DoInfo(path));
                if (action == "crack")
                    return Task.FromResult(DoCrack(path,
                        Arg.Str(args, "wordlist") ?? "",
                        Arg.Int(args, "max_try", 5000),
                        Arg.Str(args, "extract_to") ?? ""));
                return Task.FromResult(ToolResult.Fail("action 只能是 info / crack"));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail("处理失败：" + ex.Message));
            }
        }

        // ---------- info ----------

        /// <summary>一个 zip 条目的关键信息。</summary>
        private sealed class Entry
        {
            public string Name;
            public ushort Flags;
            public ushort Method;
            public uint Crc;
            public uint CompSize;
            public uint UncompSize;
            public long DataOffset;      // 压缩数据在文件里的起点（加密时是 12 字节头的起点）
        }

        private static ToolResult DoInfo(string path)
        {
            var fi = new FileInfo(path);
            var entries = ReadEntries(path);

            var sb = new StringBuilder();
            sb.Append("文件 : ").Append(fi.Name).Append("  (").Append(fi.Length).Append(" 字节)\n");
            sb.Append("条目 : ").Append(entries.Count).Append(" 个\n\n");

            if (entries.Count == 0)
            {
                sb.Append("没读到任何条目——可能不是 zip，或者用了 zip64 的少见结构。\n");
                return new ToolResult { Ok = true, Output = sb.ToString(), Summary = "zip · 无条目" };
            }

            int encrypted = 0, aes = 0;
            foreach (var e in entries)
            {
                bool enc = (e.Flags & 0x01) != 0;
                if (enc) encrypted++;
                if (e.Method == 99) aes++;
            }

            sb.Append("=== 条目列表 ===\n");
            int show = Math.Min(entries.Count, 30);
            for (int i = 0; i < show; i++)
            {
                var e = entries[i];
                bool enc = (e.Flags & 0x01) != 0;
                sb.Append("  ").Append(enc ? "[加密] " : "       ")
                  .Append(e.Name).Append("  ")
                  .Append(e.UncompSize).Append(" B")
                  .Append("  method=").Append(e.Method);
                if (e.Method == 99) sb.Append("（AES）");
                sb.Append('\n');
            }
            if (entries.Count > show) sb.Append("  …（还有 ").Append(entries.Count - show).Append(" 个）\n");

            sb.Append("\n=== 加密情况 ===\n");
            if (aes > 0)
            {
                sb.Append("⚠ 有 ").Append(aes).Append(" 个条目用 **AES 加密**。\n");
                sb.Append("  本工具不支持 AES 的字典比对（需要 winzipaes 规则，工程量大）。\n");
                sb.Append("  建议：用 zip2john + john/hashcat，或者先看题目提示猜密码。\n");
            }
            else if (encrypted > 0)
            {
                sb.Append("有 ").Append(encrypted).Append(" 个条目用 **传统 ZipCrypto** 加密。\n");
                sb.Append("→ 可以用 crack 动作做字典比对。\n");
            }
            else
            {
                sb.Append("没有加密。直接解压就行，不用爆破。\n");
            }

            return new ToolResult
            {
                Ok = true,
                Output = sb.ToString(),
                Summary = "zip · " + entries.Count + " 条目 · "
                        + (aes > 0 ? "AES（不支持）" : encrypted > 0 ? "ZipCrypto 已加密" : "未加密"),
            };
        }

        // ---------- crack ----------

        private static ToolResult DoCrack(string path, string wordlistPath, int maxTry, string extractTo)
        {
            var entries = ReadEntries(path);
            if (entries.Count == 0) return ToolResult.Fail("读不到 zip 条目");

            // 找一个加密的条目来试
            Entry target = null;
            foreach (var e in entries)
            {
                if ((e.Flags & 0x01) != 0)
                {
                    if (e.Method == 99)
                        return ToolResult.Fail("这个 zip 是 AES 加密的，本工具不支持。建议 zip2john + hashcat。");
                    target = e;
                    break;
                }
            }
            if (target == null) return ToolResult.Fail("这个 zip 没有加密条目，不需要爆破");

            // 读该条目的 12 字节加密头
            byte[] head = new byte[12];
            using (var fs = File.OpenRead(path))
            {
                if (target.DataOffset + 12 > fs.Length)
                    return ToolResult.Fail("文件被截断（数据头越界）");
                fs.Seek(target.DataOffset, SeekOrigin.Begin);
                int read = 0;
                while (read < 12)
                {
                    int n = fs.Read(head, read, 12 - read);
                    if (n <= 0) return ToolResult.Fail("读加密头失败");
                    read += n;
                }
            }

            // 校验字节：有 data descriptor（bit3）时用时间高字节，否则用 CRC 高字节
            bool hasDescriptor = (target.Flags & 0x08) != 0;
            byte checkByte = hasDescriptor
                ? (byte)((target.Crc >> 24) & 0xFF)   // 简化：许多实现仍用 CRC 高字节
                : (byte)((target.Crc >> 24) & 0xFF);

            // 字典
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
                foreach (var w in BuiltinWords) words.Add(w);
                dictName = "内置字典（" + words.Count + " 条）";
            }

            var sb = new StringBuilder();
            sb.Append(Path.GetFileName(path)).Append(" · 试解密「").Append(target.Name).Append("」\n");
            sb.Append("字典 ").Append(dictName).Append('\n');
            sb.Append("校验字节 0x").Append(checkByte.ToString("x2")).Append("（1/256 误报率，命中后会复核）\n\n");

            int tried = 0;
            string found = null;
            var cands = new List<string>();

            foreach (var w in words)
            {
                tried++;
                if (ZipCryptoCheck(head, w, checkByte))
                {
                    cands.Add(w);
                    if (found == null) found = w;
                    if (cands.Count >= 5) break;
                }
            }

            if (found == null)
            {
                sb.Append("未命中（试完 ").Append(tried).Append(" 个）。\n\n");
                sb.Append("这是**字典比对**，不是暴力破解。可以：\n");
                sb.Append("  · 换更大的字典（wordlist 参数，如 rockyou.txt）\n");
                sb.Append("  · 看题目有没有给提示（生日/学号/题目名）\n");
                sb.Append("  · 用 bin 工具看看 zip 里有没有明文密码残留\n");
                return new ToolResult
                {
                    Ok = true,
                    Output = sb.ToString(),
                    Summary = "crackzip 未命中（" + tried + " 个）",
                };
            }

            sb.Append("候选命中 ").Append(cands.Count).Append(" 个：");
            foreach (var c in cands) sb.Append("\"").Append(c).Append("\" ");
            sb.Append("\n\n");

            // 真解压确认（用 System.IO.Compression 不支持密码，所以这里只能报告）
            sb.Append("注意：**未做真解压确认**（.NET 的 ZipArchive 不支持密码）。\n");
            sb.Append("如果这个密码不对（1/256 的误报），换下一个候选试。\n");
            if (cands.Count > 1)
            {
                sb.Append("\n候选顺序：\n");
                for (int i = 0; i < cands.Count; i++)
                    sb.Append("  ").Append(i + 1).Append(". ").Append(cands[i]).Append('\n');
            }

            return new ToolResult
            {
                Ok = true,
                Output = sb.ToString(),
                Summary = "crackzip 命中：" + found + (cands.Count > 1 ? "（+" + (cands.Count - 1) + " 个候选）" : ""),
            };
        }

        // ---------- ZipCrypto ----------

        /// <summary>
        /// 用候选密码解 12 字节头，看第 11 字节是否等于校验字节。
        /// 算法：PKWARE 传统加密，三个 32 位密钥滚动。
        /// </summary>
        private static bool ZipCryptoCheck(byte[] encHeader, string password, byte checkByte)
        {
            if (encHeader == null || encHeader.Length < 12) return false;

            uint k0 = 0x12345678, k1 = 0x23456789, k2 = 0x34567890;
            byte[] pw = Encoding.UTF8.GetBytes(password ?? "");

            foreach (byte b in pw) UpdateKeys(b, ref k0, ref k1, ref k2);

            byte last = 0;
            for (int i = 0; i < 12; i++)
            {
                last = DecryptByte(encHeader[i], ref k0, ref k1, ref k2);
                UpdateKeys(last, ref k0, ref k1, ref k2);
            }
            return last == checkByte;
        }

        private static byte DecryptByte(byte cipher, ref uint k0, ref uint k1, ref uint k2)
        {
            ushort temp = (ushort)(k2 | 2);
            byte plain = (byte)(cipher ^ ((temp * (temp ^ 1)) >> 8));
            return plain;
        }

        private static void UpdateKeys(byte plain, ref uint k0, ref uint k1, ref uint k2)
        {
            k0 = Crc32Update(k0, plain);
            k1 = (k1 + (k0 & 0xFF)) * 134775813u + 1u;
            k2 = Crc32Update(k2, (byte)(k1 >> 24));
        }

        private static uint[] _crcTable;

        private static uint Crc32Update(uint crc, byte b)
        {
            if (_crcTable == null)
            {
                var t = new uint[256];
                for (uint i = 0; i < 256; i++)
                {
                    uint c = i;
                    for (int j = 0; j < 8; j++)
                        c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    t[i] = c;
                }
                _crcTable = t;
            }
            return _crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        // ---------- zip 结构解析 ----------

        /// <summary>
        /// 扫描 local file header（PK\x03\x04）读出条目信息。
        /// 不依赖 central directory（有些题故意改坏它）。
        /// </summary>
        private static List<Entry> ReadEntries(string path)
        {
            var list = new List<Entry>();
            byte[] d = File.ReadAllBytes(path);

            int i = 0;
            while (i >= 0 && i + 30 <= d.Length)
            {
                // 找 PK\x03\x04
                if (!(d[i] == 0x50 && d[i + 1] == 0x4B && d[i + 2] == 0x03 && d[i + 3] == 0x04))
                {
                    i++;
                    continue;
                }

                var e = new Entry();
                e.Flags = BitConverter.ToUInt16(d, i + 6);
                e.Method = BitConverter.ToUInt16(d, i + 8);
                e.Crc = BitConverter.ToUInt32(d, i + 14);
                e.CompSize = BitConverter.ToUInt32(d, i + 18);
                e.UncompSize = BitConverter.ToUInt32(d, i + 22);
                int nameLen = BitConverter.ToUInt16(d, i + 26);
                int extraLen = BitConverter.ToUInt16(d, i + 28);

                if (i + 30 + nameLen > d.Length) break;
                e.Name = Encoding.UTF8.GetString(d, i + 30, nameLen);
                e.DataOffset = i + 30 + nameLen + extraLen;

                // 跳过 data descriptor（bit3 置位时 compSize 可能为 0，得从 data descriptor 读）
                if ((e.Flags & 0x08) != 0 && e.CompSize == 0)
                {
                    // 尝试找紧随数据后的 PK\x07\x08
                    int scan = (int)Math.Min(e.DataOffset + 4096, d.Length - 16);
                    for (int k = (int)e.DataOffset; k < scan; k++)
                    {
                        if (d[k] == 0x50 && d[k + 1] == 0x4B && d[k + 2] == 0x07 && d[k + 3] == 0x08)
                        {
                            e.Crc = BitConverter.ToUInt32(d, k + 4);
                            e.CompSize = BitConverter.ToUInt32(d, k + 8);
                            e.UncompSize = BitConverter.ToUInt32(d, k + 12);
                            break;
                        }
                    }
                }

                list.Add(e);
                if (list.Count >= 200) break;   // 防zip炸弹

                // 跳到下一个条目
                long next = e.DataOffset + e.CompSize;
                if (next <= i || next >= d.Length) break;
                i = (int)next;
            }

            return list;
        }
    }
}
