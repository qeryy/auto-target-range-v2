// 灵枢桌面版 —— 数据目录
//
// ## 首选位置
//
//   %APPDATA%\SpiritScout\          用户数据（升级不丢）
//
// ## 为什么要有降级
//
// 有些环境（受限账户、AppLocker、某些公司策略、或者从沙箱里跑）不允许往
// AppData 写文件。这时候程序不该直接崩掉——它应该退到「exe 旁边的 data\」
// 继续工作，并把实际位置告诉用户。
//
// 解析顺序：
//   1. %APPDATA%\SpiritScout        （标准）
//   2. <exe目录>\data               （便携 / 受限账户）
//   3. %TEMP%\SpiritScout           （最后手段）
//
// 选定后写进一个内存缓存，并在「设置」页显示实际路径，用户不会猜不到数据在哪。

using System;
using System.IO;
using System.Text;

namespace SpiritScout.Core
{
    public static class AppPaths
    {
        private static string _root;
        private static readonly object Lock = new object();

        /// <summary>用户数据根目录（首次访问时解析并缓存）。</summary>
        public static string DataRoot
        {
            get
            {
                if (_root != null) return _root;
                lock (Lock)
                {
                    if (_root != null) return _root;
                    _root = ResolveRoot();
                }
                return _root;
            }
        }

        /// <summary>实际用的是哪一个候选位置（用于设置页展示）。</summary>
        public static string RootKind { get; private set; }

        /// <summary>
        /// 显式指定数据目录（**必须在首次访问 DataRoot 之前**调用）。
        ///
        /// 两个用途：
        ///   1. 便携模式：exe 旁的 data\ 目录，用户拷走整个文件夹就能带走数据；
        ///   2. 测试：让被测代码指向临时目录，不碰真实数据。
        /// </summary>
        public static bool Override(string root, string kind = "指定")
        {
            if (string.IsNullOrWhiteSpace(root)) return false;
            if (!TryPrepare(root)) return false;
            lock (Lock)
            {
                _root = root;
                RootKind = kind;
            }
            return true;
        }

        /// <summary>清除覆盖（测试用）。</summary>
        public static void Reset()
        {
            lock (Lock) { _root = null; RootKind = null; }
        }

        private static string ResolveRoot()
        {
            var candidates = new[]
            {
                new[]
                {
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "SpiritScout"),
                    "AppData"
                },
                new[]
                {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data"),
                    "程序目录"
                },
                new[]
                {
                    Path.Combine(Path.GetTempPath(), "SpiritScout"),
                    "临时目录"
                },
            };

            foreach (var c in candidates)
            {
                string dir = c[0];
                if (TryPrepare(dir))
                {
                    RootKind = c[1];
                    return dir;
                }
            }

            // 全部失败：返回第一个候选，让上层调用报出真实错误
            RootKind = "不可写";
            return candidates[0][0];
        }

        /// <summary>目录能建、能写，才算可用。</summary>
        private static bool TryPrepare(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".writable-probe");
                File.WriteAllText(probe, "ok", new UTF8Encoding(false));
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static string SessionsDir
        {
            get
            {
                string d = Path.Combine(DataRoot, "sessions");
                try { Directory.CreateDirectory(d); } catch { }
                return d;
            }
        }

        public static string LogDir
        {
            get
            {
                string d = Path.Combine(DataRoot, "logs");
                try { Directory.CreateDirectory(d); } catch { }
                return d;
            }
        }

        public static string ConfigFile { get { return Path.Combine(DataRoot, "config.json"); } }
        public static string ModelsFile { get { return Path.Combine(DataRoot, "models.json"); } }
        public static string ExperienceFile { get { return Path.Combine(DataRoot, "experience.json"); } }
        public static string SecretsFile { get { return Path.Combine(DataRoot, "secrets.dat"); } }

        public static string SessionFile(string sessionId)
        {
            return Path.Combine(SessionsDir, SafeName(sessionId) + ".json");
        }

        /// <summary>
        /// 把会话 id 变成安全的文件名。
        ///
        /// 实现挪到了 `Core.Text.SafeName`（和 McpToolAdapter 里那份重复）。
        /// 顺手修正了一点：原来会把中文换成 `_`，现在保留——会话 id 目前是
        /// `sess-yyyyMMdd-...` 这种纯 ASCII，看不出差别；但万一以后 id 带中文，
        /// 换成 `_` 会让不同会话撞到同一个文件名。
        /// </summary>
        public static string SafeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unnamed";
            return Text.SafeName(s);
        }
    }
}
