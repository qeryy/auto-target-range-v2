// 灵枢桌面版 —— 页面契约与注册表
//
// 「加一个页面」应该是写一个文件 + 注册一行，而不是去主窗口里改 switch。
// 这个文件就是那条规矩的载体。

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SpiritScout.Ui
{
    /// <summary>侧栏分组。</summary>
    public enum PageGroup
    {
        Main = 0,       // 任务、仪表盘、用量
        Agent = 1,      // 编队、模型、项目
        Knowledge = 2,  // 经验、技能、MCP
        System = 3,     // 设置、关于
    }

    /// <summary>一页。</summary>
    public interface IPage
    {
        /// <summary>稳定标识（导航高亮、状态恢复都靠它）。</summary>
        string Id { get; }

        /// <summary>侧栏显示名。</summary>
        string Title { get; }

        PageGroup Group { get; }

        /// <summary>首次进入时构建控件（只调一次）。</summary>
        Control CreateView();

        /// <summary>每次进入或点刷新时拉数据。自己处理异常并显示在页面上。</summary>
        Task RefreshAsync();
    }

    /// <summary>
    /// 侧栏图标。
    ///
    /// 没做成 `IPage.Icon`，是因为那会让每个页面都多实现一个成员——加页面本来是
    /// 「写一个文件 + 注册一行」，不该因为加个图标就变成两处必填。
    /// 这里用 id → 符号的映射，页面注册时**可选**补一行；没配的就留空。
    ///
    /// 用的是 Unicode 符号（系统字体自带），不引图标资源文件——
    /// 那样既能一眼分出层次，又不用维护一套 png/ico。
    /// </summary>
    public static class PageIcons
    {
        private static readonly Dictionary<string, string> Map =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // 每一项符号都不同——重复了就看不出区别，等于没有图标
            { "home",       "\u25A4" },   // ▤ 任务列表
            { "dash",       "\u25C8" },   // ◈ 仪表盘
            { "usage",      "\u25A6" },   // ▦ 用量
            { "agents",     "\u2691" },   // ⚑ 编队（旗帜）
            { "models",     "\u25CE" },   // ◎ 模型
            { "projects",   "\u2637" },   // ☷ 项目（分组）
            { "exp",        "\u2726" },   // ✦ 经验（星）
            { "skills",     "\u25CA" },   // ◊ 技能（菱形）
            { "mcp",        "\u21C4" },   // ⇄ MCP 接入（交换）
            { "localtools", "\u2692" },   // ⚒ 本地工具（锤）
            { "notify",     "\u2709" },   // ✉ 通知（信封）
            { "settings",   "\u2699" },   // ⚙ 设置（齿轮）
            { "about",      "\u24D8" },   // ⓘ 关于
        };

        /// <summary>取图标；没有就返回空串（不显示）。</summary>
        public static string Get(string pageId)
        {
            if (string.IsNullOrEmpty(pageId)) return "";
            string v;
            return Map.TryGetValue(pageId, out v) ? v : "";
        }
    }

    /// <summary>页面可用的共享上下文：拿服务、切页、弹提示。</summary>
    public sealed class PageContext
    {
        private readonly Action<string, bool> _toast;

        public AppServices Services { get; private set; }
        public Form Shell { get; private set; }
        public Action<string> Navigate { get; set; }

        public PageContext(AppServices services, Form shell, Action<string, bool> toast)
        {
            Services = services;
            Shell = shell;
            _toast = toast;
        }

        public void Info(string message)
        {
            if (_toast != null) _toast(message, true);
        }

        public void Warn(string message)
        {
            if (_toast != null) _toast(message, false);
        }

        public void Error(string what, Exception ex)
        {
            if (_toast != null)
            {
                _toast(what + "失败：" + Shorten(ex.Message, 150), false);
            }
            else
            {
                MessageBox.Show(Shell, what + "失败：\n\n" + ex.Message, "灵枢",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static string Shorten(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "未知错误";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }
    }

    /// <summary>
    /// 全局服务容器。页面需要的后端能力都从这里取。
    /// 加新服务时在这里加一个属性即可，老页面不受影响。
    /// </summary>
    public sealed class AppServices
    {
        public Core.SessionRepository Sessions { get; set; }
        public Core.AppConfig Config { get; set; }

        /// <summary>服务是否就绪（模型可用等）。</summary>
        public bool Ready { get; set; }

        public string Version { get; set; }

        public AppServices()
        {
            Version = Core.AppInfo.Version;   // 唯一来源见 Core\AppInfo.cs
        }
    }

    /// <summary>页面注册表。</summary>
    public static class PageRegistry
    {
        private static readonly List<IPage> Pages = new List<IPage>();

        public static void Register(IPage page)
        {
            if (page == null) throw new ArgumentNullException("page");
            foreach (var p in Pages)
            {
                if (p.Id == page.Id)
                    throw new InvalidOperationException("页面 Id 重复：" + page.Id);
            }
            Pages.Add(page);
        }

        public static void Unregister(string id)
        {
            for (int i = Pages.Count - 1; i >= 0; i--)
            {
                if (Pages[i].Id == id) Pages.RemoveAt(i);
            }
        }

        public static IList<IPage> All { get { return Pages.AsReadOnly(); } }

        public static IPage Find(string id)
        {
            foreach (var p in Pages)
            {
                if (p.Id == id) return p;
            }
            return null;
        }

        public static List<IPage> ByGroup(PageGroup group)
        {
            var list = new List<IPage>();
            foreach (var p in Pages)
            {
                if (p.Group == group) list.Add(p);
            }
            return list;
        }

        /// <summary>清空（测试用）。</summary>
        public static void Clear()
        {
            Pages.Clear();
        }
    }
}
