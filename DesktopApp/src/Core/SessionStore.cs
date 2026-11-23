// 灵枢桌面版 —— 会话存储（含消息）
//
// 一个会话一个文件：<id>.json，里面既有会话元信息也有消息列表。
// 这样打开一个会话只读一个文件，删会话也只删一个文件。
//
// 文件格式：
//   { "session": {...}, "messages": [ {...}, ... ] }
//
// ## 为什么要内存缓存
//
// 原来的 `Append` 每次都是「读全文件 → 反序列化 → 加一条 → 序列化 → 写回」。
// 1000 条消息的会话（317 KB）每追加一条要 20–30ms，而一次对话会产生
// 三次这样的写入（user / assistant / AddTokens）。
//
// 现在把最近用过的会话留在内存里，`Append` 只写盘不读盘。
// **写盘仍然立即执行**——宁可慢一点，也不冒「崩溃丢消息」的风险。
// 这点性能差距（十几毫秒）用户感知不到，但丢一条消息会很难受。
//
// 缓存很小（最多 8 个会话），换出去的时候不写盘（因为每次改动都已经写过了）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SpiritScout.Core
{
    /// <summary>会话文件的结构。</summary>
    public sealed class SessionFile
    {
        public Session Session { get; set; }
        public List<Message> Messages { get; set; }

        public SessionFile()
        {
            Session = new Session();
            Messages = new List<Message>();
        }
    }

    /// <summary>会话 + 消息的读写。</summary>
    public sealed class SessionStore
    {
        private readonly object _lock = new object();

        /// <summary>跨实例的写闸门（各页面各自 new SessionStore，实例锁不互斥）。</summary>
        private static readonly object WriteGate = new object();

        /// <summary>
        /// 跨**进程**的会话锁。
        ///
        /// `WriteGate` 只在同一个进程内有效。允许多开之后，两个窗口同时往同一个
        /// 会话里追加消息时，「读 → 改 → 写」会互相覆盖：
        ///
        /// ```
        /// 实测：A / B 各 Append 40 次，都报成功
        /// 结果：最终 44 条（期望 80），A 只有 4 条进了文件 —— 丢了 36 条
        /// ```
        ///
        /// 双方都「成功」是因为各自读到的都是自己那份缓存，后写的把前写的盖掉了。
        /// **静默丢数据比报错更危险**，所以这里用命名 Mutex 把整段操作串起来。
        ///
        /// 用 `Local\` 作用域（同一次登录会话内互斥）——跨用户同时开同一个数据目录
        /// 不是要支持的场景。
        /// </summary>
        private static IDisposable AcquireSessionLock(string sessionId)
        {
            string name = @"Local\SpiritScout.Session." + (sessionId ?? "").GetHashCode().ToString("X8");
            var mtx = new System.Threading.Mutex(false, name);
            bool got;
            try
            {
                // 等 5 秒。正常一次写盘是毫秒级，等这么久说明另一个进程卡住了。
                got = mtx.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (System.Threading.AbandonedMutexException)
            {
                // 上一个持有者崩了 —— 锁被遗弃，但我们拿到了，继续用
                got = true;
            }
            if (!got)
            {
                mtx.Dispose();
                throw new IOException("会话「" + sessionId + "」正被另一个窗口写入，等了 5 秒仍未释放。");
            }
            return new SessionLock(mtx);
        }

        /// <summary>把 Mutex 的释放包成 IDisposable，方便 using。</summary>
        private sealed class SessionLock : IDisposable
        {
            private System.Threading.Mutex _m;
            public SessionLock(System.Threading.Mutex m) { _m = m; }
            public void Dispose()
            {
                if (_m == null) return;
                try { _m.ReleaseMutex(); } catch { }
                try { _m.Dispose(); } catch { }
                _m = null;
            }
        }

        /// <summary>最近用过的会话（id → 内容）。只在内存里，改动都会立刻写盘。</summary>
        private static readonly Dictionary<string, SessionFile> _cache =
            new Dictionary<string, SessionFile>(StringComparer.OrdinalIgnoreCase);

        /// <summary>缓存上限。超过就按插入顺序丢最旧的（改动早已落盘，丢了没损失）。</summary>
        private const int CacheLimit = 8;

        // ---------- 列表缓存 ----------
        //
        // List() 要读所有会话文件。500 个会话实测 164ms——单次能接受，
        // 但任务页 / 仪表盘 / 用量页各自刷新一次就是三份 164ms，
        // 用户在几个页面之间来回切就会一直重读。
        //
        // 加一个 1 秒的短缓存：同一秒内多次 List() 复用同一份结果。
        // 任何写操作（Save / SaveSession / Delete）都会立刻作废缓存，
        // 所以「删了会话马上刷新」仍然是准的。
        private static List<Session> _listCache;
        private static DateTime _listCacheAt;
        private static readonly object ListLock = new object();

        /// <summary>作废列表缓存（写操作后必须调）。</summary>
        private static void InvalidateList()
        {
            lock (ListLock) { _listCache = null; }
            // 索引文件也作废 —— 下次 List() 重建。删失败不要紧（旧索引会被校验挡下）。
            try
            {
                string idx = IndexPath;
                if (System.IO.File.Exists(idx)) System.IO.File.Delete(idx);
            }
            catch { }
        }

        /// <summary>
        /// 会话元信息索引文件的路径。
        ///
        /// 2026-11-20 审计：`List()` 原来对**每个会话**都 `JsonStore.Read&lt;SessionFile&gt;`，
        /// 而会话文件里连全部消息一起存着 —— 列表页只要标题和时间，却把几百条消息
        /// 也反序列化了一遍。1000 个会话 × 100 条消息 = 解析 10 万条消息。
        ///
        /// 现在多存一份「只有元信息」的索引：
        ///   · 它是**纯缓存**，删了、坏了都不影响数据，下次自动重建
        ///   · 读到索引后仍校验每个 `.json` 存在，防止手工删文件后索引残留
        ///   · 任何写操作（Save/Delete/Rename）都走 InvalidateList 把它删掉
        /// </summary>
        private static string IndexPath
        {
            get { return System.IO.Path.Combine(AppPaths.SessionsDir, "_index.json"); }
        }

        /// <summary>索引文件的载荷。带版本号，将来改结构可以据此丢弃旧索引。</summary>
        public sealed class SessionIndex
        {
            public int Version { get; set; }
            public List<Session> Sessions { get; set; }
        }

        public List<Session> List()
        {
            lock (ListLock)
            {
                // 1 秒缓存太短：频繁切页时每次还是读索引文件。
                // 5 秒之内没写操作说明会话列表没动 —— 可以直接用缓存。
                // 写操作（Save/Delete/Rename）都走 InvalidateList()，不会读到陈数据。
                if (_listCache != null && (DateTime.Now - _listCacheAt).TotalMilliseconds < 5000)
                    return new List<Session>(_listCache);   // 返回副本，防调用方排序/改元素影响缓存
            }

            // ---------- 第二档：读索引文件（不解析任何消息）----------
            var fromIndex = TryReadIndex();
            if (fromIndex != null)
            {
                lock (ListLock) { _listCache = fromIndex; _listCacheAt = DateTime.Now; }
                return new List<Session>(fromIndex);
            }

            // ---------- 第三档：全量扫描（首次 / 索引失效）----------
            var result = new List<Session>();
            try
            {
                foreach (var file in Directory.GetFiles(AppPaths.SessionsDir, "*.json"))
                {
                    // 索引文件本身不是会话
                    if (string.Equals(System.IO.Path.GetFileName(file), "_index.json",
                            StringComparison.OrdinalIgnoreCase)) continue;
                    // 列表页只要元信息，但文件里连消息一起存着——直接读整份再取 Session。
                    var sf = JsonStore.Read<SessionFile>(file, null);
                    if (sf == null || sf.Session == null) continue;

                    // 和 Load() 一样校验模式。**这不是重复代码，是两条独立读取路径**：
                    // 首页列表每条都要显示模式，不修的话非法值会让 Label 抛异常。
                    // （实测：只在 Load 里加了校验，List 这条路径照样崩。）
                    if (!RunModeText.IsValid(sf.Session.Mode))
                    {
                        int bad = (int)sf.Session.Mode;
                        sf.Session.Mode = RunMode.Ctf;
                        ConfigManager.Log("会话 " + sf.Session.Id + " 的 Mode 非法（" + bad
                            + "），列表中按 ctf 显示");
                    }
                    if (!PhaseText.IsValid(sf.Session.Phase))
                    {
                        int badP = (int)sf.Session.Phase;
                        sf.Session.Phase = SessionPhase.Pending;
                        ConfigManager.Log("会话 " + sf.Session.Id + " 的 Phase 非法（" + badP
                            + "），列表中按 pending 显示");
                    }
                    result.Add(sf.Session);
                }
            }
            catch (Exception ex)
            {
                ConfigManager.Log("列举会话失败：" + ex.GetType().Name + ": " + ex.Message);
            }
            result.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));

            lock (ListLock)
            {
                _listCache = result;
                _listCacheAt = DateTime.Now;
            }
            WriteIndex(result);
            return new List<Session>(result);
        }

        /// <summary>
        /// 读索引。**任何异常都返回 null**（回退全量扫描）—— 索引坏了不能让列表打不开。
        ///
        /// 校验两件事：版本对得上、每个会话的 `.json` 确实还在
        /// （用户可能在资源管理器里手工删了文件）。
        /// </summary>
        private static List<Session> TryReadIndex()
        {
            try
            {
                string idx = IndexPath;
                if (!System.IO.File.Exists(idx)) return null;

                var payload = JsonStore.Read<SessionIndex>(idx, null);
                if (payload == null || payload.Version != IndexVersion ||
                    payload.Sessions == null) return null;

                // 校验文件还在。stat 一次比读完整文件快得多（1000 个约 10ms）。
                var ok = new List<Session>(payload.Sessions.Count);
                foreach (var se in payload.Sessions)
                {
                    if (se == null || string.IsNullOrEmpty(se.Id)) continue;
                    if (!System.IO.File.Exists(AppPaths.SessionFile(se.Id))) continue;
                    ok.Add(se);
                }
                // 全都对不上说明索引是陈的（文件都搬走了），当作没有
                if (ok.Count == 0 && payload.Sessions.Count > 0) return null;
                ok.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
                return ok;
            }
            catch (Exception ex)
            {
                ConfigManager.Log("读会话索引失败：" + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>写索引。**失败不影响功能**（下次还是全量扫描，只是慢一点）。</summary>
        private static void WriteIndex(List<Session> sessions)
        {
            try
            {
                var payload = new SessionIndex
                {
                    Version = IndexVersion,
                    Sessions = sessions,
                };
                JsonStore.Write(IndexPath, payload);
            }
            catch (Exception ex)
            {
                // 索引是纯缓存，写失败不影响功能 —— 但要留痕，否则「为什么还是慢」
                // 会变成无解的谜。这条日志只在真出问题时出现。
                ConfigManager.Log("写会话索引失败：" + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>索引结构版本。改了 SessionIndex 的字段就 +1，旧索引自动作废。</summary>
        private const int IndexVersion = 1;


        /// <summary>
        /// 直接从磁盘读，**绕过内存缓存**。
        ///
        /// 跨进程锁只保证「不会同时写」，但保证不了「读到的是对方刚写的内容」——
        /// 缓存里可能是本进程几分钟前的旧快照。持锁后必须读盘，
        /// 否则就是「A 拿着旧数据覆盖 B」。
        ///
        /// 实测（不加这个）：A / B 各 Append 40 次，都成功，最终只有 41 条。
        /// </summary>
        public SessionFile LoadFresh(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return null;
            lock (_cache) { _cache.Remove(sessionId); }
            return Load(sessionId);
        }

        public SessionFile Load(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return null;

            lock (_cache)
            {
                SessionFile hit;
                if (_cache.TryGetValue(sessionId, out hit)) return hit;
            }

            string file = AppPaths.SessionFile(sessionId);
            var sf = JsonStore.Read<SessionFile>(file, null);
            if (sf == null) return null;

            // ---------- 软损坏检测 ----------
            //
            // 硬损坏（乱码、类型错）会让 JavaScriptSerializer 抛异常，
            // JsonStore.Read 那边已经改名备份 + 记日志了。
            //
            // 但**截断的文件它不抛**——`{"Session": {"Id": "x"` 这种会被宽松解析成
            // 一个「半个对象」：Id 有值，消息全空。于是：
            //   · 用户打开会话 → 看到空的 → 以为消息丢了
            //   · 文件没备份 → 事后想找原件也没了
            //
            // 这里补一道粗检：**文件不小但读不出消息**，说明大概率是截断的。
            // 真·空会话的文件只有几十字节（只有元信息），所以用 200 字节做门槛。
            try
            {
                // 判断「文件是不是完整的 JSON」：数 { 和 } 是否平衡。
                //
                // 试过两种更简单的做法，都不行：
                //   · 看文件大小 —— 不可靠（空会话就有 240 字节）
                //   · 看末尾是不是 `}` —— 也不可靠（截断点可能正好落在
                //     某条消息对象的 `}` 后面，看起来是闭合的。实测就撞上了）
                //
                // 括号平衡是最直接的结构判据：截断的文件必然少几个闭括号。
                // 字符串里的花括号会干扰计数，但会话文件里 `{` `}` 主要出现在
                // 结构位置，正文里出现一般也成对（flag{...}），所以整体平衡性
                // 仍是可用的粗略指标。
                bool balanced = true;
                try
                {
                    string whole = File.ReadAllText(file, Encoding.UTF8);
                    int depth = 0;
                    foreach (char ch in whole)
                    {
                        if (ch == '{') depth++;
                        else if (ch == '}')
                        {
                            depth--;
                            if (depth < 0) { balanced = false; break; }
                        }
                    }
                    if (depth != 0) balanced = false;
                }
                catch { balanced = true; }   // 读不了就当正常，别误报

                bool looksTruncated = !balanced && sf.Session != null;

                if (looksTruncated)
                {
                    string backup = file + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                    try { File.Copy(file, backup, true); } catch { }
                    long sz = 0;
                    try { sz = new FileInfo(file).Length; } catch { }
                    ConfigManager.Log("会话文件疑似截断（" + sz + " 字节但读不出消息），已备份为 "
                        + Path.GetFileName(backup) + "：" + sessionId);
                }
            }
            catch { }

            // 模式校验：文件可能被手工改过、或从更老的版本带过来，
            // 反序列化会把 "Mode": 999 原样读成 (RunMode)999。
            // Label / ToValue 对未知值抛异常（开发期保护），所以这里必须先修正。
            if (sf.Session != null && !RunModeText.IsValid(sf.Session.Mode))
            {
                int bad = (int)sf.Session.Mode;
                sf.Session.Mode = RunMode.Ctf;
                ConfigManager.Log("会话 " + sessionId + " 的 Mode 非法（" + bad + "），已按 ctf 处理");
            }
            // 阶段同理：Label(非法值) 会抛，状态栏/列表都要显示它
            if (sf.Session != null && !PhaseText.IsValid(sf.Session.Phase))
            {
                int badP = (int)sf.Session.Phase;
                sf.Session.Phase = SessionPhase.Pending;
                ConfigManager.Log("会话 " + sessionId + " 的 Phase 非法（" + badP + "），已按 pending 处理");
            }

            // 版本检查（只在第一次读盘时做，缓存命中时不重复记日志）
            if (sf.Session != null) DataVersion.Check("session " + sessionId, sf.Session.SchemaVersion);
            if (sf.Messages == null) sf.Messages = new List<Message>();

            PutCache(sessionId, sf);
            return sf;
        }

        private static void PutCache(string id, SessionFile sf)
        {
            lock (_cache)
            {
                if (_cache.Count >= CacheLimit && !_cache.ContainsKey(id))
                {
                    // 简单策略：丢掉第一个（Dictionary 不保证顺序，
                    // 但这里只是控内存，丢谁都一样——每个改动都已落盘）
                    foreach (var k in new List<string>(_cache.Keys))
                    {
                        _cache.Remove(k);
                        break;
                    }
                }
                _cache[id] = sf;
            }
        }

        public void Save(SessionFile sf)
        {
            if (sf == null || sf.Session == null || string.IsNullOrEmpty(sf.Session.Id)) return;
            InvalidateList();
            using (AcquireSessionLock(sf.Session.Id))
            lock (WriteGate)
            {
                sf.Session.UpdatedAt = DateTime.Now;
                sf.Session.MessageCount = sf.Messages == null ? 0 : sf.Messages.Count;
                sf.Session.SchemaVersion = DataVersion.Current;
                JsonStore.Write(AppPaths.SessionFile(sf.Session.Id), sf);
                PutCache(sf.Session.Id, sf);
            }
        }

        public void SaveSession(Session s)
        {
            if (s == null || string.IsNullOrEmpty(s.Id)) return;
            var sf = Load(s.Id) ?? new SessionFile { Session = s };
            sf.Session = s;
            Save(sf);
        }

        /// <summary>
        /// 追加一条消息、同时把这轮消耗的 token 累加进去，**只落盘一次**。
        ///
        /// 原来 AgentLoop 是 `Append(...)` 紧跟 `AddTokens(...)`——两次完整读写。
        /// 会话 1000 条（234KB）时每次 17ms，两份就是 34ms。合起来只写一次。
        /// </summary>
        public Message AppendWithTokens(string sessionId, string role, string content,
            int tokens, string name = "")
        {
            using (AcquireSessionLock(sessionId))
            lock (WriteGate)
            {
                // 持锁后**必须读盘**，不能用缓存——缓存可能是别的窗口写入前的旧快照
                var sf = LoadFresh(sessionId);
                if (sf == null) return null;
                var msg = new Message
                {
                    SessionId = sessionId,
                    Role = role,
                    Content = content ?? "",
                    Name = name ?? "",
                    CreatedAt = DateTime.Now,
                };
                sf.Messages.Add(msg);
                if (tokens > 0) sf.Session.Tokens += tokens;
                Save(sf);
                return msg;
            }
        }

        /// <summary>追加一条消息并落盘。</summary>
        public Message Append(string sessionId, string role, string content, string name = "")
        {
            // 「读 → 改 List → 写」必须整体原子。
            // 只锁 Save 不够：两个线程可能同时 Load 到同一份缓存对象，
            // 同时往同一个 List 里 Add —— 那是 InvalidOperationException「集合已修改」。
            //
            // 而且进程内锁管不到另一个窗口，所以外面再套一层跨进程互斥。
            using (AcquireSessionLock(sessionId))
            lock (WriteGate)
            {
                var sf = LoadFresh(sessionId);
                if (sf == null) return null;
                var msg = new Message
                {
                    SessionId = sessionId,
                    Role = role,
                    Content = content ?? "",
                    Name = name ?? "",
                    CreatedAt = DateTime.Now,
                };
                sf.Messages.Add(msg);
                Save(sf);
                return msg;
            }
        }

        /// <summary>子会话 id 的前缀规则（与 Orchestrator 里建子会话的写法一致）。</summary>
        private const string SubSessionMark = "-sub-";

        public void Delete(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            InvalidateList();
            lock (_cache) { _cache.Remove(sessionId); }
            JsonStore.Delete(AppPaths.SessionFile(sessionId));

            // 级联删子会话。
            //
            // 委派给子 agent 时会在 `主会话id-sub-<agentKey>` 下建独立会话文件
            // （`Orchestrator.DispatchAsync` 里这么做的，避免污染主上下文）。
            // 但如果只删主会话，那 4 个子会话就成了孤儿——会话列表里堆着
            // 「子：ctf_web」这种条目，用户不知道是什么，也不知道从哪来的。
            //
            // 这里按前缀扫一遍。命名规则是 Orchestrator 定的，数据层本来不该知道它，
            // 但「删主带子」这件事只有数据层能一次做完（页面层调一次 Delete 就够）。
            try
            {
                string prefix = sessionId + SubSessionMark;
                foreach (var file in Directory.GetFiles(AppPaths.SessionsDir, "*.json"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    lock (_cache) { _cache.Remove(name); }
                    JsonStore.Delete(file);
                }
            }
            catch (Exception ex)
            {
                ConfigManager.Log("级联删除子会话失败: " + ex.Message);
            }
        }

        /// <summary>统计该会话累计 token（写进会话元信息，列表页直接读）。</summary>
        public void AddTokens(string sessionId, int tokens)
        {
            if (tokens <= 0) return;
            lock (WriteGate)
            {
                var sf = LoadFresh(sessionId);
                if (sf == null) return;
                sf.Session.Tokens += tokens;
                Save(sf);
            }
        }
    }
}
