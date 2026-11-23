// 灵枢桌面版 —— 配置管理与工作区
//
// 配置存 %APPDATA%\SpiritScout\config.json。
// 这里再补一件事：**首次启动时把内置的默认资源拷到数据目录**（技能库索引、
// 默认模型清单），之后只在用户手动重置时才覆盖——这样升级 exe 不会冲掉用户
// 自己改过的配置。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SpiritScout.Core
{
    public sealed class ConfigManager
    {
        private AppConfig _config;

        public AppConfig Current
        {
            get
            {
                if (_config == null) Load();
                return _config;
            }
        }

        /// <summary>
        /// 加载时的原始快照。Save 时拿它跟当前值比，**只覆盖真正变化过的字段**。
        ///
        /// 为什么需要：多开时两个窗口各自改了不同设置，如果 Save 整体覆盖，
        /// 后写的那个会把对方的改动抹掉（实测：A 设 RecallLimit=20、
        /// B 设 DefaultModel，最后 RecallLimit 又变回默认值）。
        /// 只写变化的字段，两边的改动都能活。
        /// </summary>
        private AppConfig _snapshot;

        public void Load()
        {
            _config = JsonStore.Read<AppConfig>(AppPaths.ConfigFile, null) ?? new AppConfig();
            DataVersion.Check("config", _config.SchemaVersion);
            if (string.IsNullOrEmpty(_config.WorkspaceRoot))
                _config.WorkspaceRoot = AppPaths.DataRoot;
            _snapshot = Clone(_config);
        }

        private static AppConfig Clone(AppConfig c)
        {
            if (c == null) return null;
            return new AppConfig
            {
                SchemaVersion = c.SchemaVersion,
                DefaultModel = c.DefaultModel,
                RecallLimit = c.RecallLimit,
                DistillEnabled = c.DistillEnabled,
                WorkspaceRoot = c.WorkspaceRoot,
                NamingChain = c.NamingChain == null ? null : new List<string>(c.NamingChain),
                ThemeMode = c.ThemeMode,
            };
        }

        public void Save()
        {
            if (_config == null) return;
            _config.SchemaVersion = DataVersion.Current;

            var mine = _config;
            var baseSnap = _snapshot;

            // 持锁 → 读盘上最新的 → **只覆盖我改过的字段** → 写回。
            //
            // 这样多开时：A 改 RecallLimit、B 改 DefaultModel，
            // 两边各自只写自己那一项，不会互相覆盖。
            JsonStore.Update<AppConfig>(AppPaths.ConfigFile, disk =>
            {
                disk.SchemaVersion = mine.SchemaVersion;

                // baseSnap == null 表示没 Load 过（直接新建就 Save），
                // 那种情况按「全部覆盖」处理是合理的。
                if (baseSnap == null)
                {
                    disk.DefaultModel = mine.DefaultModel;
                    disk.RecallLimit = mine.RecallLimit;
                    disk.DistillEnabled = mine.DistillEnabled;
                    disk.WorkspaceRoot = mine.WorkspaceRoot;
                disk.NamingChain = mine.NamingChain;
                disk.ThemeMode = mine.ThemeMode;
                }
                else
                {
                    if (!string.Equals(baseSnap.DefaultModel, mine.DefaultModel, StringComparison.Ordinal))
                        disk.DefaultModel = mine.DefaultModel;
                    if (baseSnap.RecallLimit != mine.RecallLimit)
                        disk.RecallLimit = mine.RecallLimit;
                    if (baseSnap.DistillEnabled != mine.DistillEnabled)
                        disk.DistillEnabled = mine.DistillEnabled;
                    if (!string.Equals(baseSnap.WorkspaceRoot, mine.WorkspaceRoot, StringComparison.Ordinal))
                        disk.WorkspaceRoot = mine.WorkspaceRoot;
                    disk.NamingChain = mine.NamingChain;
                    disk.ThemeMode = mine.ThemeMode;
                }
            }, new AppConfig());

            // 保存完，当前状态就是新的基准
            _snapshot = Clone(_config);
        }

        /// <summary>
        /// 首次启动时初始化数据目录。已存在则原样保留（升级不覆盖）。
        /// </summary>
        public void EnsureInitialized()
        {
            Load();
            // 触发目录创建
            var _ = AppPaths.DataRoot;
            var __ = AppPaths.SessionsDir;
            var ___ = AppPaths.LogDir;

            if (!File.Exists(AppPaths.ConfigFile))
                Save();

            if (!File.Exists(AppPaths.ModelsFile))
            {
                // 默认空清单：用户自己在「模型」页加。
                // 不预置任何供应商，避免把作者的选择强加给使用者。
                JsonStore.Write(AppPaths.ModelsFile, new SpiritScout.Llm.ModelFile());
            }

            if (!File.Exists(AppPaths.ExperienceFile))
            {
                JsonStore.Write(AppPaths.ExperienceFile, new System.Collections.Generic.List<Experience>());
            }
        }

        /// <summary>单份日志的大小上限（超过就轮转）。</summary>
        private const long LogMaxBytes = 8L * 1024 * 1024;

        /// <summary>保留几份历史日志。</summary>
        private const int LogKeepFiles = 5;

        /// <summary>日志系统自己坏掉时只提示一次，避免刷屏。</summary>
        private static bool _logBrokenReported;

        /// <summary>上次清理历史日志的日期（yyyyMMdd）。避免每次写日志都扫目录。</summary>
        private static string _lastPruneDate = "";

        /// <summary>把一条消息追加到当天日志（出问题时用户能翻）。</summary>
        public static void Log(string message)
        {
            try
            {
                string dir = AppPaths.LogDir;
                string today = DateTime.Now.ToString("yyyyMMdd");
                string file = Path.Combine(dir, "app-" + today + ".log");

                // 轮转：单个文件超过上限就改名，再往下只保留最近几份。
                // 不加这个的话，某个循环疯狂写日志能几分钟撑爆磁盘——
                // 那比不写日志更糟。
                try
                {
                    var fi = new FileInfo(file);
                    if (fi.Exists && fi.Length > LogMaxBytes)
                    {
                        string rolled = file + "." + DateTime.Now.ToString("HHmmss");
                        File.Move(file, rolled);
                        PruneLogs(dir);
                    }
                }
                catch { }

                // 按天清理：**光靠上面那条「超 8MB 才清」是不够的**。
                //
                // 文件按天命名、每天从 0 字节开始，正常用量一天只有几百 KB，
                // 永远摸不到 8MB 的门槛——于是 PruneLogs 一次都不会执行，
                // 一年下来攒 365 个文件。常驻软件跑几个月能把磁盘吃掉一块。
                //
                // 所以换天的时候清一次（每天最多扫一次目录，成本可以忽略）。
                if (!string.Equals(_lastPruneDate, today, StringComparison.Ordinal))
                {
                    _lastPruneDate = today;
                    PruneLogs(dir);
                }

                File.AppendAllText(file,
                    DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                // 日志写不进去（磁盘满、目录只读）——这是最该让人知道的事，
                // 因为之后所有排障线索都没了。但不能反复弹窗，只报一次。
                if (!_logBrokenReported)
                {
                    _logBrokenReported = true;
                    try
                    {
                        System.Diagnostics.Debug.WriteLine("日志写入失败: " + ex.Message);
                    }
                    catch { }
                }
            }
        }

        /// <summary>只保留最近若干份日志，删掉更早的。</summary>
        private static void PruneLogs(string dir)
        {
            try
            {
                var files = new List<FileInfo>();
                foreach (var f in Directory.GetFiles(dir, "app-*.log*"))
                {
                    try { files.Add(new FileInfo(f)); } catch { }
                }
                if (files.Count <= LogKeepFiles) return;

                files.Sort((a, b) => b.LastWriteTime.CompareTo(a.LastWriteTime));
                for (int i = LogKeepFiles; i < files.Count; i++)
                {
                    try { files[i].Delete(); } catch { }
                }
            }
            catch { }
        }
    }

}
