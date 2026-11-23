// 灵枢桌面版 —— 跨进程文件锁
//
// 允许多开之后，「读 → 改 → 写」这套操作在两个窗口之间会互相覆盖。
// 进程内的 lock 管不到另一个进程，所以需要一个跨进程的东西。
//
// 用命名 Mutex（`Local\` 作用域 = 同一次登录会话内互斥）。
//
// 名字从「文件路径的规范化形式」算出来：
//   · 路径要先 ToUpperInvariant——Windows 不区分大小写，
//     `C:\a\config.json` 和 `c:\A\Config.JSON` 必须映射到同一个锁
//   · 再取 SHA1 前 16 位做名字——Mutex 名字不能含反斜杠之外的怪字符，
//     而且太长了也不好。
//
// ## 两层各自管什么
//
//   JsonStore.Write 里的锁   → 保证**文件不会被写坏**（不撕裂、不互相覆盖半截）
//   Store.Write 里的锁       → 保证**不丢数据**（读-改-写整体原子）
//
// 只有前者时：两个窗口都成功写入，但后写的把前写的盖掉（实测经验库丢 23/40 条）。
// 两个都要有。

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SpiritScout.Core
{
    public static class CrossProcessLock
    {
        /// <summary>
        /// 拿一个按文件路径命名的跨进程锁。用完必须 Dispose（用 using）。
        /// </summary>
        /// <param name="path">目标文件路径；用它的全大写形式做 key。</param>
        /// <param name="timeoutMs">等多久。默认 5 秒——正常写盘是毫秒级。</param>
        public static IDisposable Acquire(string path, int timeoutMs = 5000)
        {
            if (string.IsNullOrWhiteSpace(path))
                return new Noop();

            string key;
            try
            {
                string norm = Path.GetFullPath(path).ToUpperInvariant();
                using (var sha = SHA1.Create())
                {
                    byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(norm));
                    var sb = new StringBuilder(16);
                    for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("x2"));
                    key = sb.ToString();
                }
            }
            catch
            {
                // 路径算不出规范化形式（超长、非法字符），退化成本进程锁
                return new Noop();
            }

            var mtx = new Mutex(false, @"Local\SpiritScout.File." + key);
            bool got;
            try
            {
                got = mtx.WaitOne(timeoutMs);
            }
            catch (AbandonedMutexException)
            {
                // 上一个持有者崩了，锁被遗弃；我们拿到了，继续
                got = true;
            }
            catch
            {
                mtx.Dispose();
                return new Noop();
            }

            if (!got)
            {
                mtx.Dispose();
                throw new IOException(
                    "文件正被另一个窗口写入，等了 " + (timeoutMs / 1000) + " 秒仍未释放：" + path);
            }
            return new Holder(mtx);
        }

        private sealed class Holder : IDisposable
        {
            private Mutex _m;
            public Holder(Mutex m) { _m = m; }
            public void Dispose()
            {
                if (_m == null) return;
                try { _m.ReleaseMutex(); } catch { }
                try { _m.Dispose(); } catch { }
                _m = null;
            }
        }

        /// <summary>拿不到锁时的空实现（不阻塞、不抛）。</summary>
        private sealed class Noop : IDisposable
        {
            public void Dispose() { }
        }
    }
}
