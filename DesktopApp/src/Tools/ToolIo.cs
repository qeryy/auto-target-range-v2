// 灵枢桌面版 —— 流读取辅助
//
// 各工具共用的「读流 + 封顶」。

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
    /// <summary>
    /// 工具之间共用的读流辅助。
    ///
    /// 放这里而不是塞进某个工具类：`http_request` / `web_fetch` 都要「流式读 + 封顶」，
    /// 复制一遍容易只改一处。
    /// </summary>
    internal static class ToolIo
    {
        /// <summary>
        /// 读 HTTP 响应体，最多留 <paramref name="cap"/> 字节。
        ///
        /// `HttpContent.ReadAsStringAsync()` 会把整个响应体读进内存——
        /// 请求一个 500MB 的文件就直接吃 500MB。这里流式读 + 上限。
        ///
        /// HTTP 流和子进程管道不同：直接 break 关掉流就行，不会阻塞对方。
        /// </summary>
        public static async System.Threading.Tasks.Task<string> ReadCappedAsync(
            System.Net.Http.HttpResponseMessage resp, int cap)
        {
            using (var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var ms = new System.IO.MemoryStream())
            {
                var buf = new byte[16384];
                int n;
                while ((n = await stream.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
                {
                    if (ms.Length >= cap) break;
                    int take = (int)Math.Min(n, cap - ms.Length);
                    ms.Write(buf, 0, take);
                }
                string body = ShellTool.DecodeSmart(ms.ToArray());
                if (ms.Length >= cap)
                    body += "\n…（超过 " + (cap / 1024) + "KB，已截断）";
                return body;
            }
        }
    }
}
