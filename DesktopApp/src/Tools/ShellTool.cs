// 灵枢桌面版 —— shell 工具
//
// 在用户机器上执行命令。
// 注意：没有容器沙箱 —— 跑的就是用户自己的命令行，权限就是当前用户的权限。

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
    public sealed class ShellTool : ITool
    {
        public string Name { get { return "shell"; } }

        public string Description
        {
            get
            {
                return "在用户本机执行一条 shell 命令并返回输出。"
                     + "Windows 下走 cmd.exe，Linux/macOS 下走 /bin/sh。"
                     + "适合执行扫描、查看文件、跑脚本这类操作。"
                     + "命令在用户机器上真实执行，权限就是当前用户的权限。";
            }
        }

        public string ParametersJson
        {
            get
            {
                return @"{
  ""type"": ""object"",
  ""properties"": {
    ""command"": { ""type"": ""string"", ""description"": ""要执行的命令"" },
    ""timeout"": { ""type"": ""integer"", ""description"": ""超时秒数，默认 60，最大 600"" }
  },
  ""required"": [""command""]
}";
            }
        }

        public async Task<ToolResult> InvokeAsync(IDictionary<string, object> args, CancellationToken ct)
        {
            string command = Arg.Str(args, "command");
            if (string.IsNullOrWhiteSpace(command)) return ToolResult.Fail("command 不能为空");

            int timeout = Arg.Int(args, "timeout", 60);
            if (timeout <= 0 || timeout > 600) timeout = 60;

            bool windows = Environment.OSVersion.Platform == PlatformID.Win32NT;

            // Windows 的 cmd 默认用 GBK 输出，中文会糊成乱码，模型看到乱码后会反复
            // 换命令重试（实测一次 ls 花了 4 次调用、近 8000 token）。
            // 这里在命令前加 chcp 65001 把代码页切到 UTF-8，并强制进程用 UTF-8 解码。
            string actual = windows ? "chcp 65001 >nul && " + command : command;

            var psi = new ProcessStartInfo
            {
                FileName = windows ? "cmd.exe" : "/bin/sh",
                Arguments = windows ? "/c " + actual : "-c \"" + command.Replace("\"", "\\\"") + "\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                // 刻意不设 StandardOutputEncoding：中文 Windows 上子进程多半输出
                // GBK，硬用 UTF-8 解码会把中文全变成 U+FFFD。下面走原始字节再自动识别。
            };

            try
            {
                using (var proc = new Process { StartInfo = psi })
                {
                    proc.Start();
                    // 读原始字节：中文 Windows 上子进程输出可能是 GBK 也可能是 UTF-8，
                    // 由 DecodeSmart 决定用哪个解。
                    var outTask = ReadBytesAsync(proc.StandardOutput.BaseStream);
                    var errTask = ReadBytesAsync(proc.StandardError.BaseStream);

                    var done = await Task.Run(() => proc.WaitForExit(timeout * 1000), ct)
                        .ConfigureAwait(false);
                    if (!done)
                    {
                        try { proc.Kill(); } catch { }
                        return ToolResult.Fail("命令超时（" + timeout + " 秒）已终止");
                    }

                    string stdout = DecodeSmart(await outTask.ConfigureAwait(false));
                    string stderr = DecodeSmart(await errTask.ConfigureAwait(false));
                    int code = proc.ExitCode;

                    var sb = new StringBuilder();
                    sb.Append("exit=").Append(code).Append('\n');
                    if (!string.IsNullOrEmpty(stdout)) sb.Append("--- stdout ---\n").Append(Core.Text.OneLine(stdout, 12000));
                    if (!string.IsNullOrEmpty(stderr)) sb.Append("\n--- stderr ---\n").Append(Core.Text.OneLine(stderr, 4000));

                    string summary = "exit " + code + " · " + command;
                    return new ToolResult
                    {
                        Ok = code == 0,
                        Output = sb.ToString(),
                        // 摘要会落进会话文件，先脱敏——模型可能在命令里直接带 key
                Summary = Core.Redact.CleanAndClip(summary, 120),
                    };
                }
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ex.GetType().Name + ": " + ex.Message);
            }
        }


        /// <summary>管道最多留这么多字节，超出部分读出来直接丢掉。</summary>
        private const int StreamCap = 256 * 1024;

        /// <summary>
        /// 读子进程输出，但**只留前 256KB**。
        ///
        /// 两个坑：
        ///
        /// 1. **不能无限读进内存。** 原来是 `while(Read) ms.Write(...)` 再 `ToArray()`，
        ///    一条 `dir C:\ /s` 输出几百 MB 就是几百 MB 内存，`ToArray` 再复制一份，
        ///    峰值翻倍。而我们实际只用前 12000 字符（`Clip` 的额度）。
        ///
        /// 2. **也不能读满就 break。** 子进程往管道里写，没人读它就会阻塞，
        ///    命令永远退不出去，最后只能等超时。所以超出的部分**继续读、丢掉**。
        /// </summary>
        private static async System.Threading.Tasks.Task<byte[]> ReadBytesAsync(System.IO.Stream stream)
        {
            using (var ms = new System.IO.MemoryStream())
            {
                var buf = new byte[8192];
                int n;
                while ((n = await stream.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
                {
                    if (ms.Length < StreamCap)
                    {
                        int take = (int)Math.Min(n, StreamCap - ms.Length);
                        ms.Write(buf, 0, take);
                    }
                    // 超出的部分读到就丢，目的是让管道继续流动
                }

                if (ms.Length >= StreamCap)
                {
                    // 给截断留个痕迹，免得模型以为命令只输出了这么多
                    byte[] mark = Encoding.UTF8.GetBytes("\n…（输出超过 256KB，已截断）");
                    ms.Write(mark, 0, mark.Length);
                }
                return ms.ToArray();
            }
        }

        /// <summary>
        /// 智能解码：先按 UTF-8 解，若出现替换字符（说明不是 UTF-8）就退回 GBK。
        /// 中文 Windows 上 cmd 与 powershell 的输出多数是 GBK，直接 UTF-8 解会全糊。
        /// </summary>
        internal static string DecodeSmart(byte[] data)
        {
            if (data == null || data.Length == 0) return "";

            string utf8 = Encoding.UTF8.GetString(data);
            if (utf8.IndexOf('\uFFFD') < 0) return utf8;

            try
            {
                Encoding gbk = Encoding.GetEncoding(936);
                string gb = gbk.GetString(data);
                // 选替换字符更少的那个
                if (CountBad(gb) <= CountBad(utf8)) return gb;
            }
            catch { }

            return utf8;
        }

        private static int CountBad(string s)
        {
            int n = 0;
            foreach (char c in s) if (c == '\uFFFD') n++;
            return n;
        }
    }

    // ===================== http =====================
}
