// 灵枢桌面版 —— 文件工具
//
// 读 / 写 / 列目录 / 算哈希 / 展开目录。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpiritScout.Tools
{
    public sealed class FileTool : ITool
    {
        public string Name { get { return "file"; } }

        public string Description
        {
            get
            {
                return "文件操作：read 读文本、write 写文本、list 列目录、hash 算 SHA256。"
                     + "路径可以是绝对路径，也可以是相对当前工作目录的路径。";
            }
        }

        public string ParametersJson
        {
            get
            {
                return @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": { ""type"": ""string"", ""description"": ""read / write / list / hash"" },
    ""path"": { ""type"": ""string"", ""description"": ""文件或目录路径"" },
    ""content"": { ""type"": ""string"", ""description"": ""write 时写入的内容"" },
    ""max_bytes"": { ""type"": ""integer"", ""description"": ""read 最多读多少字节，默认 262144"" }
  },
  ""required"": [""action"", ""path""]
}";
            }
        }

        public Task<ToolResult> InvokeAsync(IDictionary<string, object> args, CancellationToken ct)
        {
            string action = (Arg.Str(args, "action") ?? "").ToLowerInvariant();
            string path = Arg.Str(args, "path");
            if (string.IsNullOrWhiteSpace(path)) return Task.FromResult(ToolResult.Fail("path 不能为空"));

            try
            {
                switch (action)
                {
                    case "read":
                        {
                            if (!File.Exists(path)) return Task.FromResult(ToolResult.Fail("不是文件：" + path));
                            int max = Arg.Int(args, "max_bytes", 262144);
                            if (max <= 0 || max > 4 * 1024 * 1024) max = 262144;
                            var bytes = new byte[max];
                            int n;
                            using (var fs = File.OpenRead(path))
                                n = fs.Read(bytes, 0, max);

                            // 二进制判断：前 2KB 里有 0 字节就按二进制处理
                            bool binary = false;
                            for (int i = 0; i < Math.Min(n, 2048); i++)
                            {
                                if (bytes[i] == 0) { binary = true; break; }
                            }
                            if (binary)
                            {
                                string sha = Sha256(path);
                                return Task.FromResult(new ToolResult
                                {
                                    Ok = true,
                                    Output = "二进制文件，大小 " + new FileInfo(path).Length + " 字节\nsha256=" + sha,
                                    Summary = "读取（二进制）" + Path.GetFileName(path),
                                });
                            }
                            string text = Encoding.UTF8.GetString(bytes, 0, n);
                            return Task.FromResult(new ToolResult
                            {
                                Ok = true,
                                Output = text,
                                Summary = "读取 " + Path.GetFileName(path) + " (" + n + " 字节)",
                            });
                        }

                    case "write":
                        {
                            string content = Arg.Str(args, "content");
                            string dir = Path.GetDirectoryName(path);
                            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                            File.WriteAllText(path, content ?? "", new UTF8Encoding(false));
                            return Task.FromResult(new ToolResult
                            {
                                Ok = true,
                                Output = "已写入 " + new FileInfo(path).Length + " 字节",
                                Summary = "写入 " + Path.GetFileName(path),
                            });
                        }

                    case "list":
                        {
                            if (!Directory.Exists(path)) return Task.FromResult(ToolResult.Fail("不是目录：" + path));
                            var sb = new StringBuilder();
                            int count = 0;
                            // Enumerate* 是惰性的：Get* 会先把整个数组拿回来，
                            // 列一个几万文件的目录就是几万个字符串进内存，
                            // 而我们只要前 200 项。
                            foreach (var d in Directory.EnumerateDirectories(path))
                            {
                                sb.Append("[DIR]  ").Append(Path.GetFileName(d)).Append('\n');
                                if (++count >= 200) break;
                            }
                            if (count < 200)
                            {
                                foreach (var f in Directory.EnumerateFiles(path))
                                {
                                    var fi = new FileInfo(f);
                                    sb.Append(fi.Length.ToString().PadLeft(10)).Append("  ").Append(fi.Name).Append('\n');
                                    if (++count >= 200) break;
                                }
                            }
                            return Task.FromResult(new ToolResult
                            {
                                Ok = true,
                                Output = sb.Length == 0 ? "（空目录）" : sb.ToString(),
                                Summary = "列出 " + path + "（" + count + " 项）",
                            });
                        }

                    case "hash":
                        {
                            if (!File.Exists(path)) return Task.FromResult(ToolResult.Fail("不是文件：" + path));
                            var fi = new FileInfo(path);
                            return Task.FromResult(new ToolResult
                            {
                                Ok = true,
                                Output = "sha256=" + Sha256(path) + "\nsize=" + fi.Length,
                                Summary = "哈希 " + fi.Name,
                            });
                        }

                    default:
                        return Task.FromResult(ToolResult.Fail("未知 action：" + action + "（可用 read/write/list/hash）"));
                }
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail(ex.GetType().Name + ": " + ex.Message));
            }
        }

        private static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
            {
                var hash = sha.ComputeHash(fs);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }

    // ===================== note =====================
}
