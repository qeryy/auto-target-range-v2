// 灵枢桌面版（原生）—— 程序入口
//
// 一次启动做三件事：
//   1. 找到程序目录（单文件版先自解压运行时）；
//   2. 拉起 Python 后端，等端口就绪；
//   3. 显示原生 WinForms 主窗口。
//
// 不依赖浏览器：界面全是 WinForms 控件，跑在 .NET Framework 4.8 上——
// 这个运行时 Windows 10/11 自带，用户不需要额外安装任何东西。

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Forms;

namespace SpiritScout.Native
{
    internal static class Program
    {
        /// <summary>单文件版载荷尾标：前 8 字节长度 + 24 字节哈希 + 这 8 字节签名。</summary>
        private const string PayloadMagic = "SSRTPAY1";

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string baseDir;
            try
            {
                baseDir = ResolveBaseDir(args);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "准备运行时失败：\n\n" + ex.Message,
                    "灵枢 · SpiritScout", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var service = new BackendService();
            // 客户端先用空流占位；BootAsync 里服务起来后会把真流换进去
            var api = new SpiritApi(Stream.Null, Stream.Null);
            service.Log += _ => { };

            Application.ThreadException += (s, e) =>
            {
                MessageBox.Show("未处理的错误：\n\n" + e.Exception.Message,
                    "灵枢 · SpiritScout", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            using (var form = new MainForm(service, api))
            {
                form.Shown += async (s, e) =>
                {
                    await form.BootAsync(baseDir).ConfigureAwait(true);
                    // 服务起来后把管道接到客户端上
                    api.Rebind(service.StdinStream, service.StdoutStream);
                    form.OnBackendReady();
                };
                Application.Run(form);
            }

            api.Dispose();
            service.Dispose();
        }

        private static int DetectPort()
        {
            string env = Environment.GetEnvironmentVariable("SPIRITSCOUT_PORT");
            int p;
            if (!string.IsNullOrWhiteSpace(env) && int.TryParse(env, out p) && p > 0 && p < 65536)
                return p;
            return 8010;
        }

        /// <summary>
        /// 确定工作目录：单文件版就把尾部载荷解到 %LOCALAPPDATA%\SpiritScout\runtime，
        /// 正常 exe 就用自身所在目录。
        /// </summary>
        private static string ResolveBaseDir(string[] args)
        {
            string exePath = Assembly.GetEntryAssembly() != null
                ? Assembly.GetEntryAssembly().Location
                : Process.GetCurrentProcess().MainModule.FileName;
            string exeDir = Path.GetDirectoryName(exePath);

            // 已经是「带 py\ 的目录」就直接用
            if (File.Exists(Path.Combine(exeDir, "py", "python.exe")))
                return exeDir;

            // 试从自身尾部取载荷（C# 5 不能在内联处声明 out 变量）
            string extracted;
            if (TryExtractPayload(exePath, out extracted))
                return extracted;

            // 都没有：就用 exe 目录（开发态下会退到 .venv）
            return exeDir;
        }

        /// <summary>
        /// 从 exe 尾部解出运行时。尾标格式见 build-single.ps1：
        /// [8 字节载荷长度][24 字节 SHA-256][8 字节 "SSRTPAY1"]。
        /// </summary>
        private static bool TryExtractPayload(string exePath, out string dir)
        {
            dir = null;
            try
            {
                using (var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (fs.Length < 40) return false;
                    fs.Seek(-40, SeekOrigin.End);
                    var trailer = new byte[40];
                    fs.Read(trailer, 0, 40);

                    string magic = System.Text.Encoding.ASCII.GetString(trailer, 32, 8);
                    if (magic != PayloadMagic) return false;

                    long length = BitConverter.ToInt64(trailer, 0);
                    if (length <= 0 || length > fs.Length - 40) return false;

                    var expected = new byte[24];
                    Array.Copy(trailer, 8, expected, 0, 24);

                    string target = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "SpiritScout", "runtime");

                    // 已经解过且指纹一致就复用
                    string stamp = Path.Combine(target, ".payload.sha");
                    string wantHex = BitConverter.ToString(expected).Replace("-", "").ToLowerInvariant();
                    if (File.Exists(Path.Combine(target, "py", "python.exe"))
                        && File.Exists(stamp)
                        && File.ReadAllText(stamp).Trim() == wantHex)
                    {
                        dir = target;
                        return true;
                    }

                    long offset = fs.Length - 40 - length;
                    fs.Seek(offset, SeekOrigin.Begin);

                    // 先落盘成 zip，校验哈希，再解压
                    string tmpZip = Path.Combine(Path.GetTempPath(),
                        "spiritscout-payload-" + Guid.NewGuid().ToString("N") + ".zip");
                    using (var outFs = new FileStream(tmpZip, FileMode.Create, FileAccess.Write))
                    {
                        var buffer = new byte[1 << 20];
                        long remain = length;
                        using (var sha = SHA256.Create())
                        {
                            while (remain > 0)
                            {
                                int want = (int)Math.Min(buffer.Length, remain);
                                int got = fs.Read(buffer, 0, want);
                                if (got <= 0) break;
                                outFs.Write(buffer, 0, got);
                                sha.TransformBlock(buffer, 0, got, null, 0);
                                remain -= got;
                            }
                            sha.TransformFinalBlock(new byte[0], 0, 0);
                            var actual = sha.Hash;
                            for (int i = 0; i < 24; i++)
                            {
                                if (actual == null || actual[i] != expected[i])
                                    throw new InvalidDataException("载荷校验失败（文件可能损坏）。");
                            }
                        }
                    }

                    if (Directory.Exists(target))
                    {
                        try { Directory.Delete(target, true); } catch { }
                    }
                    Directory.CreateDirectory(target);
                    ZipFile.ExtractToDirectory(tmpZip, target);
                    try { File.Delete(tmpZip); } catch { }

                    File.WriteAllText(stamp, wantHex);
                    dir = target;
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
