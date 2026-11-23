// 灵枢桌面版（纯 C# 重写）—— 数据目录与持久化
//
// ## 目录布局（用户要求：数据在 AppData，升级不丢）
//
//   %APPDATA%\SpiritScout\             ← 用户数据，升级时**永不删除**
//       config.json                    配置
//       sessions\<id>.json             每个会话一个文件（含消息）
//       experience.json                提炼出的经验
//       models.json                    模型清单
//       secrets.dat                    密钥（加密后）
//
//   程序本体用 exe 自带（或 %LOCALAPPDATA%\SpiritScout\app\ 下的运行时）。
//
// ## 为什么用 JSON 文件而不是 SQLite
//
// .NET Framework 4.8 没有内置 SQLite 驱动，引入就得随包带一个 DLL —— 那正好
// 违背「单个 exe、不依赖其他东西」。单用户桌面软件的数据量（几十到几百个会话）
// 用文件完全够，而且是纯文本，出问题时用户能自己打开看。
//
// 真到需要索引的那天，把 Repository 换成 SQLite 实现即可，上层一行不用改。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace SpiritScout.Core
{
    /// <summary>JSON 读写：统一 UTF-8、缩进、原子替换（先写临时文件再换）。</summary>
    public static class JsonStore
    {
        private static readonly JavaScriptSerializer Ser = new JavaScriptSerializer
        {
            MaxJsonLength = 256 * 1024 * 1024,
            RecursionLimit = 256,
        };

        public static T Read<T>(string path, T fallback) where T : class
        {
            try
            {
                if (!File.Exists(path)) return fallback;
                string text = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(text)) return fallback;
                var value = Ser.Deserialize<T>(text);
                return value ?? fallback;
            }
            catch (Exception ex)
            {
                // 文件损坏不该让程序起不来：退回默认值，坏文件改名留证。
                // 但**不能完全静默**——用户会以为自己配的东西丢了还不知道为什么。
                // 记一条日志，并把备份路径写进去，出问题时能找到原件。
                string backup = "";
                try
                {
                    if (File.Exists(path))
                    {
                        backup = path + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                        File.Move(path, backup);
                    }
                }
                catch { }

                try
                {
                    Core.ConfigManager.Log("配置文件损坏，已回退默认值：" + path
                        + " · " + ex.GetType().Name + ": " + ex.Message
                        + (backup.Length > 0 ? " · 原件已备份为 " + Path.GetFileName(backup) : ""));
                }
                catch { }
                return fallback;
            }
        }

        /// <summary>
        /// 全局写锁。
        ///
        /// `SessionStore` 自己的 `_lock` 是**实例级**的，而各页面各自 `new SessionStore()`，
        /// 锁根本不共享。两个线程同时写同一个文件时：
        ///   · 一个把 .tmp 写了一半，另一个去 Replace → IOException「正由另一进程使用」
        ///   · 一个 Replace 完删掉 .tmp，另一个再去 Move → FileNotFoundException
        ///
        /// 实测：8 线程 × 50 次写 config.json，**289 次异常**。
        /// 用户快速切换页面就能触发（每页刷新都会写 config）。
        ///
        /// 这里加一把静态锁兜底——同一进程内所有 JSON 写入串行化。
        /// 代价是写文件变成串行（本来也就几毫秒），收益是再也不会互相踩。
        /// </summary>
        private static readonly object WriteLock = new object();

        /// <summary>
        /// 原子地「读 → 改 → 写」。
        ///
        /// 直接 `Write(path, 内存里的对象)` 在多开时是「后写的覆盖前写的」：
        /// 窗口 A 改了 RecallLimit，窗口 B 改了 DefaultModel，两边各自拿着
        /// 自己读到的旧快照整体覆盖，最后只有一个改动活下来。
        ///
        /// 这个方法的做法是：**持锁 → 重新读盘 → 在磁盘最新的基础上改 → 写回**。
        /// 所以两边都能活下来。
        ///
        /// 注意 `mutate` 拿到的是**磁盘上最新的对象**，不是调用方内存里那份。
        /// 调用方该改什么，就在 mutate 里改什么。
        /// </summary>
        public static void Update<T>(string path, Action<T> mutate, T fallback) where T : class
        {
            if (mutate == null) return;
            using (CrossProcessLock.Acquire(path))
            {
                var current = Read<T>(path, fallback) ?? fallback;
                mutate(current);
                WriteCore(path, current);   // 已经持锁，不再嵌套取锁
            }
        }

        public static void Write<T>(string path, T value)
        {
            // 两层：
            //   CrossProcessLock —— 跨进程（允许多开之后必须的）
            //   WriteLock        —— 跨线程（同进程内多个页面各自写）
            //
            // 这一层只保证「文件不会被写坏」。要保证「不丢数据」，
            // 「读-改-写」整体也得在锁里——那是各 Store 自己的责任。
            using (CrossProcessLock.Acquire(path))
            {
                lock (WriteLock)
                {
                    WriteCore(path, value);
                }
            }
        }

        private static void WriteCore<T>(string path, T value)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string json = Ser.Serialize(value);

            // 临时文件名带唯一后缀。
            //
            // 原来是固定的 `path + ".tmp"`——单进程没问题，但**允许多开之后**
            // 两个窗口同时写 config.json 就会抢同一个临时文件：
            // 一个写一半，另一个 File.Replace 时读到半个文件（或者干脆找不到）。
            string tmp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));

            // 落盘重试。
            //
            // 进程内的 WriteLock 管不到另一个进程。两个窗口同时 Replace 同一个目标时，
            // Windows 会抛 IOException（文件被占用）、FileNotFoundException（对方刚把
            // 临时文件挪走）。这类冲突是**暂时的**，等几十毫秒就好。
            //
            // 最多重试 12 次，累计约 1 秒。到这里还失败说明不是并发问题，
            // 那就真的写不进去（只读盘、权限），交给上层报错。
            Exception last = null;
            for (int attempt = 0; attempt < 12; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                        File.Replace(tmp, path, null);   // 第三个参数 null = 不留备份
                    else
                        File.Move(tmp, path);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    // File.Replace 在跨卷或某些文件系统上会失败——那种情况退回 Move 一次
                    if (ex is IOException && File.Exists(tmp))
                    {
                        try
                        {
                            try { if (File.Exists(path)) File.Delete(path); } catch { }
                            File.Move(tmp, path);
                            return;
                        }
                        catch (Exception ex2) { last = ex2; }
                    }
                    System.Threading.Thread.Sleep(30 + attempt * 20);
                }
            }

            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw new IOException("写文件失败（重试 12 次）：" + path + " — " + last.Message, last);
        }

        public static void Delete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                // 删不掉要说一声。
                //
                // 静默的话用户以为删掉了，重启后它又冒出来——比「删失败弹窗」更让人困惑。
                // 常见原因：文件被另一个窗口占着（多开）、只读属性、权限不足。
                ConfigManager.Log("删除文件失败：" + path + " — " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    /// <summary>应用配置（存 config.json）。</summary>
    public sealed class AppConfig
    {
        /// <summary>
        /// 数据格式版本（见 Core/DataVersion.cs）。
        /// 老文件没有这个字段 → 读出 0 → 当作 v1 兼容处理。
        /// </summary>
        public int SchemaVersion { get; set; }

        public string DefaultModel { get; set; }
        public int RecallLimit { get; set; }
        public bool DistillEnabled { get; set; }
        public string WorkspaceRoot { get; set; }

        /// <summary>
        /// 会话自动命名候选链：按顺序尝试的模型名列表。
        ///
        /// web 版 `models.yaml` 里的 `naming_chain`：
        /// 起新会话时按这个顺序试每个模型，第一个能用的拿来起标题。
        /// 空列表 = 用 `Target`（web 也是这么 fallback 的）。
        /// </summary>
        public List<string> NamingChain { get; set; }

        /// <summary>主题：dark / light。默认深色（桌面一直以来的样子）。</summary>
        public string ThemeMode { get; set; }

        // 默认 "dark"（桌面一直以来的样子），不是空串 ——
        // 空串和 "light" 不等，启动时会一直当成深色。
        // 切过浅色才会写成 "light"。

        public AppConfig()
        {
            DefaultModel = "";
            RecallLimit = 6;
            DistillEnabled = true;
            WorkspaceRoot = "";
            ThemeMode = "dark";     // 默认深色（旧数据没有它就是深色）
        }
    }

    /// <summary>会话存储：一个会话一个文件，读写都走这里。</summary>
    public sealed class SessionRepository
    {
        /// <summary>列出全部会话（按更新时间倒序）。</summary>
        public List<Session> List()
        {
            var result = new List<Session>();
            try
            {
                foreach (var file in Directory.GetFiles(AppPaths.SessionsDir, "*.json"))
                {
                    var s = JsonStore.Read<Session>(file, null);
                    if (s != null) result.Add(s);
                }
            }
            catch (Exception ex)
            {
                // 走到这里说明**目录遍历本身**失败了（权限、路径不存在、磁盘掉线），
                // 不是单个文件读不出来（那种情况 Read() 内部已经记过日志）。
                // 结果是列表全空——不报原因的话用户会以为「我的会话都没了」。
                ConfigManager.Log("列举会话失败：" + ex.GetType().Name + ": " + ex.Message);
            }
            result.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
            return result;
        }

        public Session Get(string id)
        {
            return JsonStore.Read<Session>(AppPaths.SessionFile(id), null);
        }

        public void Save(Session session)
        {
            if (session == null || string.IsNullOrEmpty(session.Id)) return;
            session.UpdatedAt = DateTime.Now;
            JsonStore.Write(AppPaths.SessionFile(session.Id), session);
        }

        public void Delete(string id)
        {
            JsonStore.Delete(AppPaths.SessionFile(id));
        }
    }
}
