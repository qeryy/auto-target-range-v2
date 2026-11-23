// 灵枢桌面版 —— 环境自检
//
// 用户装好之后想知道「能不能用」，不该来问你。这里跑一遍关键链路，
// 每条给出「通过 / 警告 / 失败」和一句人话说明。
//
// 检查项：
//   1. 数据目录可写
//   2. 配置文件完整（config / models / experience）
//   3. 至少一个可用模型（有供应商 + 有密钥 + 已启用）
//   4. 技能库能加载（嵌入资源）
//   5. 内置工具齐全
//   6. 经验库可读
//   7. 日志目录可写
//   8. 网络可达模型端点（可选，会真的发请求）
//   9. 程序图标已嵌进 exe（缺了窗口/任务栏就是通用图标）
//
// 故意不检查「能不能解题」——那要真实任务，不是自检该干的事。

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpiritScout.Ui
{
    public enum CheckLevel
    {
        Pass,
        Warn,
        Fail,
    }

    public sealed class CheckItem
    {
        public string Name { get; set; }
        public CheckLevel Level { get; set; }
        public string Detail { get; set; }

        public CheckItem() { Name = ""; Detail = ""; }

        public string Mark
        {
            get
            {
                if (Level == CheckLevel.Pass) return "✓";
                if (Level == CheckLevel.Warn) return "!";
                return "✗";
            }
        }
    }

    public static class SelfCheck
    {
        /// <summary>跑一遍全部检查（不含网络）。</summary>
        public static List<CheckItem> RunOffline()
        {
            var items = new List<CheckItem>();

            // 0. 运行时（.NET Framework 版本）—— 最基础，放最前
            items.Add(CheckRuntime());

            // 1. 数据目录
            items.Add(CheckDataDir());

            // 2. 配置文件
            items.Add(CheckConfigs());

            // 3. 模型
            items.Add(CheckModels());

            // 4. 技能库
            items.Add(CheckSkills());

            // 5. 工具
            items.Add(CheckTools());

            // 6. 经验库
            items.Add(CheckExperience());

            // 7. 日志目录
            items.Add(CheckLogDir());

            // 8. 字体
            items.Add(CheckFonts());

            // 9. 损坏文件备份
            items.Add(CheckBrokenFiles());

            // 10. 程序图标（窗口/任务栏/托盘用的是嵌入资源里的那份）
            items.Add(CheckIcon());

            return items;
        }

        /// <summary>测一次真实的模型端点连通（会发 HTTP 请求）。</summary>
        public static async Task<CheckItem> CheckEndpointAsync(string modelName,
            CancellationToken ct = default(CancellationToken))
        {
            var item = new CheckItem { Name = "模型端点连通" };
            try
            {
                var reg = new Llm.ModelRegistry(Core.AppPaths.ModelsFile,
                    new Llm.SecretStore(Core.AppPaths.SecretsFile));
                if (string.IsNullOrEmpty(modelName)) modelName = FirstModel(reg);
                if (string.IsNullOrEmpty(modelName))
                {
                    reg.Dispose();
                    item.Level = CheckLevel.Warn;
                    item.Detail = "没有可用模型，跳过";
                    return item;
                }

                var reply = await reg.TestAsync(modelName).ConfigureAwait(false);
                reg.Dispose();

                // TestAsync 返回 KeyValuePair<bool,string>：Key=是否可用，Value=说明
                if (reply.Key)
                {
                    item.Level = CheckLevel.Pass;
                    item.Detail = modelName + " 响应正常：" + Core.Json.Clip(reply.Value, 60);
                }
                else
                {
                    item.Level = CheckLevel.Fail;
                    item.Detail = modelName + "：" + Core.Json.Clip(reply.Value, 120);
                }
            }
            catch (Exception ex)
            {
                item.Level = CheckLevel.Fail;
                item.Detail = ex.GetType().Name + ": " + ex.Message;
            }
            return item;
        }

        // ---------- 各项 ----------

        private static CheckItem CheckDataDir()
        {
            var item = new CheckItem { Name = "数据目录" };
            try
            {
                string root = Core.AppPaths.DataRoot;
                Directory.CreateDirectory(root);
                string probe = Path.Combine(root, ".write-probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                item.Level = CheckLevel.Pass;
                item.Detail = root + "（" + Core.AppPaths.RootKind + "，可写）";
            }
            catch (Exception ex)
            {
                item.Level = CheckLevel.Fail;
                item.Detail = "不可写：" + ex.Message;
            }
            return item;
        }

        private static CheckItem CheckConfigs()
        {
            var item = new CheckItem { Name = "配置文件" };
            var missing = new List<string>();
            if (!File.Exists(Core.AppPaths.ConfigFile)) missing.Add("config.json");
            if (!File.Exists(Core.AppPaths.ModelsFile)) missing.Add("models.json");
            if (!File.Exists(Core.AppPaths.ExperienceFile)) missing.Add("experience.json");

            if (missing.Count == 0)
            {
                item.Level = CheckLevel.Pass;
                item.Detail = "config / models / experience 都在";
            }
            else
            {
                item.Level = CheckLevel.Warn;
                item.Detail = "缺 " + string.Join("、", missing) + "（首次运行会自动创建）";
            }
            return item;
        }

        private static CheckItem CheckModels()
        {
            var item = new CheckItem { Name = "可用模型" };
            try
            {
                var reg = new Llm.ModelRegistry(Core.AppPaths.ModelsFile,
                    new Llm.SecretStore(Core.AppPaths.SecretsFile));
                int providers = 0, models = 0, ready = 0;
                var names = new List<string>();
                foreach (var p in reg.Providers()) providers++;
                foreach (var m in reg.Models())
                {
                    models++;
                    if (!m.Enabled) continue;
                    var p = reg.FindProvider(m.ProviderName);
                    if (p != null && reg.HasKey(p.Name))
                    {
                        ready++;
                        names.Add(m.Name);
                    }
                }
                reg.Dispose();

                if (ready > 0)
                {
                    item.Level = CheckLevel.Pass;
                    item.Detail = providers + " 个供应商 / " + models + " 个模型，可用："
                        + string.Join("、", names);
                }
                else if (models > 0)
                {
                    item.Level = CheckLevel.Fail;
                    item.Detail = "有 " + models + " 个模型但没有可用密钥——去「模型」页填 API Key";
                }
                else
                {
                    item.Level = CheckLevel.Fail;
                    item.Detail = "还没有模型——去「模型」页添加供应商和模型";
                }
            }
            catch (Exception ex)
            {
                item.Level = CheckLevel.Fail;
                item.Detail = ex.Message;
            }
            return item;
        }

        private static CheckItem CheckSkills()
        {
            var item = new CheckItem { Name = "技能库" };
            try
            {
                Stream stream = typeof(SelfCheck).Assembly.GetManifestResourceStream("skills.json");
                if (stream == null)
                {
                    // 开发态可能没嵌入，看磁盘
                    string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "skills.json");
                    if (File.Exists(local))
                    {
                        item.Level = CheckLevel.Warn;
                        item.Detail = "从磁盘读到（exe 未嵌入技能库，分发版不该这样）";
                        return item;
                    }
                    item.Level = CheckLevel.Fail;
                    item.Detail = "skills.json 既没嵌入也没在磁盘上（技能页会是空的）";
                    return item;
                }

                using (stream)
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    var dict = Core.Json.AsDict(Core.Json.Parse(reader.ReadToEnd()));
                    var arr = Core.Json.Array(dict, "skills");
                    int n = arr == null ? 0 : arr.Count;
                    if (n > 0)
                    {
                        item.Level = CheckLevel.Pass;
                        item.Detail = n + " 条技能（内嵌资源）";
                    }
                    else
                    {
                        item.Level = CheckLevel.Fail;
                        item.Detail = "技能库是空的";
                    }
                }
            }
            catch (Exception ex)
            {
                item.Level = CheckLevel.Fail;
                item.Detail = ex.Message;
            }
            return item;
        }

        private static CheckItem CheckIcon()
        {
            var item = new CheckItem { Name = "程序图标" };
            try
            {
                System.Drawing.Icon ico = null;
                using (var st = typeof(SelfCheck).Assembly.GetManifestResourceStream("icon.ico"))
                {
                    if (st != null) ico = new System.Drawing.Icon(st);
                }
                if (ico == null)
                {
                    item.Level = CheckLevel.Warn;
                    item.Detail = "exe 里没嵌 icon.ico —— 窗口/任务栏会退回系统通用图标。"
                        + "构建脚本要同时给 /win32icon（文件图标）和 /resource:assets\\icon.ico,icon.ico（运行时图标）";
                    return item;
                }
                item.Level = CheckLevel.Pass;
                item.Detail = "已嵌入清单资源，标题栏/任务栏/托盘气泡共用这一份";
            }
            catch (Exception ex)
            {
                item.Level = CheckLevel.Fail;
                item.Detail = ex.Message;
            }
            return item;
        }

        private static CheckItem CheckTools()
        {
            var item = new CheckItem { Name = "内置工具" };
            try
            {
                var reg = Tools.ToolRegistry.CreateDefault();
                var names = new List<string>();
                foreach (var t in reg.All) names.Add(t.Name);
                // 检查 tools JSON 能否生成（这条链断了模型就没法动手）
                string json = reg.BuildToolsJson();
                if (string.IsNullOrEmpty(json) || json.Length < 50)
                {
                    item.Level = CheckLevel.Fail;
                    item.Detail = "tools 定义生成失败（模型将无法调用工具）";
                    return item;
                }
                item.Level = CheckLevel.Pass;
                item.Detail = reg.Count + " 个：" + string.Join("、", names);
            }
            catch (Exception ex)
            {
                item.Level = CheckLevel.Fail;
                item.Detail = ex.Message;
            }
            return item;
        }

        private static CheckItem CheckExperience()
        {
            var item = new CheckItem { Name = "经验库" };
            try
            {
                var store = new Knowledge.ExperienceStore(Core.AppPaths.ExperienceFile);
                var all = store.All();
                item.Level = CheckLevel.Pass;
                item.Detail = all.Count == 0
                    ? "空的（跑完任务后可在「经验」页提炼）"
                    : all.Count + " 条";
            }
            catch (Exception ex)
            {
                item.Level = CheckLevel.Warn;
                item.Detail = "读取异常：" + ex.Message;
            }
            return item;
        }

        /// <summary>
        /// 损坏文件残留。
        ///
        /// `JsonStore.Read` 遇到解析失败时会**把文件改名成
        /// `xxx.json.broken-yyyyMMddHHmmss` 并回退默认值**（这个设计是对的：
        /// 坏文件不该让程序起不来，原件也要留证）。
        ///
        /// 但**这个动作对用户是静默的** —— 他只会发现「某条任务不见了」，
        /// 不知道为什么、也不知道原件还在。自检里报出来，至少让人知道
        /// 发生过什么、去哪找原件。
        ///
        /// 有备份说明**历史上出过事**，所以是 Warn 不是 Pass；
        /// 但文件已经备份好了、程序没坏，所以也不是 Fail。
        /// </summary>
        private static CheckItem CheckBrokenFiles()
        {
            var item = new CheckItem { Name = "损坏文件备份" };
            try
            {
                var dirs = new string[] { Core.AppPaths.DataRoot, Core.AppPaths.SessionsDir };
                var found = new List<string>();
                foreach (var dir in dirs)
                {
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                    // 两种后缀都要扫：
                    //   .broken-*     JSON 解析失败（JsonStore 改名）
                    //   .unreadable-* DPAPI 解不开（SecretStore 改名）
                    foreach (var pattern in new string[] { "*.broken-*", "*.unreadable-*" })
                    {
                        string[] files;
                        try { files = Directory.GetFiles(dir, pattern); }
                        catch { continue; }
                        foreach (var f in files)
                        {
                            string name = Path.GetFileName(f);
                            if (!found.Contains(name)) found.Add(name);
                        }
                    }
                }

                if (found.Count == 0)
                {
                    item.Level = CheckLevel.Pass;
                    item.Detail = "没有（文件都读得出来）";
                }
                else
                {
                    found.Sort(StringComparer.Ordinal);
                    string head = string.Join("、", found.GetRange(0, Math.Min(3, found.Count)).ToArray());
                    item.Level = CheckLevel.Warn;
                    item.Detail = found.Count + " 个无法读取的文件已备份："
                        + head + (found.Count > 3 ? " 等" : "")
                        + "（目录 " + Core.AppPaths.DataRoot + "）";
                }
            }
            catch (Exception ex)
            {
                item.Level = CheckLevel.Warn;
                item.Detail = "扫描失败：" + Core.Text.OneLine(ex.Message, 80);
            }
            return item;
        }

        /// <summary>
        /// .NET Framework 版本。
        ///
        /// 这个程序编译时用的是 4.8 的程序集。如果目标机只有 4.7.x，
        /// **exe 不一定会立刻报错** —— .NET 4.x 是同一个 4.0 CLR 的原地升级，
        /// 程序可能正常启动，然后在某处调到一个 4.8 才有的 API 时抛
        /// `MissingMethodException`，用户完全看不出是运行时版本的问题。
        ///
        /// 所以自检里查一次，把话说在前面。
        ///
        /// 版本号来自注册表 `NDP\v4\Full\Release`（DWORD）：
        ///   528040+ = 4.8 · 533320+ = 4.8.1 · 461808+ = 4.7.2 · 460798+ = 4.7
        /// </summary>
        private static CheckItem CheckRuntime()
        {
            var item = new CheckItem { Name = ".NET 运行时" };
            try
            {
                int release = 0;
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                {
                    if (key != null)
                    {
                        object v = key.GetValue("Release");
                        if (v is int) release = (int)v;
                    }
                }

                if (release <= 0)
                {
                    item.Level = CheckLevel.Warn;
                    item.Detail = "查不到 .NET 4.x 的版本记录（可能被精简过）；" 
                        + "程序能起来说明 CLR 在，但建议装完整的 .NET Framework 4.8";
                    return item;
                }

                string ver;
                if (release >= 533320) ver = "4.8.1 或更高";
                else if (release >= 528040) ver = "4.8";
                else if (release >= 461808) ver = "4.7.2";
                else if (release >= 460798) ver = "4.7";
                else if (release >= 394802) ver = "4.6.2";
                else ver = "4.6 或更低";

                if (release >= 528040)
                {
                    item.Level = CheckLevel.Pass;
                    item.Detail = ".NET Framework " + ver + "（Release " + release + "）";
                }
                else
                {
                    item.Level = CheckLevel.Warn;
                    item.Detail = "只有 .NET Framework " + ver + "（Release " + release + "），"
                        + "本程序按 4.8 编译；若某处报「找不到方法」请升级到 4.8";
                }
            }
            catch (Exception ex)
            {
                item.Level = CheckLevel.Warn;
                item.Detail = "读注册表失败：" + Core.Text.OneLine(ex.Message, 60);
            }
            return item;
        }

        private static CheckItem CheckLogDir()
        {
            var item = new CheckItem { Name = "日志目录" };
            try
            {
                string dir = Core.AppPaths.LogDir;
                Directory.CreateDirectory(dir);
                item.Level = CheckLevel.Pass;
                item.Detail = dir;
            }
            catch (Exception ex)
            {
                item.Level = CheckLevel.Warn;
                item.Detail = "不可写：" + ex.Message;
            }
            return item;
        }

        private static CheckItem CheckFonts()
        {
            var item = new CheckItem { Name = "界面字体" };
            try
            {
                string ui = Theme.UiFontName;
                string mono = Theme.MonoFontName;
                // 首选是「Microsoft YaHei UI」；退到别的说明系统缺中文字体
                bool preferred = ui.IndexOf("YaHei", StringComparison.OrdinalIgnoreCase) >= 0
                              || ui.IndexOf("JhengHei", StringComparison.OrdinalIgnoreCase) >= 0
                              || ui.IndexOf("PingFang", StringComparison.OrdinalIgnoreCase) >= 0
                              || ui.IndexOf("Source Han", StringComparison.OrdinalIgnoreCase) >= 0
                              || ui.IndexOf("Noto Sans CJK", StringComparison.OrdinalIgnoreCase) >= 0;

                item.Level = preferred ? CheckLevel.Pass : CheckLevel.Warn;
                item.Detail = "界面 " + ui + " / 等宽 " + mono
                    + (preferred ? "" : "（系统缺中文界面字体，已回退）");

                // 等宽字体也要能显示中文，否则技能正文会变成方块
                using (var probe = new System.Drawing.Bitmap(1, 1))
                using (var g = System.Drawing.Graphics.FromImage(probe))
                {
                    var monoFont = Theme.FontMono;
                    var size = g.MeasureString("中文", monoFont);
                    if (size.Width < 1)
                    {
                        item.Level = CheckLevel.Warn;
                        item.Detail += "；等宽字体可能不显示中文";
                    }
                }
            }
            catch (Exception ex)
            {
                item.Level = CheckLevel.Warn;
                item.Detail = ex.Message;
            }
            return item;
        }

        private static string FirstModel(Llm.ModelRegistry reg)
        {
            foreach (var m in reg.Models())
            {
                if (m.Enabled) return m.Name;
            }
            return "";
        }

        /// <summary>把检查结果拼成可复制的文本（贴给他人看）。</summary>
        public static string ToText(List<CheckItem> items, string appVersion)
        {
            var sb = new StringBuilder();
            sb.Append("灵枢 · SpiritScout 自检报告\n");
            sb.Append("版本 ").Append(appVersion).Append('\n');
            sb.Append("时间 ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append('\n');
            sb.Append("数据目录 ").Append(Core.AppPaths.DataRoot).Append('\n');
            sb.Append(new string('-', 50)).Append('\n');

            int pass = 0, warn = 0, fail = 0;
            foreach (var it in items)
            {
                sb.Append('[').Append(it.Mark).Append("] ").Append(it.Name)
                  .Append(" —— ").Append(it.Detail).Append('\n');
                if (it.Level == CheckLevel.Pass) pass++;
                else if (it.Level == CheckLevel.Warn) warn++;
                else fail++;
            }
            sb.Append(new string('-', 50)).Append('\n');
            sb.Append("通过 ").Append(pass).Append(" / 警告 ").Append(warn)
              .Append(" / 失败 ").Append(fail);
            return sb.ToString();
        }
    }
}
